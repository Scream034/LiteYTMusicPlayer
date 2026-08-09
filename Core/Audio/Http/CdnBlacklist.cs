using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Runtime blacklist CDN-хостов, заблокированных ТСПУ.
/// Транзиентное состояние — не сохраняется на диск.
/// Записи автоматически истекают по TTL (ТСПУ может менять правила).
/// </summary>
/// <remarks>
/// Thread-safe. Проверка <see cref="IsBlocked"/> — O(1), zero-alloc
/// (кроме ленивого удаления expired записи).
/// Отдельный от <see cref="CdnHostStatsStore"/> — это не статистика,
/// а оперативный blacklist для CDN failover.
/// </remarks>
internal sealed class CdnBlacklist
{
    private readonly ConcurrentDictionary<string, long> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _ttlTicks;

    /// <summary>Создаёт blacklist с указанным TTL для записей.</summary>
    /// <param name="ttl">Время жизни записи. По умолчанию 5 минут.</param>
    internal CdnBlacklist(TimeSpan? ttl = null)
    {
        _ttlTicks = (ttl ?? TimeSpan.FromMinutes(5)).Ticks;
    }

    /// <summary>Количество записей (включая expired до ленивого cleanup).</summary>
    internal int Count => _blocked.Count;

    /// <summary>Помечает CDN-хост как заблокированный.</summary>
    internal void MarkBlocked(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _blocked[host] = DateTime.UtcNow.Ticks;
        Log.Info($"[CdnBlacklist] Host blocked: {host}");
    }

    /// <summary>Проверяет, заблокирован ли хост (с учётом TTL).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsBlocked(string host)
    {
        if (!_blocked.TryGetValue(host, out long blockedAtTicks))
            return false;

        if (DateTime.UtcNow.Ticks - blockedAtTicks > _ttlTicks)
        {
            _blocked.TryRemove(host, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверяет, заблокирован ли хост из URL.
    /// Быстрый парсинг без <see cref="Uri"/> аллокации.
    /// </summary>
    internal bool IsBlockedUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || _blocked.IsEmpty)
            return false;

        ReadOnlySpan<char> span = url.AsSpan();
        int schemeEnd = span.IndexOf("://".AsSpan());
        if (schemeEnd < 0) return false;

        var afterScheme = span[(schemeEnd + 3)..];
        int hostEnd = afterScheme.IndexOfAny('/', ':');
        var host = hostEnd >= 0 ? afterScheme[..hostEnd] : afterScheme;

        // ToString() неизбежен для Dictionary lookup
        return IsBlocked(host.ToString());
    }

    /// <summary>Снимает блокировку с хоста.</summary>
    internal void Unblock(string host)
    {
        if (_blocked.TryRemove(host, out _))
            Log.Info($"[CdnBlacklist] Host unblocked: {host}");
    }

    /// <summary>Очищает все записи.</summary>
    internal void Clear()
    {
        int count = _blocked.Count;
        _blocked.Clear();
        if (count > 0)
            Log.Debug($"[CdnBlacklist] Cleared {count} entries");
    }

    /// <summary>Возвращает все активные (не expired) заблокированные хосты.</summary>
    internal IReadOnlyList<string> GetBlockedHosts()
    {
        long now = DateTime.UtcNow.Ticks;
        var result = new List<string>();

        foreach (var kvp in _blocked)
        {
            if (now - kvp.Value <= _ttlTicks)
                result.Add(kvp.Key);
        }

        return result;
    }
}