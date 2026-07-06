namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Источник измерения integrated loudness трека.
/// </summary>
/// <remarks>
/// Числовой порядок значений строго соответствует семантическому приоритету:
/// большее число = более точный / надёжный источник.
/// Это свойство используется напрямую в сравнении <c>existing &gt; incoming</c>
/// в <see cref="TrackInfo.SetIntegratedLufs"/> и <see cref="AudioCacheManager.TryUpdateIntegratedLufs"/>.
///
/// <para>
/// Намеренно содержит только два содержательных значения:
/// <list type="bullet">
///   <item>
///     <b>EbuMeasured</b> — результат локального EBU R128 анализа.
///     Точность зависит от объёма проанализированного аудио (prefixscan / full-scan),
///     но это параметр измерения, а не свойство источника.
///     Не нужно различать «30 секунд» и «весь файл» на уровне enum —
///     pipeline сам выбирает окно анализа в зависимости от доступных данных.
///   </item>
///   <item>
///     <b>YoutubePerceptual</b> — серверная loudness metadata от YouTube API.
///     Всегда побеждает <see cref="EbuMeasured"/>, так как измерена
///     на полном треке с учётом перцептивной модели YouTube.
///   </item>
/// </list>
/// </para>
/// </remarks>
public enum LoudnessSource
{
    /// <summary>Источник не определён. LUFS-значение недостоверно.</summary>
    Unknown = 0,

    /// <summary>
    /// EBU R128 измерение, выполненное локально.
    /// Окно анализа: 30 с для streaming/partial-cache, 60 с для full-cache.
    /// </summary>
    EbuMeasured = 1,

    /// <summary>
    /// Perceptual loudness из YouTube API metadata.
    /// Измерена на полном треке на стороне сервера.
    /// Является каноническим значением — перезаписывает <see cref="EbuMeasured"/>.
    /// </summary>
    YoutubePerceptual = 2
}