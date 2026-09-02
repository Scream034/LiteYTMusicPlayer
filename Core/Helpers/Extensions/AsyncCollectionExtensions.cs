using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для асинхронных потоков <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
internal static class AsyncCollectionExtensions
{
    extension<T>(IAsyncEnumerable<T> source)
    {
        /// <summary>
        /// Возвращает не более <paramref name="count"/> первых элементов потока.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public async IAsyncEnumerable<T> TakeAsync(
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            var currentCount = 0;

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (currentCount >= count)
                    yield break;

                yield return i;
                currentCount++;
            }
        }

        /// <summary>
        /// Проецирует элементы потока в коллекции и объединяет их в плоскую последовательность.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> или <paramref name="transform"/> равны null.</exception>
        public async IAsyncEnumerable<T1> SelectManyAsync<T1>(
            Func<T, IEnumerable<T1>> transform,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(transform);

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var j in transform(i))
                    yield return j;
            }
        }
    }

    extension(IAsyncEnumerable<object> source)
    {
        /// <summary>
        /// Фильтрует элементы потока по заданному типу <typeparamref name="T"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public async IAsyncEnumerable<T> OfTypeAsync<T>(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (i is T match)
                    yield return match;
            }
        }
    }
}