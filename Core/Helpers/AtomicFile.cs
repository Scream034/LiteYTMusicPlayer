using System.Text;

namespace LMP.Core.Helpers;

/// <summary>
/// Обеспечивает отказоустойчивые атомарные операции записи и чтения файлов с защитой от повреждения данных
/// при аварийном завершении процесса или отключении питания (Torn Write Protection).
/// </summary>
public static class AtomicFile
{
    private const int DefaultBufferSize = 4096;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    #region Write Synchronous

    /// <summary>
    /// Атомарно записывает строковый контент в файл в кодировке UTF-8.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="content">Строковые данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>) перед перезаписью.</param>
    /// <param name="encoding">Кодировка текста (по умолчанию UTF-8 без BOM).</param>
    public static void WriteText(
        string targetPath,
        string content,
        bool createBackup = false,
        Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetBytes(content);
        WriteBytes(targetPath, bytes, createBackup);
    }

    /// <summary>
    /// Атомарно записывает массив байт в целевой файл с принудительным сбросом буфера на диск.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="bytes">Бинарные данные.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    public static void WriteBytes(
        string targetPath,
        ReadOnlySpan<byte> bytes,
        bool createBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(targetPath);

        var tempPath = string.Concat(targetPath, ".tmp");
        var backupPath = string.Concat(targetPath, ".bak");

        try
        {
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (createBackup && File.Exists(targetPath))
            {
                try
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AtomicFile] Failed to create backup copy for '{targetPath}': {ex.Message}");
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    #endregion

    #region Write Asynchronous

    /// <summary>
    /// Асинхронно и атомарно записывает строковый контент в файл.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="content">Строковые данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    /// <param name="encoding">Кодировка текста (по умолчанию UTF-8 без BOM).</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task WriteTextAsync(
        string targetPath,
        string content,
        bool createBackup = false,
        Encoding? encoding = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetBytes(content);
        await WriteBytesAsync(targetPath, bytes, createBackup, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Асинхронно и атомарно записывает бинарные данные в файл.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="bytes">Бинарные данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task WriteBytesAsync(
        string targetPath,
        ReadOnlyMemory<byte> bytes,
        bool createBackup = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(targetPath);

        var tempPath = string.Concat(targetPath, ".tmp");
        var backupPath = string.Concat(targetPath, ".bak");

        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            if (createBackup && File.Exists(targetPath))
            {
                try
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AtomicFile] Failed to create backup copy for '{targetPath}': {ex.Message}");
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    #endregion

    #region Read with Fallback Synchronous

    /// <summary>
    /// Считывает текст из целевого файла. При его повреждении, нулевом размере или отсутствии
    /// автоматически считывает резервную копию (<c>.bak</c>) и восстанавливает основной файл.
    /// </summary>
    /// <param name="targetPath">Путь к файлу.</param>
    /// <param name="recoveredFromBackup">Возвращает <c>true</c>, если данные были восстановлены из <c>.bak</c>.</param>
    /// <param name="encoding">Кодировка текста.</param>
    /// <returns>Содержимое файла или <c>null</c>, если ни основной файл, ни бэкап прочитать не удалось.</returns>
    public static string? ReadTextWithFallback(
        string targetPath,
        out bool recoveredFromBackup,
        Encoding? encoding = null)
    {
        recoveredFromBackup = false;
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        var enc = encoding ?? Utf8NoBom;
        var backupPath = string.Concat(targetPath, ".bak");

        // 1. Попытка чтения основного файла
        if (File.Exists(targetPath))
        {
            try
            {
                var fi = new FileInfo(targetPath);
                if (fi.Length > 0)
                {
                    var text = File.ReadAllText(targetPath, enc);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AtomicFile] Primary file read failed for '{targetPath}': {ex.Message}");
            }
        }

        // 2. Fallback на резервную копию
        if (File.Exists(backupPath))
        {
            try
            {
                var fi = new FileInfo(backupPath);
                if (fi.Length > 0)
                {
                    var backupText = File.ReadAllText(backupPath, enc);
                    if (!string.IsNullOrWhiteSpace(backupText))
                    {
                        recoveredFromBackup = true;
                        TryAutoHeal(backupPath, targetPath);
                        Log.Warn($"[AtomicFile] ⚠️ Successfully recovered '{targetPath}' from backup (.bak)");
                        return backupText;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AtomicFile] Backup file read failed for '{backupPath}': {ex.Message}");
            }
        }

        return null;
    }

    #endregion

    #region Read with Fallback Asynchronous

    /// <summary>
    /// Асинхронно считывает текст из файла с автоматическим восстановлением из <c>.bak</c> при сбое.
    /// </summary>
    /// <param name="targetPath">Путь к файлу.</param>
    /// <param name="encoding">Кодировка текста.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Кортеж из прочитанного содержимого и флага восстановления из бэкапа.</returns>
    public static async Task<(string? Content, bool RecoveredFromBackup)> ReadTextWithFallbackAsync(
        string targetPath,
        Encoding? encoding = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (null, false);

        var enc = encoding ?? Utf8NoBom;
        var backupPath = string.Concat(targetPath, ".bak");

        // 1. Попытка чтения основного файла
        if (File.Exists(targetPath))
        {
            try
            {
                var fi = new FileInfo(targetPath);
                if (fi.Length > 0)
                {
                    var text = await File.ReadAllTextAsync(targetPath, enc, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                        return (text, false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[AtomicFile] Primary file read failed for '{targetPath}': {ex.Message}");
            }
        }

        // 2. Fallback на резервную копию
        if (File.Exists(backupPath))
        {
            try
            {
                var fi = new FileInfo(backupPath);
                if (fi.Length > 0)
                {
                    var backupText = await File.ReadAllTextAsync(backupPath, enc, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(backupText))
                    {
                        TryAutoHeal(backupPath, targetPath);
                        Log.Warn($"[AtomicFile] ⚠️ Successfully recovered '{targetPath}' from backup (.bak)");
                        return (backupText, true);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Error($"[AtomicFile] Backup file read failed for '{backupPath}': {ex.Message}");
            }
        }

        return (null, false);
    }

    #endregion

    #region Helpers

    private static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryAutoHeal(string sourceBackupPath, string targetPath)
    {
        try
        {
            File.Copy(sourceBackupPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AtomicFile] Auto-heal copy failed for '{targetPath}': {ex.Message}");
        }
    }

    #endregion
}