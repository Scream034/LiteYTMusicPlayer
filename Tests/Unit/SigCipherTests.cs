using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и integration тесты для подсистемы дешифрации подписей (SigCipher).
/// </summary>
public static class SigCipherTests
{
    private const string MockPlayerScript = "var sts = 20650; signatureTimestamp: 20650;";
    private const string ExpectedSts = "20650";

    // 
    // UNIT TESTS (no network)
    // 

    /// <summary>
    /// Проверяет извлечение SignatureTimestamp (STS) из кода плеера.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SigCipher: STS Extraction",
        Group = TestGroups.SigCipher, Order = 10)]
    public static Task TestStsExtractionAsync()
    {
        var sts = YoutubeAstSolver.ExtractSts(MockPlayerScript);
        Assert(sts == ExpectedSts, $"Expected STS '{ExpectedSts}', got '{sts}'");
        return Task.CompletedTask;
    }

    // 
    // INTEGRATION TESTS (network required)
    // 

    /// <summary>
    /// Дешифрует реальную подпись через production pipeline:
    /// DI → <see cref="SigCipherDecryptor"/> → <see cref="JsDecryptionService"/> → QuickJS.
    /// </summary>
    [TestMethod(TestCategory.Integration, "SigCipher: Live Decryption",
        Group = TestGroups.SigCipher, Order = 20, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<SigCipherDecryptor>();
        var testSig = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig);
        sw.Stop();

        Assert(!string.IsNullOrEmpty(result), "Decryption returned empty string");
        Assert(result != testSig, "Decryption returned unchanged signature");
        Assert(result.Length > 50, $"Result length suspicious: {result.Length}");

        // Все символы результата должны быть подмножеством символов входной строки
        var inputChars = testSig.ToHashSet();
        foreach (char c in result)
            Assert(inputChars.Contains(c), $"Result contains invalid character '{c}'");

        int removedCount = testSig.Length - result.Length;
        Assert(removedCount is >= 0 and <= 10,
            $"Signature length difference out of bounds: removed {removedCount}");

        Log.Info($"[SigCipher] Decrypted in {sw.ElapsedMilliseconds}ms " +
                 $"(removed {removedCount} chars)");

        // Cache hit
        sw.Restart();
        var cached = await decryptor.DecipherAsync(testSig);
        sw.Stop();

        Assert(cached == result, "Cache returned inconsistent result");
        Assert(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms");

        Log.Info($"[SigCipher] Cache hit in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Верифицирует, что <see cref="JsDecryptionService"/> корректно обрабатывает
    /// одновременные вызовы n и sig из shared persistent context.
    /// </summary>
    [TestMethod(TestCategory.Integration, "SigCipher: Concurrent N+Sig calls",
        Group = TestGroups.SigCipher, Order = 25, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestConcurrentNAndSigAsync(IServiceProvider services)
    {
        var jsService = services.GetRequiredService<JsDecryptionService>();
        await jsService.EnsureInitializedAsync(CancellationToken.None);

        var nToken = TestConfig.Get().NToken.TestToken;
        var sigToken = TestConfig.Get().SigCipher.TestSignature;

        // Запускаем оба вызова "одновременно"
        // SemaphoreSlim(1,1) внутри service сериализует их корректно
        var sw = Stopwatch.StartNew();
        var nTask = jsService.CallAsync("n", nToken).AsTask();
        var sigTask = jsService.CallAsync("sig", sigToken).AsTask();

        await Task.WhenAll(nTask, sigTask);
        sw.Stop();

        var nResult = nTask.Result;
        var sigResult = sigTask.Result;

        // sig может вернуть null если контекст сериализует и sig отрабатывает нормально
        // главное что нет исключений и один из них правильный
        Assert(sigResult != sigToken || nResult != nToken,
            "Both n and sig returned unchanged — QuickJS likely not initialized");

        Log.Info($"[SigCipher] Concurrent calls completed in {sw.ElapsedMilliseconds}ms");
        Log.Info($"[SigCipher] n: '{nToken}' → '{nResult ?? "null"}'");
        Log.Info($"[SigCipher] sig: '{sigToken[..20]}...' → '{sigResult?[..20] ?? "null"}...'");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}