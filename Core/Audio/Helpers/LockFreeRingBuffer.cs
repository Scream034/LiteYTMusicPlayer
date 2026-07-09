using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Экстремально быстрый Lock-Free кольцевой буфер для сценария Single-Producer / Single-Consumer.
/// </summary>
/// <remarks>
/// <para><b>Архитектурные особенности:</b></para>
/// <list type="bullet">
///   <item><b>Monotonic Counters:</b> Head и Tail являются монотонно растущими счётчиками. 
///   Переполнение <see cref="int"/> обрабатывается корректно за счёт unchecked-арифметики.</item>
///   <item><b>Zero Locks:</b> Полное отсутствие блокировок, использует только атомарные барьеры памяти (Volatile).</item>
///   <item><b>False Sharing Protection:</b> Указатели Head и Tail разнесены по разным кэш-линиям (128 байт).</item>
///   <item><b>Bitwise Modulo:</b> Размер буфера строго выравнивается до степени двойки, 
///   маскирование индекса происходит только при физическом доступе к массиву.</item>
///   <item><b>Cached Counters:</b> Каждая сторона кэширует индекс противоположной стороны, минимизируя volatile reads.</item>
///   <item><b>Bounds Elision:</b> Hot path использует <see cref="MemoryMarshal.CreateSpan{T}"/> и <c>CopyTo</c>, минуя поэлементные проверки границ.</item>
/// </list>
/// </remarks>
public sealed class LockFreeRingBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;

    private ProducerState _producer;
    private ConsumerState _consumer;

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ProducerState
    {
        /// <summary>Абсолютный счётчик записанных элементов (producer двигает вперёд).</summary>
        public int Head;
        /// <summary>Кэшированное значение Tail от consumer.</summary>
        public int CachedTail;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ConsumerState
    {
        /// <summary>Абсолютный счётчик прочитанных элементов (consumer двигает вперёд).</summary>
        public int Tail;
        /// <summary>Кэшированное значение Head от producer.</summary>
        public int CachedHead;
    }

    public LockFreeRingBuffer(int requestedCapacity)
    {
        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(requestedCapacity, 16));
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
    }

    /// <summary>Текущее количество элементов в буфере.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => unchecked(Volatile.Read(ref _producer.Head) - Volatile.Read(ref _consumer.Tail));
    }

    /// <summary>Ёмкость буфера (всегда степень двойки).</summary>
    public int Capacity => _capacity;

    /// <summary>Свободное место в буфере (1 слот зарезервирован).</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - Count;
    }

    /// <summary>
    /// Локально кэшированное свободное место. 
    /// Не делает межъядерных чтений (без Volatile.Read(Tail)), безопасно для Producer-а.
    /// </summary>
    public int ProducerCachedAvailable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - unchecked(_producer.Head - _producer.CachedTail);
    }

    /// <summary>Буфер пуст.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _producer.Head) == Volatile.Read(ref _consumer.Tail);
    }

    /// <summary>
    /// Записывает данные в буфер.
    /// Вызывается ТОЛЬКО Producer-ом (decoder loop).
    /// </summary>
    public int Write(ReadOnlySpan<T> data)
    {
        int head = _producer.Head;
        int tail = _producer.CachedTail;
        int available = _capacity - 1 - unchecked(head - tail);

        if (available < data.Length)
        {
            tail = Volatile.Read(ref _consumer.Tail);
            _producer.CachedTail = tail;
            available = _capacity - 1 - unchecked(head - tail);
        }

        int toWrite = Math.Min(data.Length, available);
        if (toWrite == 0) return 0;

        ref T bufferBase = ref MemoryMarshal.GetArrayDataReference(_buffer);
        int headIndex = head & _mask;
        int firstPart = Math.Min(toWrite, _capacity - headIndex);

        // Копирование через встроенный Memmove, без скалярных циклов и bounds checks
        data[..firstPart].CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bufferBase, headIndex), firstPart));

        int secondPart = toWrite - firstPart;
        if (secondPart > 0)
        {
            data.Slice(firstPart, secondPart).CopyTo(MemoryMarshal.CreateSpan(ref bufferBase, secondPart));
        }

        // Публикуем монотонный счётчик (без маски)
        Volatile.Write(ref _producer.Head, unchecked(head + toWrite));
        return toWrite;
    }

    /// <summary>
    /// Читает данные из буфера.
    /// Вызывается ТОЛЬКО Consumer-ом (audio callback / NAudio).
    /// </summary>
    public int Read(Span<T> output)
    {
        int tail = _consumer.Tail;
        int head = _consumer.CachedHead;
        int count = unchecked(head - tail);

        if (count < output.Length)
        {
            head = Volatile.Read(ref _producer.Head);
            _consumer.CachedHead = head;
            count = unchecked(head - tail);
        }

        int toRead = Math.Min(output.Length, count);
        if (toRead == 0) return 0;

        ref T bufferBase = ref MemoryMarshal.GetArrayDataReference(_buffer);
        ref T dst = ref MemoryMarshal.GetReference(output);
        int tailIndex = tail & _mask;
        int firstPart = Math.Min(toRead, _capacity - tailIndex);

        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref bufferBase, tailIndex), firstPart)
            .CopyTo(output[..firstPart]);

        int secondPart = toRead - firstPart;
        if (secondPart > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(ref bufferBase, secondPart)
                .CopyTo(output.Slice(firstPart, secondPart));
        }

        // Публикуем монотонный счётчик
        Volatile.Write(ref _consumer.Tail, unchecked(tail + toRead));
        return toRead;
    }

    /// <summary>
    /// Очищает буфер. Вызывать ТОЛЬКО при остановленном воспроизведении!
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _producer.Head, 0);
        Volatile.Write(ref _consumer.Tail, 0);
        _consumer.CachedHead = 0;
        _producer.CachedTail = 0;
    }
}