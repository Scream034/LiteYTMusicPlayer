namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для коллекций и последовательностей.
/// </summary>
internal static class CollectionExtensions
{
    extension<T>(IEnumerable<T?> source) where T : class
    {
        /// <summary>
        /// Фильтрует последовательность, отсекая элементы со значением <see langword="null"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public IEnumerable<T> WhereNotNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
            {
                if (i is not null)
                    yield return i;
            }
        }
    }

    extension<T>(IEnumerable<T?> source) where T : struct
    {
        /// <summary>
        /// Фильтрует последовательность <see cref="Nullable{T}"/>, возвращая распакованные значения.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public IEnumerable<T> WhereNotNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
            {
                if (i is not null)
                    yield return i.Value;
            }
        }
    }

    extension<T>(IEnumerable<T> source) where T : struct
    {
        /// <summary>
        /// Возвращает элемент по индексу <paramref name="index"/> или <see langword="null"/> при выходе за границы.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public T? ElementAtOrNull(int index)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (index < 0) return null;

            if (source is IReadOnlyList<T> readOnlyList)
                return index < readOnlyList.Count ? readOnlyList[index] : null;

            if (source is IList<T> list)
                return index < list.Count ? list[index] : null;

            if (source is ICollection<T> collection && index >= collection.Count)
                return null;

            if (source is IReadOnlyCollection<T> readOnlyCollection && index >= readOnlyCollection.Count)
                return null;

            using var enumerator = source.GetEnumerator();
            var currentIndex = 0;
            while (enumerator.MoveNext())
            {
                if (currentIndex == index)
                    return enumerator.Current;
                currentIndex++;
            }

            return null;
        }

        /// <summary>
        /// Возвращает первый элемент последовательности или <see langword="null"/>, если она пуста.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен null.</exception>
        public T? FirstOrNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
                return i;

            return null;
        }
    }
}