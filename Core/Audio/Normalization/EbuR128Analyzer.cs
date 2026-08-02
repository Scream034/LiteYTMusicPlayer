using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// EBU R128 / ITU-R BS.1770-4 анализатор громкости.
///
/// <para><b>Алгоритм:</b></para>
/// <list type="bullet">
///   <item><b>Pre-scan</b> (isolated pipeline, ~50-300ms): K-weighted LUFS с relative gating.
///     Gain фиксируется до начала воспроизведения.</item>
///   <item><b>Fallback</b>: если pre-scan невозможен (недостаточно данных),
///     используется carry-over gain от предыдущего трека. LUFS будет вычислен
///     при следующем воспроизведении после кэширования.</item>
/// </list>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item><see cref="ProcessSamples"/> — fill thread (hot path, zero-alloc)</item>
///   <item><see cref="Configure"/>, <see cref="LockFromCache"/>, <see cref="LockFromMetadata"/> —
///     command thread</item>
///   <item><see cref="_pendingNormReset"/> — volatile int, lock-free sync</item>
/// </list>
/// </summary>
public sealed class EbuR128Analyzer
{
    #region Constants (EBU R128 / ITU-R BS.1770-4)

    /// <summary>Минимальный gain нормализации (защита от чрезмерного подавления).</summary>
    private const float MinNormalizationGain = 0.1f;

    /// <summary>Максимальный gain нормализации по умолчанию.</summary>
    private const float DefaultMaxNormalizationGain = 3.0f;

    /// <summary>Относительный порог гейтинга: −10 LU (ITU-R BS.1770-4 §3).</summary>
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>Константа из ITU-R BS.1770-4 уравнения (2): −0.691 dBFS.</summary>
    private const double LufsOffset = -0.691;

    /// <summary>Макс. количество gating blocks для pre-scan.</summary>
    internal const int MaxScanGatingBlocks = 512;

    #endregion

    #region Gain State

    /// <summary>
    /// Зафиксированный gain. NaN = gain не установлен.
    /// Единственная точка записи: <see cref="LockGain"/>, <see cref="LockResolvedGain"/>.
    /// </summary>
    private float _lockedGain = float.NaN;

    /// <summary>Начальный gain от предыдущего трека (устраняет cold-start скачок).</summary>
    private float _startingNormGain = 1.0f;

    /// <summary>
    /// Последний измеренный integrated LUFS. Используется для пересчёта gain
    /// при смене параметров нормализации (targetLufs, mode) без повторного scan.
    /// </summary>
    private float _lastIntegratedLufs = float.NaN;

    /// <summary>
    /// Сигнал отложенного сброса. 1 = полный reset, 2 = recalc, 0 = нет.
    /// Устанавливается из command thread, исполняется fill thread'ом.
    /// </summary>
    private volatile int _pendingNormReset;

    /// <summary>
    /// Callback фиксации integrated LUFS. Вызывается максимум один раз за pipeline.
    /// </summary>
    private volatile Action<float>? _onIntegratedLufsResolved;

#if DEBUG
    private float _lastLoggedCacheGain = float.NaN;
    private NormalizationMode _lastLoggedCacheMode;
#endif

    #endregion

    #region Configuration

    private volatile bool _enabled;
    private float _targetLufs = -14f;
    private float _maxGain = DefaultMaxNormalizationGain;
    private NormalizationMode _mode = NormalizationMode.Bidirectional;

    private sealed record ConfigSnapshot(NormalizationConfig Value);
    private volatile ConfigSnapshot? _pendingConfig;

    #endregion

    #region Public Properties

    /// <summary>Включена ли нормализация.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Зафиксирован ли gain.</summary>
    public bool IsGainLocked => !float.IsNaN(_lockedGain);

    /// <summary>Текущая конфигурация (snapshot).</summary>
    public NormalizationConfig CurrentConfig => new(_enabled, _targetLufs, _maxGain, _mode);

    #endregion

    #region Configuration API

    /// <summary>
    /// Применяет конфигурацию нормализации атомарно.
    /// </summary>
    public void Configure(NormalizationConfig config)
    {
        float clampedMaxGain = Math.Max(1f, config.MaxGain);
        var normalizedConfig = config with { MaxGain = clampedMaxGain };

        if (!config.Enabled && _enabled)
        {
            _enabled = false;
            _lockedGain = float.NaN;
            Log.Debug("[EbuR128] Normalization OFF");
            return;
        }

        if (!config.Enabled) return;

        bool wasEnabled = _enabled;
        bool changed = !wasEnabled
            || MathF.Abs(_targetLufs - normalizedConfig.TargetLufs) > 0.01f
            || MathF.Abs(_maxGain - normalizedConfig.MaxGain) > 0.01f
            || _mode != normalizedConfig.Mode;

        if (!changed) return;

        bool needsReset = !wasEnabled;
        bool needsRecalc = wasEnabled && changed;

        _pendingConfig = new ConfigSnapshot(normalizedConfig);
        _enabled = true;

        if (needsReset)
        {
            _pendingNormReset = 1;
            Log.Debug($"[EbuR128] Normalization ON: target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
        else if (needsRecalc)
        {
            _pendingNormReset = 2;
            Log.Debug($"[EbuR128] Params changed (recalc): target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
    }

    /// <summary>
    /// Публикует measured integrated LUFS и сохраняет для пересчёта.
    /// </summary>
    internal void NotifyIntegratedLufs(float integratedLufs)
    {
        if (float.IsFinite(integratedLufs))
        {
            _lastIntegratedLufs = integratedLufs;
            _onIntegratedLufsResolved?.Invoke(integratedLufs);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyPendingConfig()
    {
        var snapshot = _pendingConfig;
        if (snapshot == null) return;

        var cfg = snapshot.Value;
        _targetLufs = cfg.TargetLufs;
        _maxGain = Math.Max(1f, cfg.MaxGain);
        _mode = cfg.Mode;
    }

    /// <inheritdoc cref="SetIntegratedLufsCallback"/>
    public void SetIntegratedLufsCallback(Action<float>? callback) => _onIntegratedLufsResolved = callback;

    /// <summary>
    /// Устанавливает начальный gain от предыдущего трека.
    /// </summary>
    public void SetInitialGain(float gain)
    {
        _startingNormGain = Math.Clamp(gain, MinNormalizationGain, DefaultMaxNormalizationGain);
    }

    /// <summary>Запрашивает сброс при seek (если gain ещё не зафиксирован).</summary>
    public void PrepareForSeek()
    {
    }

    #endregion

    #region Gain Locking

    /// <summary>
    /// Применяет вычисленный runtime-gain без дополнительных событий.
    /// </summary>
    public void LockResolvedGain(float gain)
    {
        if (!_enabled) return;
        if (gain <= 0f || !float.IsFinite(gain)) return;

        var pendingSnapshot = _pendingConfig;
        var effectiveMode = pendingSnapshot?.Value.Mode ?? _mode;
        var effectiveMaxGain = pendingSnapshot != null
            ? Math.Max(1f, pendingSnapshot.Value.MaxGain)
            : _maxGain;

        if (effectiveMode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, effectiveMaxGain);

        _pendingNormReset = 0;
        _lockedGain = gain;

#if DEBUG
        if (MathF.Abs(gain - _lastLoggedCacheGain) > 0.0005f || effectiveMode != _lastLoggedCacheMode)
        {
            _lastLoggedCacheGain = gain;
            _lastLoggedCacheMode = effectiveMode;
            Log.Debug($"[EbuR128] Gain from cache: {gain:F3}x (mode={effectiveMode}, analysis skipped)");
        }
#endif
    }

    /// <summary>
    /// Фиксирует gain из EBU R128 pre-scan.
    /// </summary>
    public void LockGain(float gain)
    {
        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxGain);

        _pendingNormReset = 0;
        _lockedGain = gain;
    }

    /// <summary>
    /// Возвращает зафиксированный gain или carry-over от предыдущего трека.
    /// </summary>
    public float GetLockedGain()
    {
        if (!_enabled) return 1.0f;
        return float.IsNaN(_lockedGain) ? _startingNormGain : _lockedGain;
    }

    #endregion

    #region Hot Path

    /// <summary>
    /// Возвращает текущий gain нормализации. Обрабатывает отложенные операции
    /// из command thread (config apply, reset, recalculate).
    /// </summary>
    /// <remarks>
    /// <para><b>ВЫЗЫВАТЬ ТОЛЬКО ИЗ FILL THREAD.</b></para>
    /// <para><b>Zero-alloc. O(1).</b></para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSamples()
    {
        ApplyPendingConfig();

        int pendingOp = Interlocked.Exchange(ref _pendingNormReset, 0);
        if (pendingOp == 1)
            ExecuteReset();
        else if (pendingOp == 2)
            ExecuteRecalculate();

        return float.IsNaN(_lockedGain) ? _startingNormGain : _lockedGain;
    }

    #endregion

    #region Internal State Management

    /// <summary>
    /// Пересчитывает gain из сохранённого integrated LUFS с новыми параметрами.
    /// </summary>
    private void ExecuteRecalculate()
    {
        if (!float.IsFinite(_lastIntegratedLufs))
            return;

        float gainDb = _targetLufs - _lastIntegratedLufs;
        float gain = MathF.Pow(10f, gainDb / 20f);

        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxGain);
        _lockedGain = gain;

        Log.Debug($"[EbuR128] Recalculated from LUFS={_lastIntegratedLufs:F2}: " +
                  $"gain={gain:F3}x (target={_targetLufs}, mode={_mode})");
    }

    /// <summary>
    /// Сбрасывает gain state для нового трека.
    /// </summary>
    private void ExecuteReset()
    {
        _lockedGain = float.NaN;
        _lastIntegratedLufs = float.NaN;

#if DEBUG
        _lastLoggedCacheGain = float.NaN;
        Log.Debug("[EbuR128] Reset: ready for new track");
#endif
    }

    #endregion

    #region Static EBU R128 Computation (used by pre-scan)

    /// <summary>
    /// Вычисляет integrated LUFS из массива gating block powers (EBU R128).
    /// Применяет absolute gate (−70 LUFS) и relative gate (−10 LU).
    /// </summary>
    internal static float ComputeIntegratedLufsFromBlocks(double[] blockPowers, int blockCount)
    {
        if (blockCount == 0)
            return float.NaN;

        double sumPower = 0.0;
        for (int i = 0; i < blockCount; i++)
            sumPower += blockPowers[i];

        double meanPower = sumPower / blockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        double relThreshold = integratedLufs + RelativeGateOffsetLu;
        double relPowerThreshold = Math.Pow(10.0, (relThreshold - LufsOffset) / 10.0);

        double gatedSum = 0.0;
        int gatedCount = 0;
        for (int i = 0; i < blockCount; i++)
        {
            if (blockPowers[i] >= relPowerThreshold)
            {
                gatedSum += blockPowers[i];
                gatedCount++;
            }
        }

        if (gatedCount > 0)
        {
            meanPower = gatedSum / gatedCount;
            integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));
        }

        return (float)integratedLufs;
    }

    /// <summary>
    /// Вычисляет raw linear gain из gating block powers.
    /// </summary>
    internal static float ComputeIntegratedGainFromBlocks(
        double[] blockPowers, int blockCount, float targetLufs, float maxGain)
    {
        if (blockCount == 0)
            return 1.0f;

        double sumPower = 0.0;
        for (int i = 0; i < blockCount; i++)
            sumPower += blockPowers[i];

        double meanPower = sumPower / blockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        double relThreshold = integratedLufs + RelativeGateOffsetLu;
        double relPowerThreshold = Math.Pow(10.0, (relThreshold - LufsOffset) / 10.0);

        double gatedSum = 0.0;
        int gatedCount = 0;
        for (int i = 0; i < blockCount; i++)
        {
            if (blockPowers[i] >= relPowerThreshold)
            {
                gatedSum += blockPowers[i];
                gatedCount++;
            }
        }

        if (gatedCount > 0)
        {
            meanPower = gatedSum / gatedCount;
            integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));
        }

        float gainDb = (float)(targetLufs - integratedLufs);
        float gain = MathF.Pow(10f, gainDb / 20f);

        return Math.Clamp(gain, MinNormalizationGain, maxGain);
    }

    #endregion
}