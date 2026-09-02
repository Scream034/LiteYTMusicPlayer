using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для метаданных медиапотоков <see cref="IStreamInfo"/>.
/// </summary>
public static class StreamInfoExtensions
{
    extension<T>(T streamInfo) where T : IStreamInfo
    {
        /// <summary>
        /// Проверяет, ограничен ли поток по скорости (throttling).
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="streamInfo"/> равен null.</exception>
        public bool IsThrottled()
        {
            ArgumentNullException.ThrowIfNull(streamInfo);

            return !string.Equals(
                UrlEx.TryGetQueryParameterValue(streamInfo.Url, "ratebypass"),
                "yes",
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    extension<T>(IEnumerable<T> streamInfos) where T : IStreamInfo
    {
        /// <summary>
        /// Возвращает поток с максимальным битрейтом или <see langword="null"/>, если коллекция пуста.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="streamInfos"/> равен null.</exception>
        public T? TryGetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.MaxBy(static s => s.Bitrate);
        }

        /// <summary>
        /// Возвращает поток с максимальным битрейтом.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="streamInfos"/> равен null.</exception>
        /// <exception cref="InvalidOperationException">Если коллекция пуста.</exception>
        public T GetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.TryGetWithHighestBitrate()
                ?? throw new InvalidOperationException("Input stream collection is empty.");
        }
    }
}