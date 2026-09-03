using System.Text.Json;

namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// Высокоуровневый провайдер PoToken с in-memory и disk кэшем.
/// Ленивая инициализация: диск читается только при первом вызове.
/// </summary>
public sealed class PoTokenProvider : IDisposable
{
    // Constants

    /// <summary>Порог: если осталось меньше N минут — нужно обновить токен.</summary>
    private const int RefreshThresholdMinutes = 60;

    private static readonly string CachePath =
        Path.Combine(G.Folder.NTokenCache, "pot_session.json");

    // State

    private readonly BotGuardService _botGuard;

    /// <summary>Семафор lazy-init: загрузка с диска выполняется ровно один раз.</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Семафор генерации: одна активная генерация через BotGuard pipeline.</summary>
    private readonly SemaphoreSlim _generateLock = new(1, 1);

    // volatile: fast path читает без lock — нужна видимость между потоками
    private volatile PoTokenResult? _sessionToken;
    private volatile bool _initialized;
    private bool _disposed;

    /// <param name="http">HTTP-клиент для WAA/gstatic запросов. Не требует YouTube cookies.</param>
    public PoTokenProvider(HttpClient http)
    {
        _botGuard = new BotGuardService(http);
    }

    // Public API

    /// <summary>
    /// Возвращает session-bound PoToken для параметра <c>&amp;pot=</c> videoplayback URL.
    /// При первом вызове выполняет lazy-загрузку с диска.
    /// При истечении TTL или смене <paramref name="visitorId"/> — генерирует новый.
    /// </summary>
    /// <param name="visitorId">Visitor ID текущей сессии YouTube.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>PoToken строка или <c>null</c> при ошибке генерации.</returns>
    public async Task<string?> GetSessionTokenAsync(
        string visitorId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        // Fast path: volatile read без семафора
        var current = _sessionToken;
        if (IsUsable(current, visitorId))
        {
            Log.Debug("[PoTokenProvider] Session token from cache");
            return current!.Token;
        }

        await _generateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check под семафором
            current = _sessionToken;
            if (IsUsable(current, visitorId))
                return current!.Token;

            Log.Info($"[PoTokenProvider] Generating session-bound PoToken " +
                     $"(visitor={Truncate(visitorId)})...");

            var token = await _botGuard.GenerateAsync(visitorId, ct).ConfigureAwait(false);
            if (token is null)
            {
                Log.Warn("[PoTokenProvider] BotGuard returned null — serving stale token if available");
                return _sessionToken is { IsExpired: false } ? _sessionToken.Token : null;
            }

            var result = new PoTokenResult
            {
                Token = token,
                Identifier = visitorId,
                // 90% от стандартного TTL YouTube 12ч: буфер на network lag и clock drift
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(10)
            };

            _sessionToken = result;
            _ = SaveToDiskAsync(result);

            Log.Info($"[PoTokenProvider] Session token generated (len={token.Length})");
            return token;
        }
        finally
        {
            _generateLock.Release();
        }
    }

    /// <summary>
    /// Content-bound PoToken для параметра <c>&amp;pot=</c> videoplayback URL.
    /// Переиспользует кэшированный BotGuard-контекст — только шаг mint (~50ms).
    /// Полный pipeline (~1s) запускается лишь при истечении IntegrityToken TTL.
    /// </summary>
    /// <param name="videoId">ID видео YouTube — используется как content binding identifier.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<string?> GetContentTokenAsync(string videoId, CancellationToken ct = default)
    {
        Log.Debug($"[PoTokenProvider] Content-bound PoToken for {Truncate(videoId)}");
        var token = await _botGuard.MintForVideoAsync(videoId, ct).ConfigureAwait(false);

        if (token is not null && token.Length != 116 && token.Length != 188)
        {
            Log.Warn($"[PoTokenProvider] Token must be 116 or 118 ({token.Length}ch) — " +
                     "BotGuard environment likely failing checks, discarding");
            return null;
        }

        return token;
    }

    /// <summary>
    /// Инвалидирует in-memory кэш и сбрасывает флаг инициализации.
    /// Вызывать при смене аккаунта / VisitorData.
    /// </summary>
    public void Invalidate()
    {
        _sessionToken = null;
        _initialized = false;
        Log.Info("[PoTokenProvider] Cache invalidated");
    }

    // Lazy Init

    private async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await LoadFromDiskAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Disk I/O

    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        try
        {
            var (json, recovered) = await AtomicFile.ReadTextWithFallbackAsync(CachePath, ct: ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            if (recovered)
                Log.Warn("[PoTokenProvider] Recovered session token from backup (.bak)");

            var entry = JsonSerializer.Deserialize(json, AppJsonContext.DefaultCompact.PoTokenCacheEntry);

            if (entry is null || string.IsNullOrEmpty(entry.Token)) return;

            var result = new PoTokenResult
            {
                Token = entry.Token,
                Identifier = entry.Identifier,
                ExpiresAt = entry.ExpiresAt
            };

            if (result.IsExpired)
            {
                Log.Debug("[PoTokenProvider] Disk cache expired — will generate fresh");
                return;
            }

            var remaining = result.ExpiresAt - DateTimeOffset.UtcNow;
            var needsRefresh = remaining.TotalMinutes < RefreshThresholdMinutes;

            Log.Info($"[PoTokenProvider] Loaded from disk " +
                     $"(remaining: {remaining.TotalHours:F1}h, needsRefresh: {needsRefresh})");

            _sessionToken = result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[PoTokenProvider] Disk load failed: {ex.Message}");
        }
    }

    private async Task SaveToDiskAsync(PoTokenResult result)
    {
        try
        {
            var entry = new PoTokenCacheEntry
            {
                Token = result.Token,
                Identifier = result.Identifier,
                ExpiresAt = result.ExpiresAt,
                CachedAt = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(entry, AppJsonContext.DefaultCompact.PoTokenCacheEntry);
            await AtomicFile.WriteTextAsync(CachePath, json, createBackup: true).ConfigureAwait(false);

            Log.Debug("[PoTokenProvider] Session token saved to disk");
        }
        catch (Exception ex)
        {
            Log.Debug($"[PoTokenProvider] Disk save failed: {ex.Message}");
        }
    }

    // Helpers

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsUsable(PoTokenResult? result, string visitorId)
    {
        if (result is null || result.IsExpired) return false;
        if (!string.Equals(result.Identifier, visitorId, StringComparison.Ordinal)) return false;

        var remaining = result.ExpiresAt - DateTimeOffset.UtcNow;
        return remaining.TotalMinutes >= RefreshThresholdMinutes;
    }

    private static string Truncate(string? s, int len = 12) =>
        s is null ? "null" : s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    // IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
        _generateLock.Dispose();
        _botGuard.Dispose();
    }
}