namespace LMP.Core.Audio;

/// <summary>
/// Типизированная подсказка выбора аудиопотока.
/// Объединяет желаемый формат и битрейт без строкового представления состояния.
/// </summary>
/// <param name="Format">Желаемый формат аудиоконтейнера.</param>
/// <param name="BitrateKbps">Желаемый битрейт в kbps.</param>
public readonly record struct StreamSelectionHint(AudioFormat? Format, int BitrateKbps)
{
    /// <summary>
    /// Возвращает значение, указывающее, задан ли явно формат контейнера.
    /// </summary>
    public bool HasFormat => Format is { } value && value != AudioFormat.Unknown;

    /// <summary>
    /// Возвращает значение, указывающее, задан ли явно битрейт потока.
    /// </summary>
    public bool HasBitrate => BitrateKbps > 0;

    /// <summary>
    /// Создаёт типизированную подсказку выбора потока из текущего состояния трека и настроек приложения.
    /// </summary>
    /// <param name="track">Рантайм-модель музыкального трека.</param>
    /// <param name="rememberTrackFormat">Флаг из настроек приложения, указывающий, нужно ли помнить предпочтения формата пользователя.</param>
    /// <returns>Экземпляр структуры <see cref="StreamSelectionHint"/>.</returns>
    /// <exception cref="ArgumentNullException">Генерируется, если переданный объект трека равен null.</exception>
    public static StreamSelectionHint FromTrack(TrackInfo track, bool rememberTrackFormat)
    {
        ArgumentNullException.ThrowIfNull(track);

        AudioFormat? format = track.TransientFormat;
        int bitrate = track.TransientBitrate;

        if (rememberTrackFormat)
        {
            format ??= track.PreferredFormat;

            if (bitrate <= 0)
                bitrate = track.PreferredBitrate;
        }

        if (format == AudioFormat.Unknown)
            format = null;

        return new StreamSelectionHint(format, bitrate);
    }
}