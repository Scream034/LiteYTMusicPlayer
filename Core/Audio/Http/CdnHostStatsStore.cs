using System.Text.Json;

namespace LMP.Core.Audio.Http;

// --- Section: Public Models ---

/// <summary>
/// Статистика одного CDN-кластера YouTube.
/// Кластер = группа ingress-нод с общим <c>sn-XXXXXXXX</c> суффиксом.
/// </summary>
public sealed class CdnClusterStats
{
    /// <summary>
    /// Идентификатор кластера, извлечённый из hostname.
    /// Пример: <c>sn-4g5ednle</c> из <c>rr3---sn-4g5ednle.googlevideo.com</c>.
    /// </summary>
    public required string ClusterId { get; set; }

    /// <summary>
    /// Последний известный живой ingress-хост кластера.
    /// Используется как цель для <c>generate_204</c>.
    /// </summary>
    public required string SampleHost { get; set; }

    /// <summary>
    /// Суммарное число зафиксированных обращений к кластеру.
    /// </summary>
    public int HitCount { get; set; }

    /// <summary>
    /// Время последнего обращения (UTC).
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Экспоненциально-взвешенное среднее TTFB в мс (alpha = 0.2).
    /// <c>double.NaN</c> — замеров ещё нет.
    /// </summary>
    public double AvgTtfbMs { get; set; } = double.NaN;
}

/// <summary>
/// Конверт JSON-файла статистики CDN-кластеров.
/// </summary>
public sealed class CdnHostStatsEnvelope
{
    /// <summary>Статистика по известным CDN-кластерам.</summary>
    public List<CdnClusterStats> Clusters { get; set; } = [];

    /// <summary>
    /// Накопленное суммарное число хитов по всем кластерам.
    /// Используется для вычисления адаптивного порога.
    /// </summary>
    public int TotalHits { get; set; }

    /// <summary>Время последнего сохранения на диск (UTC).</summary>
    public DateTime LastSavedUtc { get; set; }
}

// --- Section: Store ---

/// <summary>
/// Персистентное хранилище статистики YouTube CDN-кластеров.
/// <para>
/// Используется двумя способами:
/// </para>
/// <list type="number">
///   <item>
///     <b>Startup warmup:</b> <see cref="PreWarmTopClustersAsync"/> прогревает
///     top-N кластеров по score сразу после старта приложения — ещё до
///     того как пользователь нажмёт Play.
///   </item>
///   <item>
///     <b>Адаптивный timeout:</b> <see cref="GetAvgTtfbMs"/> возвращает EMA TTFB
///     для кластера, чтобы probe-timeout в <see cref="SessionCacheStore"/>
///     был пропорционален реальной задержке CDN.
///   </item>
/// </list>
/// <para>
/// Данные хранятся в <see cref="G.FilePath.CdnHostStats"/>.
/// Запись на диск — отложенная (dirty-flag + таймер 5 мин + graceful shutdown).
/// </para>
/// </summary>
internal static class CdnHostStatsStore
{
    private const int MaxClusters = 12;
    private const int ClusterEvictionDays = 30;
    private const double EmaAlpha = 0.2;
    private const int SaveIntervalMinutes = 5;

    private static readonly Lock _lock = new();
    private static CdnHostStatsEnvelope _data = new();
    private static bool _dirty;
    private static DateTime _lastSaveTime = DateTime.MinValue;

    // --- Section: Load / Save ---

    /// <summary>
    /// Загружает статистику с диска. Вызывается однократно при старте приложения.
    /// Безопасен при отсутствии файла или повреждённом JSON.
    /// </summary>
    public static void Load()
    {
        _lastSaveTime = DateTime.UtcNow;
        try
        {
            var path = G.FilePath.CdnHostStats;
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize(
                json,
                AppJsonContext.DefaultCompact.CdnHostStatsEnvelope);

            if (envelope is null)
                return;

            lock (_lock)
            {
                _data = envelope;
                EvictStaleClustersMustHoldLock();
            }

            Log.Debug($"[CdnHostStats] Loaded {_data.Clusters.Count} cluster(s), totalHits={_data.TotalHits}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[CdnHostStats] Load failed (starting fresh): {ex.Message}");
            lock (_lock) { _data = new CdnHostStatsEnvelope(); }
        }
    }

    /// <summary>
    /// Сохраняет статистику на диск. Вызывается по таймеру и при graceful shutdown.
    /// </summary>
    public static void Save()
    {
        CdnHostStatsEnvelope snapshot;

        lock (_lock)
        {
            if (!_dirty)
                return;

            _data.LastSavedUtc = DateTime.UtcNow;
            snapshot = _data;
            _dirty = false;
            _lastSaveTime = DateTime.UtcNow;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                snapshot,
                AppJsonContext.DefaultCompact.CdnHostStatsEnvelope);
            File.WriteAllText(G.FilePath.CdnHostStats, json);
            Log.Debug($"[CdnHostStats] Saved {snapshot.Clusters.Count} cluster(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[CdnHostStats] Save failed: {ex.Message}");
            lock (_lock) { _dirty = true; }
        }
    }

    // --- Section: Record ---

    /// <summary>
    /// Регистрирует успешное обращение к CDN-хосту.
    /// <para>
    /// Zero-alloc на hot path: только Lock + поиск в List (≤ 12 элементов) + int increment.
    /// Извлечение clusterId происходит ДО входа в Lock.
    /// </para>
    /// </summary>
    /// <param name="host">Hostname CDN-ноды (<c>rr3---sn-xxx.googlevideo.com</c>).</param>
    public static void RecordHit(string host)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return;

        lock (_lock)
        {
            var cluster = FindClusterMustHoldLock(clusterId);
            if (cluster is not null)
            {
                cluster.HitCount++;
                cluster.LastSeenUtc = DateTime.UtcNow;
                cluster.SampleHost = host;
            }
            else
            {
                if (_data.Clusters.Count >= MaxClusters)
                    EvictLowestScoreClusterMustHoldLock();

                _data.Clusters.Add(new CdnClusterStats
                {
                    ClusterId = clusterId,
                    SampleHost = host,
                    HitCount = 1,
                    LastSeenUtc = DateTime.UtcNow
                });
            }

            _data.TotalHits++;
            MarkDirtyMustHoldLock();
        }
    }

    /// <summary>
    /// Обновляет EMA TTFB для кластера после успешного прогрева.
    /// </summary>
    /// <param name="host">Hostname CDN-ноды.</param>
    /// <param name="ttfbMs">Измеренный TTFB в мс.</param>
    public static void RecordTtfb(string host, double ttfbMs)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return;

        lock (_lock)
        {
            var cluster = FindClusterMustHoldLock(clusterId);
            if (cluster is null)
                return;

            cluster.AvgTtfbMs = double.IsNaN(cluster.AvgTtfbMs)
                ? ttfbMs
                : cluster.AvgTtfbMs * (1.0 - EmaAlpha) + ttfbMs * EmaAlpha;

            MarkDirtyMustHoldLock();
        }

        FlushIfNeeded();
    }


    // --- Section: Query ---

    /// <summary>
    /// Возвращает EMA TTFB для кластера, к которому принадлежит хост.
    /// </summary>
    /// <param name="host">Hostname CDN-ноды.</param>
    /// <returns>EMA TTFB в мс, или <c>double.NaN</c> если данных нет.</returns>
    public static double GetAvgTtfbMs(string host)
    {
        var clusterId = ExtractClusterId(host);
        if (clusterId is null)
            return double.NaN;

        lock (_lock)
        {
            return FindClusterMustHoldLock(clusterId)?.AvgTtfbMs ?? double.NaN;
        }
    }

    // --- Section: Startup Warmup ---

    /// <summary>
    /// Прогревает top-N CDN-кластеров при старте приложения.
    /// <para>
    /// Ранжирование по score = <c>hitCount × 0.95^daysSinceLastSeen</c>.
    /// Адаптивный минимальный порог хитов зависит от <see cref="CdnHostStatsEnvelope.TotalHits"/>:
    /// </para>
    /// <list type="table">
    ///   <item><term>totalHits &lt; 30</term><description>порог = 3, max 1 кластер</description></item>
    ///   <item><term>30 ≤ totalHits &lt; 200</term><description>порог = 10, max 2 кластера</description></item>
    ///   <item><term>totalHits ≥ 200</term><description>порог = max(10, total/50), max 3 кластера</description></item>
    /// </list>
    /// <para>Fire-and-forget: ошибки не пробрасываются, прогрев best-effort.</para>
    /// </summary>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="ct">Токен отмены (lifetime приложения).</param>
    public static async Task PreWarmTopClustersAsync(HttpClient httpClient, CancellationToken ct)
    {
        string[] targets;

        lock (_lock)
        {
            if (_data.Clusters.Count == 0)
                return;

            var (minHits, maxClusters) = ComputeAdaptivePolicy(_data.TotalHits);

            var now = DateTime.UtcNow;
            int count = 0;
            var buffer = new string[Math.Min(maxClusters, _data.Clusters.Count)];

            // Сортировка без LINQ: insertion sort по score (N ≤ 12)
            Span<(double Score, int Index)> scored = stackalloc (double, int)[_data.Clusters.Count];
            int scoredCount = 0;

            for (int i = 0; i < _data.Clusters.Count; i++)
            {
                var c = _data.Clusters[i];
                if (c.HitCount < minHits)
                    continue;

                double days = (now - c.LastSeenUtc).TotalDays;
                double score = c.HitCount * Math.Pow(0.95, days);
                scored[scoredCount++] = (score, i);
            }

            for (int i = 0; i < scoredCount && count < maxClusters; i++)
            {
                int best = i;
                for (int j = i + 1; j < scoredCount; j++)
                {
                    if (scored[j].Score > scored[best].Score)
                        best = j;
                }

                if (best != i)
                    (scored[i], scored[best]) = (scored[best], scored[i]);

                buffer[count++] = _data.Clusters[scored[i].Index].SampleHost;
            }

            targets = buffer[..count];
        }

        if (targets.Length == 0)
            return;

        Log.Debug($"[CdnHostStats] Startup warmup: {targets.Length} cluster(s)");

        for (int i = 0; i < targets.Length; i++)
            _ = CdnConnectionPreWarmer.WarmSingleHostAsync(httpClient, targets[i], ct);

    }

    // --- Section: Periodic Save ---

    /// <summary>
    /// Сохраняет данные на диск если прошло достаточно времени с последнего сохранения.
    /// Вызывается из <see cref="RecordHit"/> когда dirty-флаг установлен давно.
    /// </summary>
    public static void FlushIfNeeded()
    {
        bool shouldSave;
        lock (_lock)
        {
            shouldSave = _dirty
                && (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= SaveIntervalMinutes;
        }

        if (shouldSave)
            Save();
    }

    // --- Section: Private Helpers ---

    private static (int MinHits, int MaxClusters) ComputeAdaptivePolicy(int totalHits) =>
        totalHits switch
        {
            < 30 => (3, 1),
            < 200 => (10, 2),
            _ => (Math.Max(10, totalHits / 50), 3)
        };

    private static CdnClusterStats? FindClusterMustHoldLock(string clusterId)
    {
        for (int i = 0; i < _data.Clusters.Count; i++)
        {
            if (string.Equals(_data.Clusters[i].ClusterId, clusterId, StringComparison.Ordinal))
                return _data.Clusters[i];
        }
        return null;
    }

    private static void EvictLowestScoreClusterMustHoldLock()
    {
        if (_data.Clusters.Count == 0)
            return;

        var now = DateTime.UtcNow;
        int worstIdx = 0;
        double worstScore = double.MaxValue;

        for (int i = 0; i < _data.Clusters.Count; i++)
        {
            double days = (now - _data.Clusters[i].LastSeenUtc).TotalDays;
            double score = _data.Clusters[i].HitCount * Math.Pow(0.95, days);
            if (score < worstScore)
            {
                worstScore = score;
                worstIdx = i;
            }
        }

        _data.Clusters.RemoveAt(worstIdx);
    }

    private static void EvictStaleClustersMustHoldLock()
    {
        var cutoff = DateTime.UtcNow.AddDays(-ClusterEvictionDays);
        for (int i = _data.Clusters.Count - 1; i >= 0; i--)
        {
            if (_data.Clusters[i].LastSeenUtc < cutoff)
                _data.Clusters.RemoveAt(i);
        }
    }

    private static void MarkDirtyMustHoldLock()
    {
        _dirty = true;
    }

    /// <summary>
    /// Извлекает ClusterId из CDN-hostname. Zero-alloc через <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    /// <example>
    /// <c>rr3---sn-4g5ednle.googlevideo.com</c> → <c>sn-4g5ednle</c>
    /// </example>
    internal static string? ExtractClusterId(string host)
    {
        var span = host.AsSpan();

        var separatorIdx = span.IndexOf("---".AsSpan(), StringComparison.Ordinal);
        if (separatorIdx < 0)
            return null;

        var after = span[(separatorIdx + 3)..];

        var domainIdx = after.IndexOf(".googlevideo.com".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (domainIdx <= 0)
            return null;

        return new string(after[..domainIdx]);
    }
}