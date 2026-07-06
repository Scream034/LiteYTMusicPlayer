using System.Runtime.CompilerServices;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Helpers;

/// <summary>
/// Единая логика переноса LUFS-метаданных из кэша в runtime-модель трека.
/// </summary>
public static class TrackNormalizationHydrator
{
    /// <summary>
    /// Переносит integrated loudness из записи кэша в модель трека.
    /// </summary>
    /// <remarks>
    /// Приоритет источника делегируется <see cref="TrackInfo.SetIntegratedLufs"/>,
    /// которая использует числовой порядок <see cref="LoudnessSource"/>.
    /// Это означает:
    /// <list type="bullet">
    ///   <item>Если кэш хранит <see cref="LoudnessSource.YoutubePerceptual"/>,
    ///         а трек — <see cref="LoudnessSource.EbuMeasured"/> или <see cref="LoudnessSource.Unknown"/>,
    ///         кэшевое значение будет применено (upgrade).</item>
    ///   <item>Если трек уже имеет <see cref="LoudnessSource.YoutubePerceptual"/>,
    ///         <see cref="LoudnessSource.EbuMeasured"/> из кэша его не перезапишет (guard).</item>
    /// </list>
    ///
    /// Намеренно убран guard <c>!track.HasIntegratedLufs</c>:
    /// он блокировал upgrade из более качественного источника в кэше.
    /// </remarks>
    /// <param name="track">Runtime-модель трека.</param>
    /// <param name="entry">Cache entry с LUFS-метаданными.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HydrateNormalization(TrackInfo track, AudioCacheEntry entry)
    {
        if (entry.IntegratedLufs is float lufs && float.IsFinite(lufs))
        {
            track.SetIntegratedLufs(lufs, (LoudnessSource)entry.IntegratedLufsSource);
        }
    }
}