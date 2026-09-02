using System.Buffers;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для потоков ввода-вывода <see cref="Stream"/>.
/// </summary>
internal static class IOStreamExtensions
{
    private const int DefaultBufferSize = 81920;

    extension(Stream source)
    {
        /// <summary>
        /// Копирует содержимое в <paramref name="destination"/> с отчётом о прогрессе.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> или <paramref name="destination"/> равны null.</exception>
        public async ValueTask CopyToAsync(
            Stream destination,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);

            using var buffer = MemoryPool<byte>.Shared.Rent(DefaultBufferSize);

            var canDetermineLength = source.CanSeek;
            var streamLength = canDetermineLength ? source.Length : -1L;
            var totalBytesRead = 0L;

            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.Memory, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                await destination.WriteAsync(buffer.Memory[..bytesRead], cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;

                if (progress is not null)
                {
                    if (streamLength > 0)
                        progress.Report((double)totalBytesRead / streamLength);
                    else
                        progress.Report(totalBytesRead);
                }
            }
        }
    }
}