using System.Diagnostics;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Singleton-сервис управления persistent QuickJS context.
/// <para>
/// <b>Архитектура:</b> один native handle на всё время жизни приложения.
/// Все дешифраторы (<see cref="NToken.NTokenDecryptor"/>,
/// <see cref="SigCipher.SigCipherDecryptor"/>) разделяют один контекст —
/// скрипт компилируется ровно один раз вместо одного раза на каждый вызов.
/// </para>
/// <para>
/// <b>Thread safety:</b> QuickJS не потокобезопасен. Все вызовы к native
/// слою сериализуются через <c>_callLock</c> (SemaphoreSlim 1,1).
/// Инициализация защищена отдельным <c>_initLock</c>.
/// </para>
/// <para>
/// <b>Lifecycle:</b>
/// <list type="bullet">
///   <item>Lazy init при первом вызове — не блокирует старт приложения.</item>
///   <item>При смене версии плеера — <see cref="ReinitializeAsync"/> создаёт новый handle.</item>
///   <item>Dispose — уничтожает native handle, освобождает 250 KB QuickJS runtime.</item>
/// </list>
/// </para>
/// </summary>
public sealed class JsDecryptionService : IDisposable
{
    private readonly PlayerContextManager _playerManager;

    private IntPtr _handle = IntPtr.Zero;
    private volatile string? _currentPlayerVersion;
    private volatile bool _initialized;
    private volatile bool _disposed;

    /// <summary>Сериализует все native вызовы: QuickJS не потокобезопасен.</summary>
    private readonly SemaphoreSlim _callLock = new(1, 1);

    /// <summary>Защищает от concurrent инициализации (double-init race).</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Менеджер контекста плеера. Используется для Invalidate при 403-recovery.</summary>
    public PlayerContextManager PlayerManager => _playerManager;

    /// <param name="playerManager">
    /// Менеджер контекста плеера — источник препроцессированного скрипта.
    /// </param>
    public JsDecryptionService(PlayerContextManager playerManager)
    {
        _playerManager = playerManager;
    }

    //  Public API 

    /// <summary>
    /// Гарантирует, что native context инициализирован.
    /// <para>
    /// Идемпотентен: безопасно вызывать из нескольких потоков одновременно.
    /// Тяжёлая работа (препроцессинг AST) выполняется на ThreadPool.
    /// </para>
    /// </summary>
    public async ValueTask EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized && _handle != IntPtr.Zero) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized && _handle != IntPtr.Zero) return;

            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            // Передаем context целиком. AST-парсинг и чтение текста НЕ запустятся,
            // если CreateHandleAsync успешно поднимет контекст из байткода.
            await CreateHandleAsync(context, ct).ConfigureAwait(false);

            _initialized = true;
            context.ReleaseRawScripts();

            Log.Info($"[JsDecryptionService] Persistent context initialized (player: {context.Version})");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Вызывает <c>globalThis._result.{functionName}(argument)</c>
    /// в persistent context без повторного eval скрипта.
    /// <para>
    /// Hot path: ~0.1 мс на вызов. Полностью асинхронен — не блокирует UI.
    /// </para>
    /// </summary>
    /// <param name="functionName">Имя функции: <c>"n"</c>, <c>"sig"</c> и т.д.</param>
    /// <param name="argument">Входная строка для дешифрации.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат дешифрации или <c>null</c> при ошибке.</returns>
    public async ValueTask<string?> CallAsync(
        string functionName,
        string argument,
        CancellationToken ct = default)
    {
        if (_disposed) return null;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await CheckAndReinitializeIfNeededAsync(ct).ConfigureAwait(false);

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = QuickJsNative.CallFunction(_handle, functionName, argument);
            sw.Stop();

            Log.Debug(
                $"[JsDecryptionService] {functionName}({Truncate(argument)}) → " +
                $"{(result != null ? Truncate(result) : "null")} [{sw.Elapsed.TotalMilliseconds:F3}ms]");

            return result;
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <summary>
    /// Выполняет произвольный JavaScript в persistent context.
    /// <para>
    /// Предназначен для POT token bootstrap: шаги 3 и 5 пайплайна BotGuard
    /// требуют async/await с сохранением состояния между вызовами.
    /// Глобальные переменные (VM object, webPoSignalOutput) сохраняются между вызовами.
    /// </para>
    /// </summary>
    /// <param name="code">JavaScript-код для выполнения.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Строка результата, <c>null</c> если <c>undefined</c>,
    /// или строка с префиксом <c>"__err:"</c> при JS-ошибке.
    /// </returns>
    public async ValueTask<string?> EvalAsync(string code, CancellationToken ct = default)
    {
        if (_disposed) return null;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return QuickJsNative.Eval(_handle, code);
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <summary>
    /// Принудительно реинициализирует нативный контекст с новым плеером.
    /// Вызывается при ротации версий YouTube или при восстановлении после 403 ошибки.
    /// </summary>
    public async ValueTask ReinitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _initialized = false;

            // Получаем актуальный контекст плеера (из дискового кэша или сети)
            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            // Просто передаем контекст! CreateHandleAsync сам внутри разберется:
            // если на диске лежит байткод — он мгновенно поднимет контекст из него (за ~5 мс),
            // не считывая preprocessed.js и не запуская тяжелый AST-парсер.
            await CreateHandleAsync(context, ct).ConfigureAwait(false);

            _initialized = true;
            context.ReleaseRawScripts();

            Log.Info($"[JsDecryptionService] Context reinitialized (player: {context.Version})");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Сбрасывает флаг инициализации.
    /// Следующий вызов пересоздаст QuickJS context из кэшированного скрипта.
    /// <para>
    /// При 403-recovery контекст пересоздаётся из дискового кэша (~3 мс),
    /// а не из сети (~2.5 с download + ~1.5 с AST parse).
    /// </para>
    /// </summary>
    public void Invalidate()
    {
        _initialized = false;
        _currentPlayerVersion = null;
        Log.Info("[JsDecryptionService] Invalidated — will reinitialize on next call");
    }

    /// <summary>
    /// Вызывается при обнаружении сломанного solver (n-token → null).
    /// Сбрасывает in-memory состояние и физически удаляет все дисковые артефакты
    /// текущей версии плеера, форсируя полную перезагрузку base.js при следующем вызове.
    /// </summary>
    /// <remarks>
    /// Метод синхронный: файловые операции выполняются немедленно, без fire-and-forget,
    /// чтобы гарантировать отсутствие байткода до следующего <see cref="EnsureInitializedAsync"/>.
    /// </remarks>
    public void InvalidatePlayerContextAndBytecode()
    {
        var version = _currentPlayerVersion;

        _initialized = false;
        _currentPlayerVersion = null;

        // Мягкий сброс RAM-контекста; ClearDiskCache выполняется отдельно,
        // чтобы не зависеть от _current (может быть null после предыдущего InvalidateContext).
        _playerManager.InvalidateContext();

        if (!string.IsNullOrEmpty(version))
        {
            PlayerContext.ClearDiskCache(version);
            Log.Warn($"[JsDecryptionService] Broken n-solver — purged all disk cache for player {version}");
        }
        else
        {
            Log.Warn("[JsDecryptionService] Broken n-solver — version unknown, RAM context cleared");
        }
    }

    //  Private helpers 

    /// <summary>
    /// Создаёт новый native handle. Пытается загрузить из кэша байткода (fast path), 
    /// при неудаче парсит текст, извлекает байткод из того же контекста и асинхронно сохраняет его.
    /// Атомарно заменяет старый handle новым.
    /// </summary>
    private async ValueTask CreateHandleAsync(PlayerContext context, CancellationToken ct)
    {
        IntPtr newHandle = IntPtr.Zero;
        var bytecodePath = PlayerContext.GetBytecodeCachePath(context.Version);

        // 1. Fast path: Пробуем загрузить из байткода
        if (File.Exists(bytecodePath))
        {
            try
            {
                var bytecode = await File.ReadAllBytesAsync(bytecodePath, ct).ConfigureAwait(false);
                newHandle = await Task.Run(() => QuickJsNative.CreateContextFromBytecode(bytecode), ct).ConfigureAwait(false);

                if (newHandle != IntPtr.Zero)
                {
                    Log.Info($"[JsDecryptionService] Context loaded from bytecode ({bytecode.Length / 1024}KB) — AST preprocessing skipped");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[JsDecryptionService] Bytecode load failed: {ex.Message}");
            }
        }

        // 2. Slow path: Сбой байткода. Лениво читаем JS с диска (или компилируем) и создаем заново
        if (newHandle == IntPtr.Zero)
        {
            // Метод GetOrPrepareScript вызовется и лениво считает preprocessed.js только сейчас!
            var script = await Task.Run(
                () => context.GetOrPrepareScript(
                    () => YoutubeAstSolver.PreprocessPlayer(context.BaseJs)),
                ct).ConfigureAwait(false);

            var (handle, bytecode) = await Task.Run(() => QuickJsNative.CreateContextWithBytecode(script), ct).ConfigureAwait(false);
            newHandle = handle;

            if (bytecode is { Length: > 0 })
            {
                _ = File.WriteAllBytesAsync(bytecodePath, bytecode, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            Log.Info($"[JsDecryptionService] Bytecode cached: {bytecode.Length / 1024}KB");
                        else
                            Log.Warn($"[JsDecryptionService] Bytecode save failed: {t.Exception?.Message}");
                    }, TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        if (newHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"[JsDecryptionService] Failed to create QuickJS context for player {context.Version}");

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldHandle = _handle;
            _handle = newHandle;
            _currentPlayerVersion = context.Version;

            if (oldHandle != IntPtr.Zero)
                QuickJsNative.DestroyContext(oldHandle);
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <summary>
    /// Проверяет, не сменилась ли версия плеера, и реинициализирует при необходимости.
    /// </summary>
    private async ValueTask CheckAndReinitializeIfNeededAsync(CancellationToken ct)
    {
        if (!_initialized || _handle == IntPtr.Zero) return;

        PlayerContext? context;
        try
        {
            context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrEmpty(_currentPlayerVersion) &&
            !string.Equals(_currentPlayerVersion, context.Version, StringComparison.Ordinal))
        {
            Log.Info($"[JsDecryptionService] Player version changed: " +
                     $"{_currentPlayerVersion} → {context.Version}. Reinitializing...");
            await ReinitializeAsync(ct).ConfigureAwait(false);
        }
    }

    private static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    //  Dispose 

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _callLock.Wait(millisecondsTimeout: 2000);
        try
        {
            if (_handle != IntPtr.Zero)
            {
                QuickJsNative.DestroyContext(_handle);
                _handle = IntPtr.Zero;
            }
        }
        finally
        {
            _callLock.Release();
        }

        _callLock.Dispose();
        _initLock.Dispose();

        Log.Info("[JsDecryptionService] Disposed — QuickJS runtime released");
    }
}