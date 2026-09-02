using System.Diagnostics;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Benchmark тесты производительности EBU R128 pre-scan.
/// Измеряет время isolated scan для Opus и AAC треков из локального кэша.
/// </summary>
public static class PreScanBenchmarks
{
    private const int WarmupRuns = 1;
    private const int MeasureRuns = 3;
    private const float TargetLufs = -14f;
    private const float MaxGain = 3.0f;

    /// <summary>
    /// Измеряет время isolated pre-scan для всех полностью закэшированных треков.
    /// Выводит min/avg/max и разбивку по кодеку.
    /// </summary>
    [TestMethod(
        TestCategory.Benchmark,
        "Pre-Scan: Isolated scan timing (30s scope)",
        Group = "PreScan",
        Order = 10,
        TimeoutSeconds = 120)]
    public static async Task BenchmarkIsolatedPreScanAsync()
    {
        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
        {
            Log.Warn("[Benchmark] AudioSourceFactory.GlobalCache is null — skipping");
            return;
        }

        var candidates = CollectCandidates(cacheManager);
        if (candidates.Count == 0)
        {
            Log.Warn("[Benchmark] No fully cached tracks found. Play some tracks first.");
            return;
        }

        Log.Info($"[Benchmark] Found {candidates.Count} cached track(s). Running {MeasureRuns} scan(s) each.");
        Console.WriteLine();

        var opusResults = new List<long>();
        var aacResults = new List<long>();

        foreach (var (entry, filePath) in candidates)
        {
            Console.WriteLine($"  Track: {entry.TrackId} | {entry.Codec} | {entry.Bitrate}kbps | {entry.TotalSize / 1024}KB");

            // Прогрев (OS page cache)
            for (int i = 0; i < WarmupRuns; i++)
            {
                await RunTimedScanAsync(filePath, entry.Codec, suppressLog: true).ConfigureAwait(false);
            }

            // Замер
            var times = new long[MeasureRuns];
            for (int i = 0; i < MeasureRuns; i++)
            {
                times[i] = await RunTimedScanAsync(filePath, entry.Codec, suppressLog: false).ConfigureAwait(false);
            }

            long min = times.Min();
            long avg = (long)times.Average();
            long max = times.Max();

            Console.WriteLine($"    min={min}ms  avg={avg}ms  max={max}ms");
            Console.WriteLine();

            if (entry.Codec == AudioCodec.Opus)
                opusResults.AddRange(times);
            else if (entry.Codec == AudioCodec.Aac)
                aacResults.AddRange(times);
        }

        PrintSummary("Opus", opusResults);
        PrintSummary("AAC (SharpJaad)", aacResults);
    }

    /// <summary>
    /// Сравнительный benchmark: isolated (30s) vs legacy (через full source, 120s если применимо).
    /// Запускается только при наличии хотя бы одного Opus трека в кэше.
    /// </summary>
    [TestMethod(
        TestCategory.Benchmark,
        "Pre-Scan: Isolated 30s vs scope comparison",
        Group = "PreScan",
        Order = 20,
        TimeoutSeconds = 120)]
    public static async Task BenchmarkScopeComparisonAsync()
    {
        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
        {
            Log.Warn("[Benchmark] AudioSourceFactory.GlobalCache is null — skipping");
            return;
        }

        var candidates = CollectCandidates(cacheManager)
            .Where(c => c.Entry.Codec == AudioCodec.Opus)
            .Take(3)
            .ToList();

        if (candidates.Count == 0)
        {
            Log.Warn("[Benchmark] No Opus tracks in cache — skipping scope comparison");
            return;
        }

        Console.WriteLine("\n  Scope comparison (Opus tracks):\n");

        foreach (var (entry, filePath) in candidates)
        {
            Console.WriteLine($"  {entry.TrackId} | {entry.TotalSize / 1024 / 1024}MB");

            long ms30 = await RunTimedScanWithScopeAsync(filePath, entry.Codec, scanSeconds: 30f)
                .ConfigureAwait(false);
            long ms60 = await RunTimedScanWithScopeAsync(filePath, entry.Codec, scanSeconds: 60f)
                .ConfigureAwait(false);

            double ratio = ms60 > 0 ? (double)ms30 / ms60 : 1.0;
            Console.WriteLine($"    30s: {ms30}ms | 60s: {ms60}ms | ratio: {ratio:F2}x");
        }
    }

    // --- Helpers ---

    private static List<(AudioCacheEntry Entry, string FilePath)> CollectCandidates(
     AudioCacheManager cacheManager)
    {
        var byCodec = new Dictionary<AudioCodec, List<(AudioCacheEntry, string)>>();

        foreach (var entry in cacheManager.GetAllCompleteEntries())
        {
            var path = cacheManager.GetCachePath(entry.CacheKey);
            if (!File.Exists(path)) continue;

            if (!byCodec.TryGetValue(entry.Codec, out var list))
            {
                list = [];
                byCodec[entry.Codec] = list;
            }

            if (list.Count < 2) // максимум 2 трека на кодек
                list.Add((entry, path));
        }

        var result = new List<(AudioCacheEntry, string)>();
        foreach (var list in byCodec.Values)
            result.AddRange(list);

        return result;
    }

    private static async Task<long> RunTimedScanAsync(
        string filePath,
        AudioCodec codec,
        bool suppressLog)
    {
        return await RunTimedScanWithScopeAsync(filePath, codec, scanSeconds: 30f, suppressLog)
            .ConfigureAwait(false);
    }

    private static async Task<long> RunTimedScanWithScopeAsync(
     string filePath,
     AudioCodec codec,
     float scanSeconds,
     bool suppressLog = true)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var sw = Stopwatch.StartNew();

        try
        {
            var method = typeof(AudioPipeline).GetMethod(
                "RunIsolatedPreScanAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) ?? throw new InvalidOperationException("RunIsolatedPreScanAsync not found");

            // Теперь передаём scanSeconds — ранее он игнорировался
            var task = (Task<(float, float)>)method.Invoke(
                null,
                [filePath, codec, TargetLufs, MaxGain, cts.Token, scanSeconds])!;

            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!suppressLog)
                Console.WriteLine($"    [!] Scan error: {ex.Message}");
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static void PrintSummary(string codecName, List<long> times)
    {
        if (times.Count == 0)
        {
            Console.WriteLine($"  {codecName}: no data");
            return;
        }

        Console.WriteLine($"  {codecName} summary ({times.Count} sample(s)):");
        Console.WriteLine($"    min={times.Min()}ms  avg={(long)times.Average()}ms  max={times.Max()}ms");
        Console.WriteLine($"    p50={Percentile(times, 50)}ms  p95={Percentile(times, 95)}ms");
        Console.WriteLine();
    }

    private static long Percentile(List<long> sorted, int p)
    {
        var s = sorted.OrderBy(x => x).ToList();
        int idx = (int)Math.Ceiling(p / 100.0 * s.Count) - 1;
        return s[Math.Clamp(idx, 0, s.Count - 1)];
    }
}