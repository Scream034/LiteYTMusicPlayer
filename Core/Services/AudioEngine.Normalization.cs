using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Normalization State

    private readonly Dictionary<string, (float IntegratedLufs, LoudnessSource Source)> _normalizationBatch =
        new(StringComparer.Ordinal);

    #endregion

    #region Pipeline Configuration

    /// <summary>
    /// Конфигурирует pipeline перед открытием gate: громкость, нормализация и кроссфейдер.
    /// </summary>
    private void ConfigurePipelineBeforeStart(AudioPipeline pipeline, string? trackId)
    {
        trackId ??= pipeline.StreamInfo.TrackId;

        float volumeGain = ComputeFinalGain();
        _currentGain = volumeGain;
        _player.SetVolumeGain(volumeGain);

        var audioSettings = _library.Settings.Audio;

        bool isFullCache = pipeline.Source is Audio.Sources.LocalFileSource;
        int preScanDurationMs = isFullCache ? PreScanDurationFullCacheMs : PreScanDurationStreamingMs;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode,
            preScanDurationMs);

        pipeline.Analyzer.Configure(normConfig);

        Log.Debug($"[AudioEngine] Configuring pipeline for '{trackId}'. " +
                  $"Normalization: {normConfig.Enabled}, Mode: {normConfig.Mode}, " +
                  $"PreScan: {preScanDurationMs / 1000}s");

        if (normConfig.Enabled && !string.IsNullOrEmpty(trackId))
        {
            var registryTrack = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
            var currentTrack = CurrentTrack;
            var track = registryTrack ?? (currentTrack?.Id == trackId ? currentTrack : null);
            var cacheEntry = FindNormalizationCacheEntry(trackId);

            if (track != null && cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            float resolvedGain = track != null
                ? NormalizationGainResolver.Resolve(track, normConfig)
                : float.NaN;

            if (float.IsNaN(resolvedGain)
                && cacheEntry?.IntegratedLufs is float cacheIntegratedLufs
                && float.IsFinite(cacheIntegratedLufs))
            {
                resolvedGain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(
                    cacheIntegratedLufs,
                    normConfig);
            }

#if DEBUG
            if (track != null)
            {
                Log.Debug($"[AudioEngine] Track resolved: ID={track.Id}, Title='{track.Title}' " +
                          $"| Source: {(registryTrack != null ? "Registry" : "CurrentTrackFallback")} " +
                          $"| Integrated LUFS: {(track.HasIntegratedLufs ? track.IntegratedLufs.ToString("F2") : "NaN")} " +
                          $"| LUFS Source: {track.IntegratedLufsSource} " +
                          $"| Cache LUFS: {(cacheEntry?.IntegratedLufs is float clufs ? clufs.ToString("F2") : "null")} " +
                          $"| Cache LUFS Source: {(cacheEntry != null ? ((LoudnessSource)cacheEntry.IntegratedLufsSource).ToString() : "null")} " +
                          $"| Resolved Gain: {(float.IsNaN(resolvedGain) ? "NaN" : resolvedGain.ToString("F4"))}");
            }
#endif

            if (!float.IsNaN(resolvedGain))
            {
                pipeline.Analyzer.LockResolvedGain(resolvedGain);
                Log.Info($"[AudioEngine] Normalization gain locked from LUFS metadata: {resolvedGain:F4}x for {trackId}");
            }
            else if (track != null && !(pipeline.Source is Audio.Sources.CachingStreamSource { IsFullyBuffered: false }))
            {
                Log.Warn($"[AudioEngine] Normalization resolver returned NaN for {trackId}. EBU R128 Pre-scan is REQUIRED.");
            }
        }

        pipeline.SnapCrossfaderToGain();
    }

    #endregion

    #region Loudness Persistence & Sync

    /// <summary>
    /// Сохраняет resolved integrated loudness в runtime-модели, AudioCache и очередь DB persistence.
    /// </summary>
    private void CommitIntegratedLufs(string trackId, float integratedLufs, LoudnessSource source)
    {
        if (string.IsNullOrEmpty(trackId) || !float.IsFinite(integratedLufs))
            return;

        var canonical = _library.GetTrack(trackId);
        canonical?.SetIntegratedLufs(integratedLufs, source);

        var registryTrack = _trackRegistry.TryGet(trackId);
        if (registryTrack != null && !ReferenceEquals(registryTrack, canonical))
            registryTrack.SetIntegratedLufs(integratedLufs, source);

        var current = CurrentTrack;
        if (current != null
            && current.Id == trackId
            && !ReferenceEquals(current, canonical)
            && !ReferenceEquals(current, registryTrack))
        {
            current.SetIntegratedLufs(integratedLufs, source);
        }

        AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(trackId, integratedLufs, source);

        _pendingNormalizationWrites.Enqueue((trackId, integratedLufs, source));
    }

    /// <summary>
    /// Асинхронно сохраняет отложенные записи integrated loudness в БД.
    /// </summary>
    private async Task FlushPendingNormalizationWritesAsync(CancellationToken ct)
    {
        if (_pendingNormalizationWrites.IsEmpty) return;

        lock (_normalizationBatch)
        {
            _normalizationBatch.Clear();
            while (_pendingNormalizationWrites.TryDequeue(out var pending))
                _normalizationBatch[pending.TrackId] = (pending.IntegratedLufs, pending.Source);
        }

        if (_normalizationBatch.Count == 0) return;

        foreach (var (trackId, data) in _normalizationBatch)
        {
            try
            {
                var track = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
                if (track != null)
                {
                    track.SetIntegratedLufs(data.IntegratedLufs, data.Source);
                    await _library.AddOrUpdateTrackAsync(track, ct).ConfigureAwait(false);
                }
                else
                {
                    await _library.SaveTrackNormalizationMetadataAsync(
                        trackId,
                        data.IntegratedLufs,
                        (int)data.Source,
                        ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[AudioEngine] Failed to persist normalization metadata for {trackId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Синхронно сохраняет все отложенные записи integrated loudness в базу данных.
    /// </summary>
    private void FlushPendingNormalizationWritesSync()
    {
        if (_pendingNormalizationWrites.IsEmpty) return;

        lock (_normalizationBatch)
        {
            _normalizationBatch.Clear();
            while (_pendingNormalizationWrites.TryDequeue(out var pending))
                _normalizationBatch[pending.TrackId] = (pending.IntegratedLufs, pending.Source);

            if (_normalizationBatch.Count == 0) return;

            foreach (var (trackId, data) in _normalizationBatch)
            {
                try
                {
                    var track = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
                    if (track != null)
                    {
                        track.SetIntegratedLufs(data.IntegratedLufs, data.Source);
                        _library.AddOrUpdateTrackAsync(track, CancellationToken.None)
                            .ConfigureAwait(false)
                            .GetAwaiter()
                            .GetResult();
                    }
                    else
                    {
                        _library.SaveTrackNormalizationMetadataAsync(
                                trackId,
                                data.IntegratedLufs,
                                (int)data.Source,
                                CancellationToken.None)
                            .ConfigureAwait(false)
                            .GetAwaiter()
                            .GetResult();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"[AudioEngine] Failed to sync persist normalization metadata for {trackId}: {ex.Message}");
                }
            }
        }
    }

    #endregion

    #region Dynamic Gain Application

    /// <summary>
    /// Обновляет gain нормализации в running pipeline при получении более точного LUFS.
    /// </summary>
    private void UpdateRunningPipelineGain(string trackId, float integratedLufs)
    {
        if (!float.IsFinite(integratedLufs)) return;

        var pipeline = _player.GetActivePipeline();
        if (pipeline == null) return;
        if (!string.Equals(pipeline.StreamInfo.TrackId, trackId, StringComparison.Ordinal)) return;

        var audioSettings = _library.Settings.Audio;
        if (!audioSettings.NormalizationEnabled) return;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        float gain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(integratedLufs, normConfig);
        if (float.IsNaN(gain)) return;

        pipeline.Analyzer.LockResolvedGain(gain);
        Log.Info($"[AudioEngine] Normalization gain updated from YouTube LUFS: {gain:F4}x (lufs={integratedLufs:F2}) for {trackId}");
    }

    /// <summary>
    /// Пробрасывает актуальные настройки нормализации в активный pipeline.
    /// </summary>
    private void ApplyNormalizationToPipeline()
    {
        var pipeline = _player.GetActivePipeline();
        if (pipeline == null) return;

        var audioSettings = _library.Settings.Audio;
        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode);

        var track = CurrentTrack;
        AudioCacheEntry? cacheEntry = null;

        if (track != null)
        {
            cacheEntry = FindNormalizationCacheEntry(track.Id);
            if (cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);
        }

        float resolvedGain = normConfig.Enabled
            ? NormalizationGainResolver.Resolve(track, normConfig)
            : float.NaN;

        if (float.IsNaN(resolvedGain)
            && cacheEntry?.IntegratedLufs is float cacheIntegratedLufs
            && float.IsFinite(cacheIntegratedLufs))
        {
            resolvedGain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(
                cacheIntegratedLufs,
                normConfig);
        }

        pipeline.Analyzer.Configure(normConfig);

        if (normConfig.Enabled && !float.IsNaN(resolvedGain))
            pipeline.Analyzer.LockResolvedGain(resolvedGain);
    }

    #endregion
}