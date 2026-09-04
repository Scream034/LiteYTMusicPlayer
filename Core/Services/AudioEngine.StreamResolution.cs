using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Stream Resolution

    /// <summary>
    /// Разрешает источник аудиопотока для воспроизведения трека.
    /// </summary>
    /// <param name="track">Метаданные трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <param name="seekPosition">Начальная позиция воспроизведения при перемотке.</param>
    /// <returns>Дескриптор готового аудиопотока.</returns>
    private async Task<ResolvedStreamDescriptor> ResolveStreamAsync(
        TrackInfo track,
        CancellationToken ct,
        TimeSpan? seekPosition = null)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        Log.Debug($"[AudioEngine] ResolveStreamAsync start: track={track.Id}, seek={seekPosition?.TotalMilliseconds ?? 0}ms, requestedFormat={requested.Format?.ToContainerName() ?? "-"}, requestedBitrate={requested.BitrateKbps}");

        var rawId = track.GetRawIdSpan().ToString();

        // --- Path 0: Downloaded local files (Downloads folder) ---
        if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
        {
            var downloadedFormat = AudioSourceFactory.DetectFormat(track.LocalPath);
            if (downloadedFormat == AudioFormat.Unknown) downloadedFormat = AudioFormat.WebM;

            bool isUserOverrodeFormat = requested.HasFormat && requested.Format != downloadedFormat;

            if (!isUserOverrodeFormat)
            {
                var fileInfo = new FileInfo(track.LocalPath);
                var codec = AudioSourceFactory.GetCodecForFormat(downloadedFormat);
                int fileBitrateKbps = track.Duration.TotalSeconds > 0
                    ? Math.Max((int)(fileInfo.Length * 8 / track.Duration.TotalSeconds / 1000), 32)
                    : 128;

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = track.LocalPath,
                    Format = downloadedFormat,
                    Codec = codec,
                    BitrateKbps = fileBitrateKbps,
                    ContentLengthBytes = fileInfo.Length,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync LOCAL DOWNLOAD -> {descriptor}");
                TryEnrichIntegratedLufsFromLocalSources(track);
                return descriptor;
            }
        }

        // --- Path 1: Full disk cache (exact match by format+bitrate bucket) ---
        if (requested.HasFormat && requested.HasBitrate)
        {
            string exactCacheKey = AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            if (AudioSourceFactory.GlobalCache is { } exactCache && exactCache.IsFullyCached(exactCacheKey))
            {
                var exactEntry = exactCache.GetCacheInfo(exactCacheKey);
                if (exactEntry != null && IsCacheSizePlausible(exactEntry, track.Duration))
                {
                    TrackNormalizationHydrator.HydrateNormalization(track, exactEntry);
                    TryEnrichIntegratedLufsFromLocalSources(track);

                    var descriptor = new ResolvedStreamDescriptor
                    {
                        TrackId = track.Id,
                        Url = "",
                        Format = exactEntry.Format,
                        Codec = exactEntry.Codec,
                        BitrateKbps = exactEntry.Bitrate,
                        ContentLengthBytes = exactEntry.TotalSize,
                        Origin = StreamSource.DiskCacheFull
                    };

                    Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE (exact) -> {descriptor}");
                    return descriptor;
                }
            }
        }

        // --- Path 1b: Full disk cache (any format, no user preference) ---
        if (!requested.HasFormat)
        {
            var fullCache = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                         ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

            if (fullCache != null && IsCacheSizePlausible(fullCache.Value.Entry, track.Duration))
            {
                var entry = fullCache.Value.Entry;
                TrackNormalizationHydrator.HydrateNormalization(track, entry);
                TryEnrichIntegratedLufsFromLocalSources(track);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = "",
                    Format = entry.Format,
                    Codec = entry.Codec,
                    BitrateKbps = entry.Bitrate,
                    ContentLengthBytes = entry.TotalSize,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 2: Partial cache fast-start ---
        var bootstrapCache = TryGetPartialBootstrapCache(track, seekPosition);
        if (bootstrapCache != null)
        {
            TrackNormalizationHydrator.HydrateNormalization(track, bootstrapCache);
            TryEnrichIntegratedLufsFromLocalSources(track);

            if (TryGetCompatibleContinuationUrl(track, bootstrapCache, out var eagerUrl))
            {
                Log.Info($"[AudioEngine] Partial-cache fast start with eager continuation URL: {track.Id}");

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = eagerUrl,
                    Format = bootstrapCache.Format,
                    Codec = bootstrapCache.Codec,
                    BitrateKbps = bootstrapCache.Bitrate,
                    ContentLengthBytes = bootstrapCache.TotalSize,
                    Origin = StreamSource.DiskCachePartial
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (eager URL) -> {descriptor}");
                return descriptor;
            }

            _ = PrimeContinuationUrlAsync(track, bootstrapCache, ct);

            Log.Info($"[AudioEngine] Partial-cache fast start: {track.Id}");

            var lazyDescriptor = new ResolvedStreamDescriptor
            {
                TrackId = track.Id,
                Url = "",
                Format = bootstrapCache.Format,
                Codec = bootstrapCache.Codec,
                BitrateKbps = bootstrapCache.Bitrate,
                ContentLengthBytes = bootstrapCache.TotalSize,
                Origin = StreamSource.DiskCachePartial
            };

            Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (lazy URL) -> {lazyDescriptor}");
            return lazyDescriptor;
        }

        ct.ThrowIfCancellationRequested();

        // --- Path 3: Session cache (disk manifest with HEAD probe) ---
        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry is { Variants.Count: > 0 })
        {
            var cacheEntry = FindNormalizationCacheEntry(track.Id);
            if (cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                var format = selectedVariant.Format;
                var codec = selectedVariant.CodecType != AudioCodec.Unknown
                    ? selectedVariant.CodecType
                    : AudioSourceFactory.GetCodecForFormat(format);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Itag = selectedVariant.Itag,
                    Format = format,
                    Codec = codec,
                    BitrateKbps = selectedVariant.Bitrate / 1000,
                    ContentLengthBytes = selectedVariant.Clen,
                    Url = selectedVariant.Url,
                    ExpireUtc = diskEntry.ExpireUtc,
                    CdnHost = diskEntry.CdnHost,
                    IntegratedLufs = diskEntry.IntegratedLufs,
                    LanguageCode = selectedVariant.LanguageCode,
                    IsDefaultLanguage = selectedVariant.IsDefaultLanguage,
                    Origin = StreamSource.SessionCache
                };

                Log.Info($"[AudioEngine] Session cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 4: Provider memory cache (RAM manifest) ---
        var memDescriptor = _youtube.TryGetCachedStreamDescriptor(
            track.Id,
            requested.Format,
            requested.BitrateKbps);

        if (memDescriptor != null)
        {
            if (memDescriptor.Value.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < memDescriptor.Value.ExpireUtc)
            {
                var cacheEntry = FindNormalizationCacheEntry(track.Id);
                if (cacheEntry != null)
                    TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

                var descriptor = memDescriptor.Value with { TrackId = track.Id };
                Log.Info($"[AudioEngine] Provider memory cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
            else
            {
                Log.Debug($"[AudioEngine] Provider memory cache hit ignored due to ExpireUtc passed: {track.Id}");
            }
        }

        // --- Path 5: YouTube API call (cold path) ---
        var freshDescriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

        Log.Info($"[AudioEngine] ResolveStreamAsync YOUTUBE API -> {freshDescriptor}");
        return freshDescriptor;
    }

    #endregion

    #region URL Acquisition & Continuation

    private Task<ContinuationUrlResult?> GetOrStartContinuationUrlAcquisitionAsync(TrackInfo track)
    {
        if (_pendingUrlAcquisitions.TryGetValue(track.Id, out var existing))
            return existing;

        var sessionToken = GetSessionToken();
        var created = AcquireContinuationUrlCoreAsync(track, sessionToken);

        if (_pendingUrlAcquisitions.TryAdd(track.Id, created))
        {
            _ = RemovePendingContinuationAcquisitionAsync(track.Id, created);
            return created;
        }

        return _pendingUrlAcquisitions[track.Id];
    }

    private async Task<ContinuationUrlResult?> AcquireContinuationUrlCoreAsync(
      TrackInfo track,
      CancellationToken ct)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry != null)
        {
            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                return new ContinuationUrlResult(
                    selectedVariant.Url,
                    selectedVariant.Clen,
                    selectedVariant.Bitrate / 1000,
                    selectedVariant.Format,
                    selectedVariant.CodecType,
                    diskEntry.IntegratedLufs);
            }
        }

        var descriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false);
        if (descriptor is null || !descriptor.Value.HasLiveUrl)
            return null;

        var d = descriptor.Value;

        return new ContinuationUrlResult(
            d.Url,
            d.ContentLengthBytes,
            d.BitrateKbps,
            d.Format,
            d.Codec,
            d.IntegratedLufs);
    }

    private async Task RemovePendingContinuationAcquisitionAsync(
        string trackId,
        Task<ContinuationUrlResult?> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }

        _pendingUrlAcquisitions.TryRemove(
            new KeyValuePair<string, Task<ContinuationUrlResult?>>(trackId, task));
    }

    private async ValueTask<string?> RefreshUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;

        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
            ?? _trackRegistry.TryGet(trackId)
            ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        if (track == null || IsSealedFailedTrack(trackId)) return null;

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);

        try
        {
            SessionCacheStore.Invalidate(trackId);
            _youtube.InvalidateMemoryCache(trackId);

            Log.Info($"[AudioEngine] 403 refresh: per-track caches invalidated for {trackId}");

            var descriptor = await Task.Run(
                () => _youtube.RefreshStreamAsync(track, false, linked.Token),
                linked.Token).ConfigureAwait(false);

            if (descriptor is { HasLiveUrl: true })
            {
                Log.Info($"[AudioEngine] Soft refresh returned new URL for {trackId}");
                return descriptor.Value.Url;
            }

            Log.Warn($"[AudioEngine] Soft refresh failed for {trackId}, falling back to force refresh");

            descriptor = await Task.Run(
                () => _youtube.RefreshStreamAsync(track, true, linked.Token),
                linked.Token).ConfigureAwait(false);

            return descriptor is { HasLiveUrl: true } ? descriptor.Value.Url : null;
        }
        catch (Exception) when (linked.IsCancellationRequested
            || sessionToken.IsCancellationRequested
            || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(trackId);
            RaiseError(ex);
            return null;
        }
    }

    private async ValueTask<string?> AcquireUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId))
            return null;

        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
            ?? _trackRegistry.TryGet(trackId)
            ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        if (track == null || IsSealedFailedTrack(trackId))
            return null;

        try
        {
            var task = GetOrStartContinuationUrlAcquisitionAsync(track);
            var result = await task.WaitAsync(ct).ConfigureAwait(false);
            return result?.Url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Acquire URL skipped for {trackId}: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Partial Cache Fast Start

    private static int ComputePartialCacheBootstrapBytes(int bitrateKbps)
    {
        double bitrateBytesPerSec = Math.Max(1, bitrateKbps) * 1000.0 / 8.0;
        int bytes = (int)Math.Ceiling(bitrateBytesPerSec * PartialCacheBootstrapTargetMs / 1000.0);
        return Math.Clamp(bytes, PartialCacheBootstrapMinBytes, PartialCacheBootstrapMaxBytes);
    }

    private AudioCacheEntry? TryGetPartialBootstrapCache(TrackInfo track, TimeSpan? seekPosition)
    {
        if (seekPosition is { TotalMilliseconds: > 0 })
            return null;

        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
            return null;

        int bitrateHint = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat).BitrateKbps;
        if (bitrateHint <= 0)
            bitrateHint = 160;

        int requiredBytes = ComputePartialCacheBootstrapBytes(bitrateHint);
        return cacheManager.FindBestStartupCache(track.Id, requiredBytes);
    }

    private bool TryAttachPrimedContinuationUrlToActiveSource(TrackInfo track, string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
            return false;

        var current = CurrentTrack;
        if (current == null || !string.Equals(current.Id, track.Id, StringComparison.Ordinal))
            return false;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is not Audio.Sources.CachingStreamSource cachingSource)
            return false;

        if (cachingSource.TryAttachContinuationUrl(url))
        {
            Log.Debug($"[AudioEngine] Primed continuation URL attached to live source: {track.Id}");
            return true;
        }

        return false;
    }

    private async Task PrimeContinuationUrlAsync(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || IsSealedFailedTrack(track.Id))
                return;

            if (TryGetCompatibleContinuationUrl(track, expectedEntry, out var existingUrl))
            {
                TryAttachPrimedContinuationUrlToActiveSource(track, existingUrl);
                return;
            }

            var result = await GetOrStartContinuationUrlAcquisitionAsync(track)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            if (result == null || string.IsNullOrEmpty(result.Value.Url))
                return;

            if (!IsContinuationVariantCompatible(expectedEntry, result.Value.Format, result.Value.Bitrate))
            {
                Log.Warn($"[AudioEngine] Continuation priming variant mismatch for {track.Id}: " +
                         $"expected={expectedEntry.Format}/{expectedEntry.Bitrate}kbps, " +
                         $"actual={result.Value.Format}/{result.Value.Bitrate}kbps");
                return;
            }

            TryAttachPrimedContinuationUrlToActiveSource(track, result.Value.Url);

            if (float.IsFinite(result.Value.IntegratedLufs))
            {
                CommitIntegratedLufs(
                    track.Id,
                    result.Value.IntegratedLufs,
                    LoudnessSource.YoutubePerceptual);

                UpdateRunningPipelineGain(track.Id, result.Value.IntegratedLufs);
            }

            Log.Info($"[AudioEngine] Partial-cache continuation primed: {track.Id} " +
                     $"({result.Value.Codec.ToDisplayName()}/{result.Value.Bitrate}kbps)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Continuation priming skipped for {track.Id}: {ex.Message}");
        }
    }

    #endregion

    #region Cache & Metadata Helpers

    /// <summary>
    /// Выбирает вариант из записи манифеста, предпочитая хосты не в CDN blacklist.
    /// </summary>
    private static VariantEntry? SelectBestVariantFromEntry(
        List<VariantEntry> variants,
        AudioFormat? preferredFormat)
    {
        if (variants.Count == 0) return null;

        var blacklist = Audio.AudioSourceFactory.CdnBlacklist;

        // Сначала ищем вариант с нужным форматом, не в blacklist
        if (preferredFormat is { } fmt && fmt != AudioFormat.Unknown)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].Format != fmt) continue;
                if (!blacklist.IsBlockedUrl(variants[i].Url)) return variants[i];
            }

            // Все нужного формата заблокированы — берём любой того формата
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].Format == fmt) return variants[i];
            }
        }

        // Без формата — любой не заблокированный
        for (int i = 0; i < variants.Count; i++)
        {
            if (!blacklist.IsBlockedUrl(variants[i].Url)) return variants[i];
        }

        // Все заблокированы — fallback на первый (YouTube API даст другой CDN)
        Log.Warn("[AudioEngine] All manifest variants are CDN-blacklisted — using first anyway");
        return variants[0];
    }

    private static bool IsContinuationVariantCompatible(
         AudioCacheEntry expectedEntry,
         AudioFormat format,
         int bitrate)
    {
        if (format == AudioFormat.Unknown)
            return false;

        if (format != expectedEntry.Format)
            return false;

        if (bitrate <= 0 || expectedEntry.Bitrate <= 0)
            return true;

        string candidateKey = AudioSourceFactory.BuildCacheKey(
            expectedEntry.TrackId,
            format,
            bitrate);

        return string.Equals(candidateKey, expectedEntry.CacheKey, StringComparison.Ordinal);
    }

    private bool TryGetCompatibleContinuationUrl(
     TrackInfo track,
     AudioCacheEntry expectedEntry,
     out string url)
    {
        url = string.Empty;

        var manifest = SessionCacheStore.GetManifest(track.Id);

        if (manifest != null && IsContinuationUrlLikelyFresh(manifest))
        {
            for (int i = 0; i < manifest.Variants.Count; i++)
            {
                var variant = manifest.Variants[i];
                if (IsContinuationVariantCompatible(expectedEntry, variant.Format, variant.Bitrate / 1000))
                {
                    url = variant.Url;
                    return true;
                }
            }
        }

        var rawId = track.GetRawIdSpan().ToString();
        var descriptor = _youtube.TryGetCachedStreamDescriptor(
            rawId,
            expectedEntry.Format,
            expectedEntry.Bitrate);

        if (descriptor is { HasLiveUrl: true } d &&
            IsContinuationVariantCompatible(expectedEntry, d.Format, d.BitrateKbps))
        {
            if (d.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < d.ExpireUtc)
            {
                url = d.Url;
                return true;
            }
        }

        return false;
    }

    private static bool IsContinuationUrlLikelyFresh(
        TrackManifestEntry manifest,
        int safetyBufferMinutes = 5)
    {
        if (manifest.ExpireUtc == default || manifest.ExpireUtc == DateTime.MinValue)
            return true;

        return DateTime.UtcNow.AddMinutes(safetyBufferMinutes) < manifest.ExpireUtc;
    }

    private static AudioCacheEntry? FindNormalizationCacheEntry(string trackId)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache == null || string.IsNullOrEmpty(trackId))
            return null;

        return cache.FindBestCacheByTrackId(trackId) ?? cache.FindBestStartupCache(trackId, 0);
    }

    private static void TryEnrichIntegratedLufsFromLocalSources(TrackInfo track)
    {
        if (track.HasIntegratedLufs)
            return;

        var manifest = SessionCacheStore.GetManifest(track.Id);
        if (manifest is null)
            return;

        if (!float.IsFinite(manifest.IntegratedLufs))
            return;

        track.SetIntegratedLufs(
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(
            track.Id,
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        Log.Debug($"[AudioEngine] Enriched LUFS from SessionCache: {track.Id} → {manifest.IntegratedLufs:F2} LUFS");
    }

    /// <summary>
    /// Проверяет, является ли размер закэшированного файла правдоподобным для указанной длительности трека.
    /// </summary>
    /// <param name="entry">Запись кэша.</param>
    /// <param name="duration">Длительность трека из метаданных.</param>
    /// <returns><c>true</c>, если размер файла соответствует физике кодека.</returns>
    private static bool IsCacheSizePlausible(AudioCacheEntry entry, TimeSpan duration)
    {
        if (duration <= TimeSpan.FromSeconds(5))
            return entry.TotalSize > 32 * 1024;

        int bitrate = entry.Bitrate > 0 ? entry.Bitrate : 128;
        long minExpectedBytes = (long)(duration.TotalSeconds * bitrate * 1000.0 / 8.0 * 0.50);
        return entry.TotalSize >= minExpectedBytes;
    }

    #endregion
}