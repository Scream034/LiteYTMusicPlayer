using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Volume State

    private int _volumePercent;
    private float _currentGain;
    private bool _volumeInitialized;
    private readonly Lock _volumeLock = new();

    /// <summary>
    /// Кэшированный словарь для flush normalization metadata — reuse через Clear().
    /// </summary>
    private readonly Dictionary<string, (float IntegratedLufs, LoudnessSource Source)> _normalizationBatch =
        new(StringComparer.Ordinal);

    private const int VolumeSaveIntervalMs = 2000;

    #endregion

    #region Public API

    /// <summary>Возвращает текущую громкость в процентах.</summary>
    public float GetVolume() => _volumePercent;

    /// <summary>Устанавливает громкость мгновенно.</summary>
    public void SetVolumeInstant(float value)
    {
        lock (_volumeLock)
        {
            _volumePercent = Math.Clamp(
                (int)Math.Round(value), 0,
                Math.Max(_library.Settings.MaxVolumeLimit, 100));
        }
        ApplyGainToPipeline();
    }

    /// <summary>Сохраняет громкость в настройки немедленно.</summary>
    public void SaveVolumeNow() => _library.UpdateSettings(s => s.Volume = _volumePercent);

    /// <summary>Обрабатывает изменение максимального лимита громкости.</summary>
    public void OnMaxVolumeLimitChanged(int newMaxVolume)
    {
        lock (_volumeLock)
        {
            if (_volumePercent > newMaxVolume) _volumePercent = newMaxVolume;
        }
        ApplyGainToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(newMaxVolume));
    }

    /// <summary>Пересчитывает gain после изменения аудио-настроек.</summary>
    public void UpdateAudioSettings()
    {
        ApplyGainToPipeline();
        ApplyNormalizationToPipeline();
        RaiseOnUI(() => OnMaxVolumeChanged?.Invoke(_library.Settings.MaxVolumeLimit));
    }

    /// <summary>Инициализирует громкость из настроек при первом запуске.</summary>
    public void InitializeVolumeFromSettings()
    {
        if (_volumeInitialized) return;
        var settings = _library.Settings;
        _volumePercent = settings.Volume switch
        {
            > 0 and <= 1.0f => (int)(settings.Volume * 100),
            > 1 => (int)settings.Volume,
            _ => 50
        };
        _volumeInitialized = true;
        ApplyGainToPipeline();
    }

    #endregion

    #region Internal

    /// <summary>
    /// Единственный источник истины для вычисления финального gain.
    /// </summary>
    private float ComputeFinalGain()
    {
        var settings = _library.Settings;
        float gain = ComputeGain(_volumePercent, Math.Max(settings.MaxVolumeLimit, 100), settings.Audio);
        float targetGainDb = Math.Clamp(settings.TargetGainDb, -20f, 20f);
        gain *= MathF.Pow(10f, targetGainDb / 20f);
        return Math.Clamp(gain, 0f, MaxGain);
    }

    /// <summary>Применяет volume gain к backend.</summary>
    private void ApplyGainToPipeline()
    {
        float gain = ComputeFinalGain();
        _currentGain = gain;
        _player.SetVolumeGain(gain);
    }

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
        Log.Info($"[AudioEngine] Normalization gain updated from YouTube LUFS: " +
                 $"{gain:F4}x (lufs={integratedLufs:F2}) for {trackId}");
    }

    /// <summary>
    /// Пробрасывает актуальные настройки нормализации в активный pipeline.
    /// В новой модели gain всегда вычисляется из persisted integrated LUFS.
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

    private static float ComputeGain(int volumePercent, int maxVolume, AudioSettings audioSettings)
    {
        if (volumePercent <= 0) return 0f;

        if (audioSettings.VolumeBoostEnabled)
        {
            int normalCeiling = Math.Min(maxVolume, VolumeNormalRange);
            if (volumePercent <= normalCeiling)
                return ApplyVolumeCurve((float)volumePercent / normalCeiling, audioSettings.VolumeCurve);

            return 1.0f + (float)(volumePercent - normalCeiling) / VolumeNormalRange;
        }

        return ApplyVolumeCurve((float)volumePercent / maxVolume, audioSettings.VolumeCurve);
    }

    private static float ApplyVolumeCurve(float t, VolumeCurveType curve)
    {
        t = Math.Clamp(t, 0f, 1f);
        return curve switch
        {
            VolumeCurveType.Linear => t,
            VolumeCurveType.Quadratic => t * t,
            VolumeCurveType.Logarithmic => MathF.Log2(1f + t),
            VolumeCurveType.Cubic => t * t * t,
            VolumeCurveType.SpeedOfLight => (MathF.Exp(t * 2f) - 1f) / (MathF.Exp(2f) - 1f),
            _ => t * t
        };
    }

    /// <summary>Периодически сохраняет громкость, legacy gain и новую normalization metadata в БД.</summary>
    private async Task VolumeSaveLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VolumeSaveIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                _library.UpdateSettings(s =>
                {
                    s.Volume = _volumePercent;
                    s.RepeatMode = RepeatMode;
                    s.ShuffleEnabled = ShuffleEnabled;
                });

                await FlushPendingNormalizationWritesAsync(_lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Синхронно сохраняет все отложенные записи integrated loudness в базу данных.
    /// Используется в shutdown-path до полной миграции со старой gain-модели.
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

    /// <summary>
    /// Асинхронно сохраняет отложенные записи integrated loudness в БД.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
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

    #endregion
}