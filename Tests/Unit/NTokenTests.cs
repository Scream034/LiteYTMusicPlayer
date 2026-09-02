using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и integration тесты для N-Token подсистемы.
/// <para>
/// Все live-тесты используют shared <see cref="JsDecryptionService"/> из DI,
/// что гарантирует тест реального production-пути с persistent QuickJS context.
/// Изолированные тесты создают отдельный сервис через <see cref="FixedPlayerContextManager"/>.
/// </para>
/// </summary>
public static class NTokenTests
{
    private static string TestToken => TestConfig.Get().NToken.TestToken;

    // 
    // LIVE INTEGRATION TESTS
    // 

    /// <summary>
    /// Полный integration-тест дешифрации N-Token через production pipeline:
    /// DI → <see cref="NTokenDecryptor"/> → <see cref="JsDecryptionService"/> → QuickJS.
    /// </summary>
    [TestMethod(TestCategory.Integration, "NToken: Live Decryption",
        Group = TestGroups.NToken, Order = 30, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecryptAsync(token, CancellationToken.None);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty string");
        Assert(result != token, "Decryption returned unchanged token");
        Assert(!result.Contains("undefined"), "Result contains 'undefined'");
        Assert(!result.Contains("null"), "Result contains 'null'");
        Assert(result.Length >= 5, $"Result suspiciously short: '{result}'");
        Assert(result.Length <= 60, $"Result suspiciously long: {result.Length} chars");

        foreach (char c in result)
            Assert(char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
                $"Result contains invalid character '{c}'");

        Log.Info($"[NToken] Decrypted in {sw.ElapsedMilliseconds}ms: '{token}' → '{result}'");
    }

    /// <summary>
    /// Проверяет, что повторная дешифрация одного и того же токена обслуживается из кэша
    /// без обращения к QuickJS runtime (время ответа &lt; 5 мс).
    /// </summary>
    [TestMethod(TestCategory.Integration, "NToken: Cache Hit",
        Group = TestGroups.NToken, Order = 40, RequiresNetwork = true)]
    public static async Task TestCacheHitAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        // Прогрев: гарантируем, что токен попал в кэш
        var first = await decryptor.DecryptAsync(token, CancellationToken.None);
        Assert(first != token, "Warmup decryption failed — precondition for cache test not met");

        var sw = Stopwatch.StartNew();
        var second = await decryptor.DecryptAsync(token, CancellationToken.None);
        sw.Stop();

        Assert(first == second, "Cache returned inconsistent result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms (expected <5ms)");

        Log.Info($"[NToken] Cache hit in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Проверяет idempotency guard: если на вход подать уже расшифрованный токен,
    /// он должен вернуться без изменений и без вызова QuickJS.
    /// </summary>
    [TestMethod(TestCategory.Integration, "NToken: Idempotency Guard",
        Group = TestGroups.NToken, Order = 45, RequiresNetwork = true)]
    public static async Task TestIdempotencyAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        var decrypted = await decryptor.DecryptAsync(token, CancellationToken.None);
        Assert(decrypted != token, "Warmup decryption failed — precondition not met");

        // Передаём уже расшифрованный токен — должен вернуться без изменений
        var sw = Stopwatch.StartNew();
        var again = await decryptor.DecryptAsync(decrypted, CancellationToken.None);
        sw.Stop();

        Assert(again == decrypted, "Idempotency guard returned modified value");
        Assert(sw.ElapsedMilliseconds < 5, $"Idempotency check too slow: {sw.ElapsedMilliseconds}ms");

        Log.Info($"[NToken] Idempotency guard OK in {sw.ElapsedMilliseconds}ms");
    }

    // 
    // AST SOLVER + QUICKJS UNIT TEST (offline, cached player required)
    // 

    /// <summary>
    /// Верифицирует полный pipeline: AST preprocessing → QuickJS persistent context →
    /// вызов функций <c>_result.n</c> и <c>_result.sig</c>.
    /// <para>
    /// Тест изолирован от сети: использует <see cref="FixedPlayerContextManager"/>
    /// с закэшированным base.js. Пропускается, если кэш отсутствует.
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Unit, "AST + QuickJS: Solver Integration (cached player)",
        Group = TestGroups.Solver, Order = 65, RequiresNetwork = false, TimeoutSeconds = 60)]
    public static async Task TestAstSolverWithPersistentContextAsync()
    {
        // Ищем любую кэшированную версию плеера
        var config = TestConfig.Get().PlayerVersions;
        PlayerContext? context = null;
        string? usedVersion = null;

        foreach (var version in config.Versions)
        {
            context = PlayerContext.LoadFromCacheNoExpiry(version);
            if (context is not null) { usedVersion = version; break; }
        }

        if (context is null)
        {
            Log.Warn("[AST] Skipped: no cached player versions found.");
            Log.Info($"[AST] Add versions to test-config.json → playerVersions.versions");
            Log.Info($"[AST] Cache folder: {G.Folder.NTokenCache}");
            return;
        }

        Log.Info($"[AST] Testing player version: {usedVersion} ({context.BaseJs.Length / 1024}KB)");

        // Препроцессинг AST
        var sw = Stopwatch.StartNew();
        var script = context.GetOrPrepareScript(() => YoutubeAstSolver.PreprocessPlayer(context.BaseJs));
        sw.Stop();

        Assert(script.Contains("_result.n"), "Preprocessed script missing _result.n assignment");
        Assert(script.Contains("_result.sig"), "Preprocessed script missing _result.sig assignment");
        Log.Info($"[AST] Preprocessed in {sw.ElapsedMilliseconds}ms ({script.Length / 1024}KB)");

        // Создаём изолированный JsDecryptionService с фиксированным контекстом
        var fixedManager = new FixedPlayerContextManager(context);
        using var jsService = new JsDecryptionService(fixedManager);

        sw.Restart();
        await jsService.EnsureInitializedAsync(CancellationToken.None);
        sw.Stop();
        Log.Info($"[AST] QuickJS context initialized in {sw.ElapsedMilliseconds}ms");

        // Тест N-Token
        var testToken = TestToken;
        sw.Restart();
        var decryptedN = await jsService.CallAsync("n", testToken);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(decryptedN), $"N-token decryption returned null/empty");
        Assert(decryptedN != testToken, $"N-token unchanged after decryption");
        Assert(!decryptedN!.Contains("undefined"), "N-token result contains 'undefined'");
        Assert(decryptedN.Length >= 5, $"N-token result suspiciously short: '{decryptedN}'");
        Log.Info($"[AST] N-token in {sw.ElapsedMilliseconds}ms: '{testToken}' → '{decryptedN}'");

        // Тест SigCipher
        var testSig = TestConfig.Get().SigCipher.TestSignature;
        sw.Restart();
        var decryptedSig = await jsService.CallAsync("sig", testSig);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(decryptedSig), "Sig decryption returned null/empty");
        Assert(decryptedSig != testSig, "Sig unchanged after decryption");
        Log.Info($"[AST] Sig in {sw.ElapsedMilliseconds}ms: '{testSig[..20]}...' → '{decryptedSig![..20]}...'");

        Log.Info($"[AST] ✓ Player version {usedVersion} verified successfully");
    }

    // 
    // PLAYER VERSION COMPATIBILITY
    // 

    /// <summary>
    /// Тестирует совместимость с несколькими закэшированными версиями плеера.
    /// Для каждой версии создаёт изолированный <see cref="JsDecryptionService"/>
    /// и верифицирует дешифрацию N-Token и Sig.
    /// </summary>
    [TestMethod(TestCategory.Integration, "NToken: Player Version Compatibility",
        Group = TestGroups.NToken, Order = 50, RequiresNetwork = false, TimeoutSeconds = 120)]
    public static async Task TestPlayerVersionCompatibilityAsync()
    {
        var config = TestConfig.Get().PlayerVersions;

        if (config.Versions.Length == 0)
        {
            Log.Info("[NToken] Version compatibility skipped: no versions configured.");
            Log.Info("[NToken] Add to test-config.json → playerVersions.versions: [\"ac678d18\"]");
            return;
        }

        int passed = 0, failed = 0, skipped = 0;

        foreach (var version in config.Versions)
        {
            Log.Info($"\n[NToken]  Version: {version} ");

            var context = PlayerContext.LoadFromCacheNoExpiry(version);
            if (context is null)
            {
                Log.Warn($"  ⊘ Skipped: no cached base.js for {version}");
                skipped++;
                continue;
            }

            Log.Info($"  ✓ Loaded ({context.BaseJs.Length / 1024}KB)");

            var fixedManager = new FixedPlayerContextManager(context);
            using var jsService = new JsDecryptionService(fixedManager);

            try
            {
                await jsService.EnsureInitializedAsync(CancellationToken.None);
                Log.Info("  ✓ JsDecryptionService initialized");

                // N-Token smoke test
                var nResult = await jsService.CallAsync("n", TestToken);
                if (string.IsNullOrEmpty(nResult) || nResult == TestToken)
                {
                    Log.Error($"  ✗ N-token decryption failed: '{nResult ?? "null"}'");
                    failed++;
                    continue;
                }
                Log.Info($"  ✓ N-token: '{TestToken}' → '{nResult}'");

                // Per-version test cases
                if (config.NTokenTestCases.TryGetValue(version, out var cases) && cases.Length > 0)
                {
                    int casePassed = 0, caseFailed = 0;

                    foreach (var tc in cases)
                    {
                        var result = await jsService.CallAsync("n", tc.Encrypted);
                        if (result == tc.Expected)
                        {
                            casePassed++;
                            Log.Info($"    ✓ {tc.Description ?? tc.Encrypted[..Math.Min(15, tc.Encrypted.Length)]}");
                        }
                        else
                        {
                            caseFailed++;
                            Log.Error($"    ✗ Expected: '{tc.Expected}', Got: '{result ?? "null"}'");
                        }
                    }

                    if (caseFailed > 0) { failed++; continue; }
                }

                passed++;
                Log.Info($"  ✓ Version {version} PASSED");
            }
            catch (Exception ex)
            {
                Log.Error($"  ✗ Exception: {ex.Message}");
                failed++;
            }
        }

        Log.Info($"\n[NToken] Compatibility: {passed} passed, {failed} failed, {skipped} skipped");

        if (failed > 0)
            throw new Exception($"Version compatibility: {failed} version(s) failed");
    }

    // 
    // BENCHMARK
    // 

    /// <summary>
    /// Бенчмарк производительности persistent context vs создание context на каждый вызов.
    /// <para>
    /// Ожидаемые результаты на типичном железе:
    /// <list type="bullet">
    ///   <item>Cache miss (QuickJS call): ~0.1–2 мс</item>
    ///   <item>Cache hit: &lt; 1 мс</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "NToken: Benchmark (persistent context)",
        Group = TestGroups.NToken, Order = 100, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task BenchmarkAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var jsService = services.GetRequiredService<JsDecryptionService>();

        // Прогрев
        await decryptor.DecryptAsync(TestToken, CancellationToken.None);
        Log.Info("[Benchmark] Warmup complete");

        var iterations = TestConfig.Get().NToken.BenchmarkIterations;

        // Генерируем уникальные токены (чтобы не попасть в кэш)
        // Формат: ASCII letters+digits+'-'+'_', длина 15
        var tokens = Enumerable.Range(0, iterations)
            .Select(i => $"BenchTkn{i:D5}AA__"[..15])
            .ToArray();

        // Cache miss — прямые вызовы QuickJS через persistent context
        var sw = Stopwatch.StartNew();
        foreach (var token in tokens)
            await jsService.CallAsync("n", token);
        sw.Stop();

        var avgMiss = (double)sw.ElapsedMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache miss (QuickJS call): {avgMiss:F3}ms avg ({iterations} iterations)");

        // Cache hit — через NTokenDecryptor (memory cache)
        // Сначала заполняем кэш
        foreach (var token in tokens)
            await decryptor.DecryptAsync(token, CancellationToken.None);

        sw.Restart();
        foreach (var token in tokens)
            await decryptor.DecryptAsync(token, CancellationToken.None);
        sw.Stop();

        var avgHit = (double)sw.ElapsedMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache hit: {avgHit:F3}ms avg");

        Assert(avgHit < 1.0, $"Cache hit too slow: {avgHit:F3}ms (expected <1ms)");
        Assert(avgMiss < 5.0, $"QuickJS call too slow: {avgMiss:F3}ms (expected <5ms for persistent context)");

        Log.Info($"[Benchmark] ✓ Speedup from cache: {avgMiss / avgHit:F1}×");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

/// <summary>
/// Тест-двойник <see cref="PlayerContextManager"/>, всегда возвращающий фиксированный контекст.
/// Позволяет тестировать AST solver и QuickJS offline, без сетевых запросов.
/// </summary>
internal sealed class FixedPlayerContextManager : PlayerContextManager
{
    private readonly PlayerContext _context;

    public FixedPlayerContextManager(PlayerContext context) : base(null!)
        => _context = context;

    public override Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
        => Task.FromResult(_context);
}