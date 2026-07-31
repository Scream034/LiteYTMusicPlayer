using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Дешифратор N-Token YouTube.
/// </summary>
public sealed class NTokenDecryptor : BaseYoutubeDecryptor
{
    private const int MaxNTokenLength = 60;
    private const int MinNTokenLength = 5;

    /// <summary>
    /// Событие, сигнализирующее о начале комплексной (JS) дешифрации.
    /// </summary>
    public event Action<string?>? OnComplexDecryptionStarted;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="NTokenDecryptor"/>.
    /// </summary>
    public NTokenDecryptor(JsDecryptionService jsService, string? cacheFilePath = null)
        : base(jsService, cacheFilePath ?? G.FilePath.NTokenCache, maxMemory: 2000, maxDisk: 500)
    {
    }

    /// <summary>
    /// Расшифровывает N-Token YouTube.
    /// </summary>
    public async ValueTask<string> DecryptAsync(
        string nToken,
        string? contextId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;

        if (DecryptedTokens.ContainsKey(nToken))
        {
            Log.Debug($"[NToken] Idempotency bypass: '{Truncate(nToken)}' already decrypted");
            return nToken;
        }

        if (Cache.TryGet(nToken, out var cached))
        {
            DecryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        OnComplexDecryptionStarted?.Invoke(contextId);

        await JsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = await JsService.CallAsync("n", nToken, ct).ConfigureAwait(false);

        if (ValidateResult(result, nToken))
        {
            Cache.Set(nToken, result!);
            DecryptedTokens.TryAdd(result!, 0);
            return result!;
        }

        // null = solver сломан (не просто неверный вывод) — байткод для текущей версии плеера
        // несовместим с актуальным алгоритмом YouTube. Удаляем дисковые артефакты, чтобы
        // следующий GetOrLoadAsync загрузил свежий base.js вместо повторного чтения .bin.
        if (result is null)
            JsService.InvalidatePlayerContextAndBytecode();

        Log.Warn($"[NToken] Validation failed for '{Truncate(nToken)}', returning original");
        return nToken;
    }

    /// <inheritdoc cref="DecryptAsync(string, string?, CancellationToken)"/>
    public ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default) =>
        DecryptAsync(nToken, contextId: null, ct);

    /// <inheritdoc />
    public override void InvalidateCache()
    {
        base.InvalidateCache();
        Log.Info("[NToken] Cache invalidated");
    }

    private static bool ValidateResult(string? result, string input)
    {
        if (string.IsNullOrEmpty(result) || result == input) return false;
        if (result.Length < MinNTokenLength || result.Length > MaxNTokenLength) return false;
        if (result is "undefined" or "null" or "NaN" or "true" or "false") return false;

        foreach (char c in result)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        if (input.Contains(result, StringComparison.Ordinal) ||
            result.Contains(input, StringComparison.Ordinal))
            return false;

        if (HasLongSharedSubstring(input.AsSpan(), result.AsSpan(), minLength: 8))
            return false;

        return true;
    }

    private static bool HasLongSharedSubstring(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int minLength)
    {
        if (a.Length < minLength || b.Length < minLength) return false;

        for (int i = 0; i <= a.Length - minLength; i++)
        {
            if (b.IndexOf(a.Slice(i, minLength)) >= 0)
                return true;
        }

        return false;
    }

    private static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
}