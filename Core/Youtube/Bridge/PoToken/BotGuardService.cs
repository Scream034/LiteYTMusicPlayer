using System.Diagnostics;
using System.Text;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// Управляет persistent QuickJS-контекстом для выполнения BotGuard VM
/// и генерации PoToken через staged pipeline.
/// </summary>
public sealed class BotGuardService : IDisposable
{
    // Dependencies
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // State
    private IntPtr _handle = IntPtr.Zero;
    private string? _cachedBgScript;
    private string? _cachedBgHash;
    private bool _disposed;

    /// <summary>
    /// Время истечения текущего IntegrityToken.
    /// Читается/пишется только под <see cref="_lock"/>.
    /// </summary>
    private DateTimeOffset _contextExpiresAt;

    /// <summary>
    /// Таймаут ожидания завершения Promise в event loop.
    /// </summary>
    private const int PumpTimeoutMs = 10_000;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="BotGuardService"/>.
    /// </summary>
    /// <param name="http">Клиент HTTP.</param>
    public BotGuardService(HttpClient http)
    {
        _http = http;
    }

    // 
    // Public API
    // 

    /// <summary>
    /// Полный pipeline генерации PoToken для identifier.
    /// </summary>
    public async Task<string?> GenerateAsync(string identifier, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        const int maxAttempts = 3;

#if DEBUG
        try
        {
            Common.QuickJsNative.VerifyExpectedBridgeLoaded();
        }
        catch (QuickJsBridgeVerificationException ex)
        {
            Log.Error($"[BotGuardService] Fatal bridge verification error before pipeline start: {ex.Message}");
            return null;
        }
#endif

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                if (attempt <= 3)
                    Log.Debug($"[BotGuardService] Retry {attempt}/{maxAttempts}...");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            Log.Debug($"[BotGuardService] Step 1: Fetching challenge (attempt {attempt})...");

            var challenge = await WaaClient.FetchChallengeAsync(
                _http, null, ct).ConfigureAwait(false);

            if (challenge is null)
            {
                Log.Error("[BotGuardService] Failed to fetch challenge");
                return null;
            }

            string bgScript = await GetOrDownloadBgScriptAsync(challenge, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(bgScript))
            {
                Log.Error("[BotGuardService] No bg.js script available");
                return null;
            }

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await RunPipelineAsync(bgScript, challenge, identifier, ct)
                    .ConfigureAwait(false);

                if (result is not null)
                {
                    Log.Info($"[BotGuardService] ✓ Pipeline succeeded on attempt {attempt} " +
                             $"({sw.Elapsed.TotalMilliseconds:F0}ms)");
                    return result;
                }

                ResetContext();
            }
            finally
            {
                Log.Debug($"[BotGuardService] Attempt {attempt}: {sw.Elapsed.TotalMilliseconds:F0}ms");
                _lock.Release();
            }
        }

        Log.Error($"[BotGuardService] All {maxAttempts} attempts failed " +
                  $"({sw.Elapsed.TotalMilliseconds:F0}ms)");
        return null;
    }

    /// <summary>
    /// Fast mint для content-bound PoToken.
    /// </summary>
    public async Task<string?> MintForVideoAsync(string videoId, CancellationToken ct = default)
    {
        if (_handle != IntPtr.Zero)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_handle != IntPtr.Zero && DateTimeOffset.UtcNow < _contextExpiresAt)
                {
#if DEBUG
                    BeginTraceCapture("fast-mint");
#endif

                    var setId = Common.QuickJsNative.Eval(
                        _handle,
                        $"globalThis._bg_mintIdentifier = '{EscapeJsString(videoId)}';");

                    if (setId?.StartsWith("__err:") != true)
                    {
                        var token = await RunAsyncJsAsync(
                            "_bg_doMint(globalThis._bg_mintIdentifier)",
                            "mint-video",
                            ct).ConfigureAwait(false);

                        DumpDiagnosticConsole("fast-mint");

                        if (token is not null)
                        {
#if DEBUG
                            SaveSuccessFastMintArtifacts(videoId, CollectTraceData(), _cachedBgHash);
#endif
                            Log.Debug($"[BotGuardService] Fast-mint OK ({token.Length}ch) for {Truncate(videoId, 11)}");
                            return token;
                        }

                        CaptureFastMintFailure(videoId, "mint-video returned null");
                    }
                    else
                    {
                        CaptureFastMintFailure(videoId, $"failed to set identifier: {setId}");
                    }

                    ResetContext();
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        Log.Debug($"[BotGuardService] Context stale — full pipeline for {Truncate(videoId, 11)}");
        return await GenerateAsync(videoId, ct).ConfigureAwait(false);
    }

    // 
    // Pipeline
    // 

    private async Task<string?> RunPipelineAsync(
        string bgScript,
        BotGuardChallenge challenge,
        string identifier,
        CancellationToken ct)
    {
        //
        // Шаг 3a: staged context initialization
        //
        if (_handle == IntPtr.Zero)
        {
            Log.Debug("[BotGuardService] Step 3a: Creating empty QuickJS context...");
            var ok = await CreateAndLoadContextAsync(bgScript, challenge, ct).ConfigureAwait(false);
            if (!ok)
            {
                CapturePipelineFailure(challenge, null, "CreateAndLoadContextAsync returned false");
                ResetContext();
                return null;
            }

            Log.Debug("[BotGuardService] QuickJS BotGuard context created and staged-load completed");
            DumpDiagnosticConsole("post-load");
        }

        //
        // Шаг 3b: Async snapshot
        //
        Log.Debug("[BotGuardService] Step 3b: Getting BotGuard snapshot (async)...");

#if DEBUG
        BeginTraceCapture("snapshot");
#endif

        var snapshot = await RunAsyncJsAsync("_bg_doSnapshot()", "snapshot", ct)
            .ConfigureAwait(false);

        DumpDiagnosticConsole("post-snapshot");

        if (snapshot is null)
        {
            CapturePipelineFailure(challenge, null, "snapshot returned null");
            ResetContext();
            return null;
        }

        Log.Debug($"[BotGuardService] Snapshot OK ({snapshot.Length} chars)");

        //
        // Post-snapshot pump
        //
        Log.Debug("[BotGuardService] Post-snapshot pump (waiting for webPoSignalOutput)...");

        var postPumped = await Task.Run(
            () => Common.QuickJsNative.PumpEventLoop(_handle, 3_000), ct)
            .ConfigureAwait(false);

        var wpoLength = ReadWebPoSignalOutputLength();
        Log.Debug($"[BotGuardService] Post-snapshot: pumped={postPumped}, webPoSignalOutput.length={wpoLength}");

        DumpDiagnosticConsole("post-pump-1");

        if (wpoLength == 0)
        {
            var bgStateJson = DumpBgStateJson();
            Log.Debug($"[BotGuardService] _bg_state at FAIL: {bgStateJson}");

            CapturePipelineFailure(
                challenge,
                snapshot,
                $"webPoSignalOutput.length={wpoLength}, _bg_state={bgStateJson}");

            Log.Debug("[BotGuardService] wpo=0, skipping IT+mint");
            return null;
        }
        else if (wpoLength > 0)
        {
#if DEBUG
            SaveSuccessPipelineArtifacts(challenge, identifier, snapshot, CollectTraceData());
#else
            SaveSuccessPipelineArtifacts(challenge, identifier, snapshot, null);
#endif
        }

        //
        // Шаг 4: IntegrityToken
        //
        Log.Debug("[BotGuardService] Step 4: Fetching IntegrityToken...");

        var integrityData = await WaaClient.GenerateIntegrityTokenAsync(
            _http, snapshot, ct).ConfigureAwait(false);

        if (integrityData is null)
        {
            Log.Error("[BotGuardService] Failed to get IntegrityToken");
            CapturePipelineFailure(challenge, snapshot, "GenerateIntegrityTokenAsync returned null");
            ResetContext();
            return null;
        }

        Log.Debug($"[BotGuardService] IntegrityToken OK (TTL={integrityData.EstimatedTtlSecs}s, isFallback={integrityData.IsFallbackToken})");

        //
        // Шаг 5a: Activate token
        //
        Log.Debug("[BotGuardService] Step 5a: Activating IntegrityToken...");

        var activateResult = Common.QuickJsNative.Eval(
            _handle,
            $"globalThis._bg_activateToken('{EscapeJsString(integrityData.IntegrityToken)}')");

        if (activateResult?.StartsWith("__err:") == true)
        {
            Log.Error($"[BotGuardService] Token activation failed: {activateResult}");
            CapturePipelineFailure(challenge, snapshot, $"token activation failed: {activateResult}");
            ResetContext();
            return null;
        }

        Log.Debug($"[BotGuardService] Token activation result: {activateResult}");

        _contextExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
            integrityData.EstimatedTtlSecs * 0.85);

        DumpDiagnosticConsole("post-activation");

        await Task.Run(
            () => Common.QuickJsNative.PumpEventLoop(_handle, 1_000), ct)
            .ConfigureAwait(false);

        //
        // Шаг 5b: Mint
        //
        Log.Debug("[BotGuardService] Step 5b: Minting PoToken...");

#if DEBUG
        BeginTraceCapture("mint");
#endif

        var setIdResult = Common.QuickJsNative.Eval(
            _handle,
            $"globalThis._bg_mintIdentifier = '{EscapeJsString(identifier)}';");

        if (setIdResult?.StartsWith("__err:") == true)
        {
            Log.Error($"[BotGuardService] Failed to set identifier: {setIdResult}");
            CapturePipelineFailure(challenge, snapshot, $"failed to set identifier: {setIdResult}");
            ResetContext();
            return null;
        }

        var poToken = await RunAsyncJsAsync(
            "_bg_doMint(globalThis._bg_mintIdentifier)",
            "mint",
            ct).ConfigureAwait(false);

        DumpDiagnosticConsole("post-mint");

        if (poToken is null)
        {
            CapturePipelineFailure(challenge, snapshot, "mint returned null");
            ResetContext();
            return null;
        }

        Log.Info($"[BotGuardService] PoToken minted OK. Length={poToken.Length}, TTL={integrityData.EstimatedTtlSecs}s");
        return poToken;
    }

    /// <summary>
    /// Создаёт пустой native context и staged-load'ит:
    /// compat → bg.js → bootstrap.
    /// </summary>
    private async Task<bool> CreateAndLoadContextAsync(
        string bgScript,
        BotGuardChallenge challenge,
        CancellationToken ct)
    {
        _handle = await Task.Run(
            () => Common.QuickJsNative.CreateContext("void 0;"),
            ct).ConfigureAwait(false);

#if DEBUG
        var envProbe = Common.QuickJsNative.Eval(
            _handle,
            "typeof window + '|' + " +
            "typeof self + '|' + " +
            "typeof global + '|' + " +
            "typeof document + '|' + " +
            "typeof navigator + '|' + " +
            "typeof Event + '|' + " +
            "typeof location + '|' + " +
            "typeof history + '|' + " +
            "typeof screen");

        Log.Info($"[BotGuardService] Native env probe: {envProbe}");

        const string expectedProbe =
            "object|object|object|object|object|function|object|object|object";

        if (!string.Equals(envProbe, expectedProbe, StringComparison.Ordinal))
        {
            Log.Error($"[BotGuardService] Native browser environment probe failed. " +
                      $"Expected='{expectedProbe}', Actual='{envProbe}'");
            return false;
        }
#endif

        if (_handle == IntPtr.Zero)
        {
            Log.Error("[BotGuardService] Failed to create empty QuickJS context");
            return false;
        }

        Log.Debug($"[BotGuardService] Staged load: bg.js={bgScript.Length / 1024}KB, initiating native bootstrap...");

        // Шаг 1: Оцениваем сам тяжелый JS-скрипт bg.js
        if (!EvalPhase("bg.js", bgScript))
            return false;

        // Шаг 2: Инициализируем нативный бутстрап без вызовов JS-строк
        bool bootstrapOk = await Task.Run(
            () => Common.QuickJsNative.InitBotGuardBootstrap(_handle, challenge.GlobalName, challenge.Program),
            ct).ConfigureAwait(false);

        if (!bootstrapOk)
        {
            Log.Error("[BotGuardService] Failed to initialize native BotGuard bootstrap");
            return false;
        }

        Log.Debug("[BotGuardService] QuickJS BotGuard context created and native staged-load completed");
        return true;
    }

    /// <summary>
    /// Выполняет одну bootstrap-фазу и логирует точную ошибку.
    /// </summary>
    private bool EvalPhase(string phaseName, string script)
    {
        var sw = Stopwatch.StartNew();
        var result = Common.QuickJsNative.Eval(_handle, script);
        sw.Stop();

        if (result?.StartsWith("__err:") == true)
        {
            var nativeErr = Common.QuickJsNative.GetLastError(_handle);
            Log.Error($"[BotGuardService] Phase '{phaseName}' failed in {sw.Elapsed.TotalMilliseconds:F1}ms");
            Log.Error($"[BotGuardService] Phase '{phaseName}' JS error: {result[6..]}");
            if (!string.IsNullOrEmpty(nativeErr))
                Log.Error($"[BotGuardService] Phase '{phaseName}' native last_error: {nativeErr}");
            return false;
        }

        Log.Debug($"[BotGuardService] Phase '{phaseName}' OK in {sw.Elapsed.TotalMilliseconds:F1}ms");
        return true;
    }

    // 
    // Diagnostic helpers
    // 

    [Conditional("DEBUG")]
    private void DumpDiagnosticConsole(string phase)
    {
#if DEBUG
        if (_handle == IntPtr.Zero) return;

        var consoleOutput = GetConsoleSnapshot(clear: true);
        if (string.IsNullOrEmpty(consoleOutput)) return;

        var lines = consoleOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int count = 0;
        bool inTaggedMultilineBlock = false;

        foreach (var line in lines)
        {
            if (IsDiagnosticConsoleLine(line))
            {
                Log.Debug($"[BotGuard-Diag:{phase}] {line}");
                count++;
                inTaggedMultilineBlock =
                    line.Contains("[BG-FATAL-STACK]") ||
                    line.Contains("[ASF-FATAL-STACK]");
                continue;
            }

            if (inTaggedMultilineBlock && !line.StartsWith('[', StringComparison.Ordinal))
            {
                Log.Debug($"[BotGuard-Diag:{phase}] {line}");
                count++;
                continue;
            }

            inTaggedMultilineBlock = false;
        }

        if (count > 0)
            Log.Info($"[BotGuard-Diag:{phase}] {count} diagnostic entries logged");
#endif
    }

    // 
    // Async JS runner
    // 

    private async Task<string?> RunAsyncJsAsync(
        string jsExpression,
        string operationName,
        CancellationToken ct)
    {
        const string evalTemplate =
            "globalThis._bg_result = null;" +
            "globalThis._bg_error  = null;" +
            "globalThis._bg_done   = false;" +
            "Promise.resolve({EXPR}).then(" +
            "  function(r) {" +
            "    globalThis._bg_result = (r == null) ? '' : String(r);" +
            "    globalThis._bg_done   = true;" +
            "  }," +
            "  function(e) {" +
            "    globalThis._bg_error = (e && e.message) ? e.message : String(e);" +
            "    globalThis._bg_done  = true;" +
            "  }" +
            ");";

        var startResult = Common.QuickJsNative.Eval(
            _handle,
            evalTemplate.Replace("{EXPR}", jsExpression));

        if (startResult?.StartsWith("__err:") == true)
        {
            Log.Error($"[BotGuardService] {operationName} eval failed: {startResult[6..]}");
            Log.Error($"[BotGuardService] {operationName} native last_error: {Common.QuickJsNative.GetLastError(_handle)}");
            return null;
        }

        var pumped = await Task.Run(
            () => Common.QuickJsNative.PumpEventLoop(_handle, PumpTimeoutMs), ct)
            .ConfigureAwait(false);

        if (pumped < 0)
        {
            Log.Error($"[BotGuardService] {operationName} pump exception: {Common.QuickJsNative.GetLastError(_handle)}");
            return null;
        }

        var doneCheck = Common.QuickJsNative.Eval(_handle, "globalThis._bg_done ? 'yes' : 'no'");

        if (doneCheck?.StartsWith("__err:") == true)
        {
            Log.Error($"[BotGuardService] {operationName} context corrupted after pump: {doneCheck[6..]} (pumped={pumped})");
            return null;
        }

        if (doneCheck != "yes")
        {
            Log.Error($"[BotGuardService] {operationName} timed out after {PumpTimeoutMs}ms (done={doneCheck}, pumped={pumped})");
            return null;
        }

        var error = Common.QuickJsNative.Eval(_handle, "globalThis._bg_error");
        if (!string.IsNullOrEmpty(error))
        {
            Log.Error($"[BotGuardService] {operationName} JS error: {error}");
            return null;
        }

        var result = Common.QuickJsNative.Eval(_handle, "globalThis._bg_result");
        if (string.IsNullOrEmpty(result))
        {
            Log.Error($"[BotGuardService] {operationName} returned empty. pumped={pumped}, LastError: {Common.QuickJsNative.GetLastError(_handle)}");
            return null;
        }

        return result;
    }

    // 
    // Helpers
    // 

    private string? DumpBgStateJson()
    {
        if (_handle == IntPtr.Zero) return null;

        const string code = """
        (function() {
            try {
                var s = globalThis._bg_state;
                if (!s) return '{"error":"_bg_state missing"}';
                var wpoTypes = [];
                var wpo = s.webPoSignalOutput;
                for (var i = 0; i < (wpo ? wpo.length : 0); i++)
                    wpoTypes.push(typeof wpo[i]);
                return JSON.stringify({
                    ready:              s.ready,
                    hasSetIT:           typeof s.setIntegrityToken === 'function',
                    hasASF:             typeof (s.vmFunctions && s.vmFunctions.asyncSnapshotFunction) === 'function',
                    hasShutFn:          typeof (s.vmFunctions && s.vmFunctions.shutdownFunction) === 'function',
                    wpoLength:          wpo ? wpo.length : -1,
                    wpoTypes:           wpoTypes
                });
            } catch(e) {
                return '{"error":"' + String(e.message).replace(/"/g, "'") + '"}';
            }
        })()
        """;

        return Common.QuickJsNative.Eval(_handle, code);
    }

    private void CapturePipelineFailure(
        BotGuardChallenge challenge,
        string? snapshot,
        string reason)
    {
        var bgStateJson = DumpBgStateJson();
        var lastError = _handle != IntPtr.Zero
            ? Common.QuickJsNative.GetLastError(_handle)
            : "NULL handle";

#if DEBUG
        var consoleOutput = GetConsoleSnapshot(clear: false);
        var traceData = CollectTraceData();
#else
        string? consoleOutput = null;
        string? traceData = null;
#endif

        SaveFailProgram(
            challenge,
            snapshot,
            bgStateJson,
            lastError,
            consoleOutput,
            traceData,
            reason);
    }

    private void CaptureFastMintFailure(string videoId, string reason)
    {
        var bgStateJson = DumpBgStateJson();
        var lastError = _handle != IntPtr.Zero
            ? Common.QuickJsNative.GetLastError(_handle)
            : "NULL handle";

#if DEBUG
        var consoleOutput = GetConsoleSnapshot(clear: false);
        var traceData = CollectTraceData();
#else
        string? consoleOutput = null;
        string? traceData = null;
#endif

        SaveFastMintFailure(
            videoId,
            bgStateJson,
            lastError,
            consoleOutput,
            traceData,
            reason,
            _cachedBgHash);
    }

    private static void SaveSuccessPipelineArtifacts(
        BotGuardChallenge challenge,
        string identifier,
        string snapshot,
        string? traceData)
    {
        try
        {
            var dir = Path.Combine(G.Folder.Logs, "BotGuard", "programs");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("HH-mm-ss-fff");
            var safeId = SanitizeFileToken(identifier, 24);
            var baseName = $"SUCCESS_{stamp}_{safeId}_{challenge.Program.Length}ch_snap{snapshot.Length}";

            var diagSb = new StringBuilder(512);
            diagSb.AppendLine($"// SUCCESS dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            diagSb.AppendLine($"// Kind:        full-pipeline");
            diagSb.AppendLine($"// Identifier:  {identifier}");
            diagSb.AppendLine($"// VM:          {challenge.GlobalName}");
            diagSb.AppendLine($"// Hash:        {challenge.InterpreterHash}");
            diagSb.AppendLine($"// Program.len: {challenge.Program.Length}");
            diagSb.AppendLine($"// Snapshot.len:{snapshot.Length}");

            SaveArtifactBundle(
                baseName,
                challenge.Program,
                diagSb.ToString(),
                consoleOutput: null,
                traceData: traceData);

            Log.Debug($"[BotGuardService] SUCCESS artifacts saved: {baseName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[BotGuardService] SaveSuccessPipelineArtifacts failed: {ex.Message}");
        }
    }

    private static void SaveSuccessFastMintArtifacts(
        string videoId,
        string? traceData,
        string? bgHash)
    {
        try
        {
            var dir = Path.Combine(G.Folder.Logs, "BotGuard", "programs");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("HH-mm-ss-fff");
            var safeId = SanitizeFileToken(videoId, 24);
            var baseName = $"SUCCESS_FASTMINT_{stamp}_{safeId}";

            var diagSb = new StringBuilder(256);
            diagSb.AppendLine($"// SUCCESS dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            diagSb.AppendLine($"// Kind:        fast-mint");
            diagSb.AppendLine($"// Identifier:  {videoId}");
            diagSb.AppendLine($"// BgHash:      {bgHash ?? "null"}");

            SaveArtifactBundle(
                baseName,
                programText: null,
                diagText: diagSb.ToString(),
                consoleOutput: null,
                traceData: traceData);

            Log.Debug($"[BotGuardService] SUCCESS fast-mint artifacts saved: {baseName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[BotGuardService] SaveSuccessFastMintArtifacts failed: {ex.Message}");
        }
    }

    private static void SaveFailProgram(
        BotGuardChallenge challenge,
        string? snapshot,
        string? bgStateJson,
        string? lastError,
        string? consoleOutput,
        string? traceData = null,
        string? reason = null)
    {
        try
        {
            var stamp = DateTime.Now.ToString("HH-mm-ss-fff");
            int snapshotLen = snapshot?.Length ?? 0;
            var baseName = $"FAIL_{stamp}_{challenge.Program.Length}ch_snap{snapshotLen}";

            var diagSb = new StringBuilder(512);
            diagSb.AppendLine($"// FAIL dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            diagSb.AppendLine($"// Reason:      {reason ?? "n/a"}");
            diagSb.AppendLine($"// VM:          {challenge.GlobalName}");
            diagSb.AppendLine($"// Hash:        {challenge.InterpreterHash}");
            diagSb.AppendLine($"// Program.len: {challenge.Program.Length}");
            diagSb.AppendLine($"// Snapshot.len:{snapshotLen}");
            diagSb.AppendLine($"// _bg_state:   {bgStateJson ?? "null"}");
            diagSb.AppendLine($"// LastError:   {lastError ?? "null"}");

            SaveArtifactBundle(
                baseName,
                challenge.Program,
                diagSb.ToString(),
                consoleOutput,
                traceData);

            Log.Debug($"[BotGuardService] FAIL dump saved: {baseName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[BotGuardService] SaveFailProgram failed: {ex.Message}");
        }
    }

    private static void SaveFastMintFailure(
        string videoId,
        string? bgStateJson,
        string? lastError,
        string? consoleOutput,
        string? traceData,
        string? reason,
        string? bgHash)
    {
        try
        {
            var stamp = DateTime.Now.ToString("HH-mm-ss-fff");
            var safeId = SanitizeFileToken(videoId, 24);
            var baseName = $"FAIL_FASTMINT_{stamp}_{safeId}";

            var diagSb = new StringBuilder(512);
            diagSb.AppendLine($"// FAIL dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            diagSb.AppendLine($"// Kind:        fast-mint");
            diagSb.AppendLine($"// Identifier:  {videoId}");
            diagSb.AppendLine($"// Reason:      {reason ?? "n/a"}");
            diagSb.AppendLine($"// BgHash:      {bgHash ?? "null"}");
            diagSb.AppendLine($"// _bg_state:   {bgStateJson ?? "null"}");
            diagSb.AppendLine($"// LastError:   {lastError ?? "null"}");

            SaveArtifactBundle(
                baseName,
                programText: null,
                diagText: diagSb.ToString(),
                consoleOutput: consoleOutput,
                traceData: traceData);

            Log.Debug($"[BotGuardService] FAIL fast-mint dump saved: {baseName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[BotGuardService] SaveFastMintFailure failed: {ex.Message}");
        }
    }

    private static void SaveArtifactBundle(
     string baseName,
     string? programText,
     string diagText,
     string? consoleOutput,
     string? traceData)
    {
        var dir = Path.Combine(G.Folder.Logs, "BotGuard", "programs");

        if (!string.IsNullOrEmpty(programText))
        {
            AtomicFile.WriteText(
                Path.Combine(dir, $"{baseName}.program.txt"),
                programText,
                createBackup: false,
                Encoding.UTF8);
        }

        AtomicFile.WriteText(
            Path.Combine(dir, $"{baseName}.diag.txt"),
            diagText,
            createBackup: false,
            Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(consoleOutput))
        {
            AtomicFile.WriteText(
                Path.Combine(dir, $"{baseName}.console.txt"),
                consoleOutput,
                createBackup: false,
                Encoding.UTF8);
        }

        if (!string.IsNullOrEmpty(traceData) && !traceData.Contains("total=0, buffered=0"))
        {
            AtomicFile.WriteText(
                Path.Combine(dir, $"{baseName}.trace.txt"),
                traceData,
                createBackup: false,
                Encoding.UTF8);

            Log.Debug($"[BotGuardService] Trace saved: {traceData.Length / 1024}KB");
        }
    }

#if DEBUG
    [Conditional("DEBUG")]
    private void BeginTraceCapture(string operationName)
    {
#if DEBUG
        if (_handle == IntPtr.Zero) return;
        Common.QuickJsNative.TraceEnable(_handle, true);
        Common.QuickJsNative.TraceReset(_handle);
        Log.Debug($"[BotGuardService] Trace reset for {operationName}");
#endif
    }

    private string CollectTraceData()
    {
        if (_handle == IntPtr.Zero) return "// no context\n";

        int total = Common.QuickJsNative.TraceGetTotal(_handle);
        int count = Common.QuickJsNative.TraceGetCount(_handle);

        var sb = new StringBuilder(16_384);
        sb.AppendLine($"// String comparisons: total={total}, buffered={count}");

        if (total == 0)
        {
            sb.AppendLine("// no comparisons recorded");
            Log.Info("[BotGuardService] Trace collected: 0 total comparisons");
            return sb.ToString();
        }

        ReadOnlySpan<string> filters =
        [
            "undefined",
            "[object ",
            "native code",
            "loading",
            "complete"
        ];

        foreach (var filter in filters)
        {
            var filtered = Common.QuickJsNative.TraceDumpFiltered(_handle, filter, 200);
            if (string.IsNullOrEmpty(filtered)) continue;

            sb.Append("\n// --- Filter: \"").Append(filter).AppendLine("\" ---");
            sb.Append(filtered);
        }

        int tailCount = count > 200 ? 200 : count;
        int tailOffset = count - tailCount;

        var tail = Common.QuickJsNative.TraceDump(_handle, tailOffset, tailCount);
        if (!string.IsNullOrEmpty(tail))
        {
            sb.AppendLine($"\n// --- Last {tailCount} comparisons ---");
            sb.Append(tail);
        }

        Log.Info($"[BotGuardService] Trace collected: {total} total comparisons, {sb.Length / 1024}KB output");
        return sb.ToString();
    }

    private string? GetConsoleSnapshot(bool clear)
    {
        if (_handle == IntPtr.Zero) return null;

        var output = Common.QuickJsNative.GetConsoleOutput(_handle);
        if (clear)
            Common.QuickJsNative.ClearConsoleOutput(_handle);

        return string.IsNullOrWhiteSpace(output) ? null : output;
    }
#endif

    private int ReadWebPoSignalOutputLength()
    {
        if (_handle == IntPtr.Zero) return -1;

        var raw = Common.QuickJsNative.Eval(
            _handle,
            "String(globalThis._bg_state ? globalThis._bg_state.webPoSignalOutput.length : -1)");

        if (raw is null || raw.StartsWith("__err:"))
            return -1;

        return int.TryParse(raw, out var n) ? n : -1;
    }

    private async Task<string> GetOrDownloadBgScriptAsync(
        BotGuardChallenge challenge,
        CancellationToken ct)
    {
        if (_cachedBgScript is not null && _cachedBgHash == challenge.InterpreterHash)
        {
            Log.Debug("[BotGuardService] Step 2: Using cached bg.js");
            return _cachedBgScript;
        }

        string script;

        if (!string.IsNullOrEmpty(challenge.InlineScript))
        {
            Log.Info($"[BotGuardService] Step 2: Using INLINE bg.js ({challenge.InlineScript.Length / 1024}KB)");
            script = challenge.InlineScript;
        }
        else if (!string.IsNullOrEmpty(challenge.ScriptUrl))
        {
            Log.Debug($"[BotGuardService] Step 2: Downloading bg.js from {Truncate(challenge.ScriptUrl, 60)}");
            script = await _http.GetStringAsync(challenge.ScriptUrl, ct).ConfigureAwait(false);
        }
        else
        {
            Log.Warn("[BotGuardService] No script in challenge");
            return string.Empty;
        }

        if (_cachedBgHash != challenge.InterpreterHash)
            ResetContext();

        _cachedBgScript = script;
        _cachedBgHash = challenge.InterpreterHash;

        Log.Info($"[BotGuardService] bg.js ready: {script.Length / 1024}KB, Hash={Truncate(challenge.InterpreterHash)}");
        return script;
    }

    private void ResetContext()
    {
        if (_handle == IntPtr.Zero) return;
        Common.QuickJsNative.DestroyContext(_handle);
        _handle = IntPtr.Zero;
        Log.Debug("[BotGuardService] Context reset");
    }

    private static string Truncate(string? s, int len = 16) =>
        s is null ? "null"
                  : s.Length <= len ? s
                  : string.Concat(s.AsSpan(0, len), "...");

    private static bool IsDiagnosticConsoleLine(string line) =>
            line.Contains("[BG-TRAP]") ||
            line.Contains("[BG-FATAL-STACK]") ||
            line.Contains("[@]") ||
            line.Contains("[VM-") ||
            line.Contains("[ASF]") ||
            line.Contains("[ASF-FATAL-STACK]") ||
            line.Contains("[CB") ||
            line.Contains("[WPO") ||
            line.Contains("[ACT]") ||
            line.Contains("[MINT]") ||
            line.Contains("[DIAG-") ||
            line.Contains("[OVERFLOW]") ||
            line.Contains("[DIAG-THREAD-MIGRATION]");

    private static string SanitizeFileToken(string? value, int maxLen = 32)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "null";

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            if (sb.Length >= maxLen)
                break;
        }

        return sb.Length == 0 ? "empty" : sb.ToString();
    }

    /// <summary>
    /// Экранирует строку для JS-литерала в одинарных кавычках.
    /// </summary>
    private static string EscapeJsString(string s)
    {
        if (s.Length == 0) return string.Empty;

        int maxLen = s.Length * 6;

        if (maxLen <= 4096)
        {
            Span<char> buf = stackalloc char[maxLen];
            int pos = 0;

            foreach (char c in s)
                pos += AppendEscaped(buf[pos..], c);

            return new string(buf[..pos]);
        }

        var sb = new StringBuilder(s.Length + 64);
        Span<char> tmp = stackalloc char[6];

        foreach (char c in s)
        {
            int len = AppendEscaped(tmp, c);
            sb.Append(tmp[..len]);
        }

        return sb.ToString();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int AppendEscaped(Span<char> buf, char c)
        {
            switch (c)
            {
                case '\\': buf[0] = '\\'; buf[1] = '\\'; return 2;
                case '\'': buf[0] = '\\'; buf[1] = '\''; return 2;
                case '\n': buf[0] = '\\'; buf[1] = 'n'; return 2;
                case '\r': buf[0] = '\\'; buf[1] = 'r'; return 2;
                case '\t': buf[0] = '\\'; buf[1] = 't'; return 2;
                case '\0': buf[0] = '\\'; buf[1] = '0'; return 2;
                case '\u2028': "\\u2028".AsSpan().CopyTo(buf); return 6;
                case '\u2029': "\\u2029".AsSpan().CopyTo(buf); return 6;
                default: buf[0] = c; return 1;
            }
        }
    }

    // 
    // IDisposable
    // 

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool acquired = _lock.Wait(millisecondsTimeout: 5_000);
        try
        {
            ResetContext();
        }
        finally
        {
            if (acquired)
                _lock.Release();
        }

        _lock.Dispose();
    }
}