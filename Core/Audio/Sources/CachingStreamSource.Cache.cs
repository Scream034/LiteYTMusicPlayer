using System.Buffers;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- RamRangeBlock ---

    /// <summary>
    /// Иммутабельный блок данных в RAM-кэше, арендованный из <see cref="MemoryPool{T}"/>.
    /// Владеет <see cref="IMemoryOwner{T}"/> и освобождает его ровно один раз через <see cref="Dispose"/>.
    /// </summary>
    internal sealed class RamRangeBlock : IDisposable
    {
        private IMemoryOwner<byte>? _owner;
        private int _disposed;

        /// <summary>Абсолютное смещение начала блока в контенте.</summary>
        public long StartOffset { get; }

        /// <summary>Абсолютное смещение байта, следующего за последним байтом блока.</summary>
        public long EndOffsetExclusive => StartOffset + Length;

        /// <summary>Фактическая длина данных блока в байтах.</summary>
        public int Length { get; }

        /// <summary>Слайс арендованной памяти, содержащий ровно <see cref="Length"/> байт.</summary>
        public Memory<byte> Memory { get; }

        /// <summary>
        /// Создаёт блок из арендованного <see cref="IMemoryOwner{T}"/>.
        /// </summary>
        /// <param name="startOffset">Абсолютное смещение блока в контенте.</param>
        /// <param name="owner">Владелец арендованной памяти.</param>
        /// <param name="actualLength">Количество реально записанных байт.</param>
        public RamRangeBlock(long startOffset, IMemoryOwner<byte> owner, int actualLength)
        {
            StartOffset = startOffset;
            _owner = owner;
            Length = actualLength;
            Memory = owner.Memory[..actualLength];
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner?.Dispose();
            _owner = null;
        }
    }

    // --- SlidingRamCache ---

    /// <summary>
    /// Потокобезопасный RAM-кэш диапазонов байт с скользящим окном вокруг текущей позиции.
    /// <para>
    /// Блоки хранятся отсортированными по <see cref="RamRangeBlock.StartOffset"/>.
    /// Гарантирует отсутствие перекрытий: <see cref="TryAdd"/> отклоняет overlapping блоки.
    /// </para>
    /// </summary>
    internal sealed class SlidingRamCache : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<RamRangeBlock> _blocks = new(32);
        private long _totalBytes;

        /// <summary>Суммарный объём данных во всех блоках кэша (байт).</summary>
        public long TotalBytes
        {
            get { lock (_lock) return _totalBytes; }
        }

        /// <summary>
        /// Добавляет блок в кэш, сохраняя сортировку по смещению.
        /// </summary>
        /// <param name="block">Блок для добавления.</param>
        /// <returns>
        /// <c>true</c> если блок добавлен успешно;
        /// <c>false</c> если он перекрывается с уже существующим блоком.
        /// </returns>
        public bool TryAdd(RamRangeBlock block)
        {
            lock (_lock)
            {
                int insertAt = _blocks.Count;
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive <= block.StartOffset) continue;
                    if (block.EndOffsetExclusive <= current.StartOffset) { insertAt = i; break; }
                    return false;
                }
                _blocks.Insert(insertAt, block);
                _totalBytes += block.Length;
                return true;
            }
        }

        /// <summary>
        /// Пытается прочитать данные, начиная с <paramref name="position"/>, из первого подходящего блока.
        /// </summary>
        /// <param name="position">Абсолютная позиция начала чтения.</param>
        /// <param name="destination">Буфер назначения.</param>
        /// <param name="read">Количество скопированных байт.</param>
        /// <returns><c>true</c> если данные найдены и скопированы.</returns>
        public bool TryRead(long position, Memory<byte> destination, out int read)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    int offsetInBlock = (int)(position - current.StartOffset);
                    read = Math.Min(destination.Length, current.Length - offsetInBlock);
                    if (read <= 0) { read = 0; return false; }
                    current.Memory.Span.Slice(offsetInBlock, read).CopyTo(destination.Span);
                    return true;
                }
            }
            read = 0;
            return false;
        }

        /// <summary>
        /// Проверяет, содержит ли кэш полный диапазон <c>[startOffset, startOffset+length)</c>.
        /// </summary>
        /// <param name="startOffset">Начало диапазона.</param>
        /// <param name="length">Длина диапазона.</param>
        /// <returns><c>true</c> если диапазон полностью покрыт одним блоком.</returns>
        public bool ContainsRange(long startOffset, int length)
        {
            long endExclusive = startOffset + length;
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > startOffset) break;
                    if (current.EndOffsetExclusive <= startOffset) continue;
                    return current.StartOffset <= startOffset && current.EndOffsetExclusive >= endExclusive;
                }
            }
            return false;
        }

        /// <summary>
        /// Возвращает количество непрерывных байт от <paramref name="position"/>
        /// до конца первого блока, содержащего эту позицию.
        /// </summary>
        /// <param name="position">Стартовая позиция.</param>
        /// <returns>Длина доступного непрерывного диапазона; <c>0</c> если позиция не покрыта.</returns>
        public long GetContiguousBytesFrom(long position)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    return current.EndOffsetExclusive - position;
                }
            }
            return 0;
        }

        /// <summary>
        /// Удаляет блок, содержащий <paramref name="position"/>, и возвращает его.
        /// </summary>
        /// <param name="position">Позиция внутри искомого блока.</param>
        /// <param name="block">Извлечённый блок; <c>null</c> если не найден.</param>
        /// <returns><c>true</c> если блок найден и удалён.</returns>
        public bool TryRemoveContaining(long position, out RamRangeBlock? block)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    _blocks.RemoveAt(i);
                    _totalBytes -= current.Length;
                    block = current;
                    return true;
                }
            }
            block = null;
            return false;
        }

        /// <summary>
        /// Возвращает снимок всех блоков кэша в порядке возрастания смещения.
        /// </summary>
        public RamRangeBlock[] GetRangesSnapshot()
        {
            lock (_lock) { return _blocks.Count == 0 ? [] : [.. _blocks]; }
        }

        /// <summary>
        /// Вытесняет блоки, выходящие за пределы скользящего окна, и обрезает кэш до <paramref name="maxRamBytes"/>.
        /// </summary>
        /// <param name="centerOffset">Текущая позиция чтения — центр окна.</param>
        /// <param name="evictionWindowBytes">Полуширина окна в байтах.</param>
        /// <param name="maxRamBytes">Абсолютный лимит RAM-кэша.</param>
        public void Trim(long centerOffset, long evictionWindowBytes, long maxRamBytes)
        {
            lock (_lock)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive < centerOffset - evictionWindowBytes
                        || current.StartOffset > centerOffset + evictionWindowBytes)
                    {
                        _blocks.RemoveAt(i);
                        _totalBytes -= current.Length;
                        current.Dispose();
                    }
                }
                while (_totalBytes > maxRamBytes && _blocks.Count > 0)
                {
                    int removeIndex = ChooseFarthestIndex(centerOffset);
                    var removed = _blocks[removeIndex];
                    _blocks.RemoveAt(removeIndex);
                    _totalBytes -= removed.Length;
                    removed.Dispose();
                }
            }
        }

        /// <summary>
        /// Освобождает все блоки и сбрасывает счётчик байт.
        /// </summary>
        public void DisposeAll()
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++) _blocks[i].Dispose();
                _blocks.Clear();
                _totalBytes = 0;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => DisposeAll();

        // Выбирает индекс блока, наиболее удалённого от centerOffset.
        private int ChooseFarthestIndex(long centerOffset)
        {
            int chosen = 0;
            long farthestDistance = long.MinValue;
            for (int i = 0; i < _blocks.Count; i++)
            {
                long blockCenter = _blocks[i].StartOffset + (_blocks[i].Length >> 1);
                long distance = blockCenter >= centerOffset
                    ? blockCenter - centerOffset
                    : centerOffset - blockCenter;
                if (distance > farthestDistance) { farthestDistance = distance; chosen = i; }
            }
            return chosen;
        }
    }
}