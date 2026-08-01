namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Единая точка управления версией плеера и base.js.
/// Singleton, потокобезопасный.
/// </summary>
/// <param name="http">HTTP-клиент для загрузки скриптов плеера.</param>
public class PlayerContextManager(HttpClient http)
{
    private readonly HttpClient _http = http;

    /// <summary>
    /// Семафор single-flight: гарантирует один активный <see cref="PlayerContext.DetectVersionAsync"/>
    /// при любом количестве параллельных вызовов <see cref="GetOrLoadAsync"/>.
    /// </summary>
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    private volatile PlayerContext? _current;
    private volatile string? _cachedSignatureTimestamp;

    /// <summary>
    /// Возвращает актуальный контекст плеера.
    /// При отсутствии валидного in-memory контекста последовательно проверяет
    /// дисковый кэш и выполняет сетевую загрузку.
    /// При недоступности сети делает fallback на дисковый кэш.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="InvalidOperationException">
    /// Не удалось определить версию плеера или загрузить base.js.
    /// </exception>
    public virtual async Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        // Fast path: volatile read без захвата семафора
        var current = _current;
        if (current?.IsValid() == true) return current;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _current;
            if (current?.IsValid() == true) return current;

            // Попытка определить версию через сеть
            (string Version, string[] Urls)? versionInfo;
            try
            {
                versionInfo = await PlayerContext.DetectVersionAsync(_http, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Внутренний таймаут HttpClient (не отмена вызывающего кода)
                versionInfo = null;
            }

            // Сеть недоступна → fallback на дисковый кэш
            if (versionInfo is not { } versionData)
            {
                var diskFallback = TryLoadFromDiskCache();
                if (diskFallback != null)
                {
                    _current = diskFallback;
                    return diskFallback;
                }

                throw new InvalidOperationException("Failed to detect player version");
            }

            string version = versionData.Version;
            string[] urls = versionData.Urls;

            var cached = PlayerContext.LoadFromCache(version);
            if (cached is not null)
            {
                _current = cached;
                Log.Debug($"[PlayerContextManager] Loaded from cache: {version}");
                return cached;
            }

            foreach (var url in urls)
            {
                try
                {
                    Log.Debug($"[PlayerContextManager] Downloading: {url}");
                    var baseJs = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var newContext = new PlayerContext(version, baseJs);
                    await newContext.SaveCacheAsync().ConfigureAwait(false);
                    _current = newContext;
                    Log.Info($"[PlayerContextManager] Loaded fresh: {version} ({baseJs.Length / 1024}KB)");
                    return newContext;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Debug($"[PlayerContextManager] Download failed ({url}): {ex.Message}");
                }
            }

            var lastResort = TryLoadFromDiskCache();
            if (lastResort != null)
            {
                _current = lastResort;
                return lastResort;
            }

            throw new InvalidOperationException("Failed to download base.js from all candidate URLs");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Сканирует дисковый кэш и загружает наиболее свежий валидный контекст
    /// без обращения к сети. Используется как fallback при недоступности
    /// <see cref="PlayerContext.DetectVersionAsync"/>.
    /// </summary>
    /// <returns>Контекст плеера или <c>null</c> если валидный кэш отсутствует.</returns>
    public static PlayerContext? TryLoadFromDiskCache()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return null;

            var stsFiles = Directory.GetFiles(dir, "player_*_sts.txt");
            if (stsFiles.Length == 0) return null;

            // Свежайший первым — максимизируем вероятность валидного STS
            Array.Sort(stsFiles, static (a, b) =>
                File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

            const string prefix = "player_";
            const string suffix = "_sts.txt";

            for (int i = 0; i < stsFiles.Length; i++)
            {
                var fileName = Path.GetFileName(stsFiles[i]);
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !fileName.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                var version = fileName[prefix.Length..^suffix.Length];
                if (string.IsNullOrEmpty(version)) continue;

                var context = PlayerContext.LoadFromCache(version);
                if (context?.IsValid() == true)
                {
                    Log.Info($"[PlayerContextManager] Disk cache fallback: loaded version {version}");
                    return context;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerContextManager] TryLoadFromDiskCache failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Возвращает закэшированный signatureTimestamp.
    /// Единый источник истины для всех экземпляров <see cref="VideoController"/>.
    /// </summary>
    public string? GetCachedSignatureTimestamp() => _cachedSignatureTimestamp;

    /// <summary>Записывает signatureTimestamp в кэш.</summary>
    /// <param name="sts">Значение STS или <c>null</c> для сброса.</param>
    public void SetCachedSignatureTimestamp(string? sts) => _cachedSignatureTimestamp = sts;

    /// <summary>
    /// Сбрасывает кэш signatureTimestamp.
    /// Вызывается через <see cref="VideoController.InvalidateSignatureTimestamp"/>
    /// при 403-recovery — гарантирует сброс для всех экземпляров <see cref="VideoController"/>.
    /// </summary>
    public void InvalidateSignatureTimestamp()
    {
        _cachedSignatureTimestamp = null;
        Log.Debug("[PlayerContextManager] SignatureTimestamp cache invalidated");
    }

    /// <summary>
    /// Мягкая инвалидация: сбрасывает in-memory контекст, дисковый кэш сохраняется.
    /// </summary>
    public void InvalidateContext() => _current = null;

    /// <summary>
    /// Жёсткая инвалидация: сбрасывает in-memory контекст и физически удаляет дисковый кэш.
    /// </summary>
    public void Invalidate()
    {
        // Snapshot до обнуления: корректен при параллельной записи из GetOrLoadAsync
        var snapshot = _current;
        _current = null;
        if (snapshot != null)
            PlayerContext.ClearDiskCache(snapshot.Version);
    }
}