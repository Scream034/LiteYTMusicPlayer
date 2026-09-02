namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для коллекций <see cref="Thumbnail"/>.
/// </summary>
public static class ThumbnailExtensions
{
    extension(IEnumerable<Thumbnail> thumbnails)
    {
        /// <summary>
        /// Возвращает миниатюру с максимальным разрешением или <see langword="null"/>, если коллекция пуста.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="thumbnails"/> равен null.</exception>
        public Thumbnail? TryGetWithHighestResolution()
        {
            ArgumentNullException.ThrowIfNull(thumbnails);
            return thumbnails.MaxBy(static t => t.Resolution.Area);
        }

        /// <summary>
        /// Возвращает миниатюру с максимальным разрешением.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="thumbnails"/> равен null.</exception>
        /// <exception cref="InvalidOperationException">Если коллекция пуста.</exception>
        public Thumbnail GetWithHighestResolution()
        {
            ArgumentNullException.ThrowIfNull(thumbnails);
            return thumbnails.TryGetWithHighestResolution()
                ?? throw new InvalidOperationException("Input thumbnail collection is empty.");
        }
    }
}