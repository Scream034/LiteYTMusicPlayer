namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Режим нормализации громкости.
/// </summary>
public enum NormalizationMode
{
    /// <summary>Spotify-стиль: тихие треки усиливаются, громкие понижаются до таргета.</summary>
    Bidirectional,

    /// <summary>YouTube-стиль: только понижение громких треков (gain ≤ 1.0).</summary>
    DownwardOnly
}

/// <summary>
/// Конфигурация нормализации громкости для одного pipeline.
/// </summary>
/// <param name="Enabled">Включена ли нормализация.</param>
/// <param name="TargetLufs">Целевой уровень громкости в LUFS.</param>
/// <param name="MaxGain">Максимальный gain-множитель.</param>
/// <param name="Mode">Режим нормализации (Upward / Downward / Bidirectional).</param>
/// <param name="PreScanDurationMs">
/// Длительность EBU R128 pre-scan в миллисекундах.
/// <list type="bullet">
///   <item>30 000 мс — для streaming и partial-cache (по умолчанию).</item>
///   <item>60 000 мс — для full-cache (файл доступен целиком, точность важнее).</item>
/// </list>
/// </param>
public readonly record struct NormalizationConfig(
    bool Enabled,
    float TargetLufs,
    float MaxGain,
    NormalizationMode Mode,
    int PreScanDurationMs = 30_000);