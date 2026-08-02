namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Volume State

    private int _volumePercent;
    private float _currentGain;
    private bool _volumeInitialized;
    private readonly Lock _volumeLock = new();

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

    #region Internal Computation & Loop

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

    /// <summary>Периодически сохраняет громкость и запускает сброс нормализации в БД.</summary>
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

    #endregion
}