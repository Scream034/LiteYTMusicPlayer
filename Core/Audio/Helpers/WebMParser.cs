using System.Buffers;
using System.Buffers.Binary;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Стандартно-совместимый парсер контейнеров WebM/Matroska (EBML) для извлечения пакетов OPUS.
/// Оптимизирован для потокового чтения без аллокаций, с защитой от десинхронизации (desync) и пропуска нулевых байтов (EBML Padding).
/// </summary>
/// <remarks>
/// <para>Соответствует спецификациям RFC 8794 (EBML) и RFC 9559 (Matroska).</para>
/// </remarks>
public sealed class WebMParser : IDisposable
{
    //
    // EBML & Matroska Element IDs
    //
    private const uint EBML_ID = 0x1A45DFA3;
    private const uint SEGMENT_ID = 0x18538067;
    private const uint CLUSTER_ID = 0x1F43B675;
    private const uint SIMPLE_BLOCK_ID = 0xA3;
    private const uint BLOCK_GROUP_ID = 0xA0;
    private const uint BLOCK_ID = 0xA1;
    private const uint TIMECODE_ID = 0xE7;
    private const uint INFO_ID = 0x1549A966;
    private const uint DURATION_ID = 0x4489;
    private const uint TIMECODE_SCALE_ID = 0x2AD7B1;
    private const uint TRACKS_ID = 0x1654AE6B;
    private const uint TRACK_ENTRY_ID = 0xAE;
    private const uint TRACK_NUMBER_ID = 0xD7;
    private const uint TRACK_TYPE_ID = 0x83;
    private const uint CODEC_PRIVATE_ID = 0x63A2;
    private const uint AUDIO_ID = 0xE1;
    private const uint SAMPLING_FREQUENCY_ID = 0xB5;
    private const uint CHANNELS_ID = 0x9F;
    private const uint CUES_ID = 0x1C53BB6B;
    private const uint CUE_POINT_ID = 0xBB;
    private const uint CUE_TIME_ID = 0xB3;
    private const uint CUE_TRACK_POSITIONS_ID = 0xB7;
    private const uint CUE_CLUSTER_POSITION_ID = 0xF1;

    //
    // Константы Lacing и EBML
    //
    private const int LACING_NONE = 0;
    private const int LACING_XIPH = 1;
    private const int LACING_FIXED = 2;
    private const int LACING_EBML = 3;

    /// <summary>Маркер EBML VINT, обозначающий неизвестный размер.</summary>
    public const long UNKNOWN_SIZE = -1;

    private const long DEFAULT_TIMECODE_SCALE = 1_000_000; // 1ms в наносекундах
    private const int ReadBufferSize = 64 * 1024;
    private const int MaxResyncScanBytes = 256 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _readBuffer;
    private int _bufPos;
    private int _bufLen;

    private readonly List<CuePoint> _cuePoints = [];

    /// <summary>
    /// Очередь для распакованных фреймов из Laced-блоков.
    /// Гарантирует извлечение всех аудиокадров из одного SimpleBlock без потери данных.
    /// </summary>
    private readonly Queue<AudioBlock> _lacedFrames = new();

    private long _segmentOffset;
    private long _currentClusterTimecode;
    private long _timecodeScale = DEFAULT_TIMECODE_SCALE;
    private long _duration;
    private int _audioTrackNumber = 1;
    private bool _headersParsed;
    private bool _requiresResync;

    /// <summary>Байтовая позиция начала содержимого последнего распознанного Cluster.</summary>
    public long LastClusterBytePosition { get; private set; }

    /// <summary>Флаг успешного восстановления синхронизации (используется для диагностики усечённых файлов).</summary>
    public bool ResyncOccurred { get; private set; }

    /// <summary>Длительность медиа в миллисекундах.</summary>
    public long DurationMs => _duration * _timecodeScale / 1_000_000;

    /// <summary>Коллекция точек быстрого поиска.</summary>
    public IReadOnlyList<CuePoint> CuePoints => _cuePoints;

    /// <summary>Конфигурация декодера (CodecPrivate).</summary>
    public byte[]? CodecPrivate { get; private set; }

    /// <summary>Частота дискретизации аудио.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Количество каналов.</summary>
    public int Channels { get; private set; } = 2;

    /// <summary>Представляет точку быстрого перехода (Cue).</summary>
    public readonly record struct CuePoint(long TimeMs, long ClusterOffset);

    /// <summary>Представляет извлечённый аудио-блок.</summary>
    public readonly record struct AudioBlock(
        IMemoryOwner<byte> Owner,
        int Length,
        long TimestampMs,
        bool IsKeyFrame
    );

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="WebMParser"/>.
    /// </summary>
    /// <param name="stream">Входной аудио-поток.</param>
    /// <param name="bufferSize">Размер промежуточного буфера чтения.</param>
    public WebMParser(Stream stream, int bufferSize = ReadBufferSize)
    {
        _stream = stream;
        _readBuffer = new byte[bufferSize];
    }

    #region Буферизованное Чтение (Zero-Alloc)

    private async ValueTask<bool> EnsureBufferedAsync(int needed, CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available >= needed) return true;

        if (available > 0 && _bufPos > 0)
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);

        _bufPos = 0;
        _bufLen = available;

        while (_bufLen < needed)
        {
            int read = await _stream.ReadAsync(_readBuffer.AsMemory(_bufLen, _readBuffer.Length - _bufLen), ct).ConfigureAwait(false);
            if (read == 0) return _bufLen >= needed;
            _bufLen += read;
        }
        return true;
    }

    private async ValueTask FillBufferAsync(CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available > 0 && _bufPos > 0)
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);

        _bufPos = 0;
        _bufLen = available;

        int toRead = _readBuffer.Length - _bufLen;
        if (toRead > 0)
        {
            int read = await _stream.ReadAsync(_readBuffer.AsMemory(_bufLen, toRead), ct).ConfigureAwait(false);
            _bufLen += read;
        }
    }

    private int BufferedReadByte()
    {
        if (_bufPos >= _bufLen) return -1;
        return _readBuffer[_bufPos++];
    }

    private ReadOnlySpan<byte> BufferedReadSpan(int count)
    {
        var span = _readBuffer.AsSpan(_bufPos, count);
        _bufPos += count;
        return span;
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        int remaining = buffer.Length;

        int buffered = Math.Min(remaining, _bufLen - _bufPos);
        if (buffered > 0)
        {
            _readBuffer.AsSpan(_bufPos, buffered).CopyTo(buffer.Span[..buffered]);
            _bufPos += buffered;
            offset += buffered;
            remaining -= buffered;
        }

        while (remaining > 0)
        {
            int read = await _stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Unexpected EOF while reading exact payload.");
            offset += read;
            remaining -= read;
        }
    }

    private async ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int buffered = (int)Math.Min(count, _bufLen - _bufPos);
        _bufPos += buffered;
        count -= buffered;

        if (count <= 0) return;

        if (_stream.CanSeek)
        {
            _stream.Position += count;
            _bufPos = 0;
            _bufLen = 0;
            return;
        }

        while (count > 0)
        {
            await FillBufferAsync(ct).ConfigureAwait(false);
            int skip = (int)Math.Min(count, _bufLen - _bufPos);
            if (skip == 0) throw new EndOfStreamException($"Unexpected EOF skipping {count} bytes.");
            _bufPos += skip;
            count -= skip;
        }
    }

    private long StreamPositionWithBuffer() => _stream.Position - (_bufLen - _bufPos);

    #endregion

    #region Парсинг Заголовков

    /// <summary>
    /// Парсит заголовки контейнера до начала первого Cluster.
    /// </summary>
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        if (_headersParsed) return true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long elementStart = StreamPositionWithBuffer();
                var (id, size) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);

                if (id == 0) return false;

                switch (id)
                {
                    case INFO_ID:
                        await ParseInfoAsync(size, ct).ConfigureAwait(false);
                        break;

                    case TRACKS_ID:
                        await ParseTracksAsync(size, ct).ConfigureAwait(false);
                        break;

                    case CUES_ID:
                        await ParseCuesAsync(size, ct).ConfigureAwait(false);
                        break;

                    case CLUSTER_ID:
                        // Нашли аудиоданные. Откатываемся, чтобы ReadNextBlockAsync сам вошел в кластер.
                        if (_stream.CanSeek)
                        {
                            _stream.Position = elementStart;
                            _bufPos = 0;
                            _bufLen = 0;
                        }
                        _headersParsed = true;
                        return true;

                    case EBML_ID:
                    case SEGMENT_ID:
                        if (id == SEGMENT_ID) _segmentOffset = StreamPositionWithBuffer();
                        continue;

                    default:
                        if (size != UNKNOWN_SIZE && size > 0)
                            await SkipBytesAsync(size, ct).ConfigureAwait(false);
                        break;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask ParseInfoAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case TIMECODE_SCALE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    _timecodeScale = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case DURATION_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    _duration = (long)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask ParseTracksAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            if (id == TRACK_ENTRY_ID)
                await ParseTrackEntryAsync(elementSize, ct).ConfigureAwait(false);
            else if (elementSize != UNKNOWN_SIZE)
                await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ParseTrackEntryAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;
        int trackNumber = 0;
        int trackType = 0;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case TRACK_NUMBER_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    trackNumber = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case TRACK_TYPE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    trackType = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CODEC_PRIVATE_ID:
                    CodecPrivate = new byte[elementSize];
                    await ReadExactAsync(CodecPrivate, ct).ConfigureAwait(false);
                    break;

                case AUDIO_ID:
                    await ParseAudioSettingsAsync(elementSize, ct).ConfigureAwait(false);
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }

        if (trackType == 2 && trackNumber > 0)
            _audioTrackNumber = trackNumber;
    }

    private async ValueTask ParseAudioSettingsAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case SAMPLING_FREQUENCY_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    SampleRate = (int)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                case CHANNELS_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    Channels = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask ParseCuesAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            if (id == CUE_POINT_ID)
            {
                var cuePoint = await ParseCuePointAsync(elementSize, ct).ConfigureAwait(false);
                if (cuePoint.HasValue) _cuePoints.Add(cuePoint.Value);
            }
            else if (elementSize != UNKNOWN_SIZE)
            {
                await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<CuePoint?> ParseCuePointAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;
        long time = 0;
        long clusterPosition = 0;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case CUE_TIME_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return null;
                    time = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CUE_TRACK_POSITIONS_ID:
                    long posEnd = StreamPositionWithBuffer() + elementSize;
                    while (StreamPositionWithBuffer() < posEnd)
                    {
                        var (posId, posSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
                        if (posId == CUE_CLUSTER_POSITION_ID)
                        {
                            if (!await EnsureBufferedAsync((int)posSize, ct)) return null;
                            clusterPosition = ReadUnsignedInt(BufferedReadSpan((int)posSize));
                        }
                        else if (posSize != UNKNOWN_SIZE)
                            await SkipBytesAsync(posSize, ct).ConfigureAwait(false);
                    }
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
        return new CuePoint(time * _timecodeScale / 1_000_000, clusterPosition);
    }

    #endregion

    #region Чтение Блоков (Streaming Flat-Loop)

     /// <summary>
    /// Сбрасывает состояние парсера и подготавливает его к поиску нового синхромаркера.
    /// </summary>
    public void RequireResync()
    {
        _requiresResync = true;
        _bufPos = 0;
        _bufLen = 0;
        
        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
    }

    /// <summary>
    /// Читает следующий аудиоблок. Снабжен защитой от десинхронизации и поддержкой ручного сброса.
    /// </summary>
    public async ValueTask<AudioBlock?> ReadNextBlockAsync(CancellationToken ct = default)
    {
        if (_requiresResync)
        {
            _requiresResync = false;
            if (!await TryResyncToNextClusterAsync(ct).ConfigureAwait(false))
                return null;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lacedFrames.TryDequeue(out var frame))
                    return frame;

                var (id, size) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
                if (id == 0) return null;

                switch (id)
                {
                    case CLUSTER_ID:
                        LastClusterBytePosition = StreamPositionWithBuffer();
                        continue;

                    case TIMECODE_ID:
                        int timecodeSize = CheckedSizeToInt32(size, "Cluster Timecode");
                        await EnsureBufferedOrThrowAsync(timecodeSize, "cluster timecode", ct).ConfigureAwait(false);
                        _currentClusterTimecode = ReadUnsignedInt(BufferedReadSpan(timecodeSize));
                        continue;

                    case SIMPLE_BLOCK_ID:
                    case BLOCK_ID:
                        await ParseBlockPayloadAsync(CheckedSizeToInt32(size, "Block"), id == SIMPLE_BLOCK_ID, ct).ConfigureAwait(false);
                        if (_lacedFrames.TryDequeue(out var lacedFrame))
                            return lacedFrame;
                        continue;

                    case BLOCK_GROUP_ID:
                        continue;

                    default:
                        if (size != UNKNOWN_SIZE && size > 0)
                            await SkipBytesAsync(size, ct).ConfigureAwait(false);
                        continue;
                }
            }
            catch (EndOfStreamException ex) when (!ct.IsCancellationRequested && _stream.CanSeek)
            {
                throw new ParserCorruptionException(StreamPositionWithBuffer(), "Unexpected EOF inside EBML stream", ex);
            }
            catch (InvalidDataException ex) when (!ct.IsCancellationRequested && _stream.CanSeek)
            {
                throw new ParserCorruptionException(StreamPositionWithBuffer(), "EBML structural corruption detected", ex);
            }
        }
        return null;
    }

    private async ValueTask ParseBlockPayloadAsync(int payloadSize, bool isSimpleBlock, CancellationToken ct)
    {
        long blockEndPos = StreamPositionWithBuffer() + payloadSize;

        await EnsureBufferedOrThrowAsync(1, "track number", ct).ConfigureAwait(false);
        int trackByte = BufferedReadByte();
        int trackLen = GetVIntLength((byte)trackByte);
        
        long trackNum;
        if (trackLen == 1) trackNum = trackByte & 0x7F;
        else
        {
            if (trackLen == 0 || trackLen > 8)
                throw new ParserCorruptionException(StreamPositionWithBuffer(), $"Invalid EBML Track Number VINT length: {trackLen}");

            await EnsureBufferedOrThrowAsync(trackLen - 1, "track number payload", ct).ConfigureAwait(false);
            Span<byte> trackBytes = stackalloc byte[trackLen];
            trackBytes[0] = (byte)trackByte;
            BufferedReadSpan(trackLen - 1).CopyTo(trackBytes[1..]);
            trackNum = ReadVInt(trackBytes);
        }

        await EnsureBufferedOrThrowAsync(3, "timecode and flags", ct).ConfigureAwait(false);
        short timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(BufferedReadSpan(2));
        int flags = BufferedReadByte();

        if (StreamPositionWithBuffer() > blockEndPos)
        {
            throw new ParserCorruptionException(StreamPositionWithBuffer(), "Block header parsing exceeded allocated element payload size.");
        }

        long remainingPayload = blockEndPos - StreamPositionWithBuffer();

        if (trackNum != _audioTrackNumber)
        {
            if (remainingPayload > 0) await SkipBytesAsync(remainingPayload, ct).ConfigureAwait(false);
            return;
        }

        bool isKeyFrame = isSimpleBlock && (flags & 0x80) != 0;
        int lacingType = (flags >> 1) & 0x03;

        if (remainingPayload <= 0) return;

        long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;

        if (lacingType == LACING_NONE)
        {
            var mem = MemoryPool<byte>.Shared.Rent((int)remainingPayload);
            await ReadExactAsync(mem.Memory[..(int)remainingPayload], ct).ConfigureAwait(false);
            _lacedFrames.Enqueue(new AudioBlock(mem, (int)remainingPayload, timestampMs, isKeyFrame));
            return;
        }

        await ParseLacingAsync((int)remainingPayload, lacingType, timestampMs, isKeyFrame, ct).ConfigureAwait(false);
    }

    private async ValueTask ParseLacingAsync(int payloadSize, int lacing, long timestampMs, bool isKeyFrame, CancellationToken ct)
    {
        long payloadEndPos = StreamPositionWithBuffer() + payloadSize;

        await EnsureBufferedOrThrowAsync(1, "lacing frame count", ct).ConfigureAwait(false);
        int frameCountByte = BufferedReadByte();
        if (frameCountByte < 0) return;

        int framesCount = frameCountByte + 1;
        int[] frameSizes = new int[framesCount];

        if (lacing == LACING_XIPH)
        {
            int totalSizes = 0;
            if (framesCount > 1)
            {
                for (int i = 0; i < framesCount - 1; i++)
                {
                    int size = 0;
                    while (true)
                    {
                        await EnsureBufferedOrThrowAsync(1, "xiph size", ct).ConfigureAwait(false);
                        int b = BufferedReadByte();
                        if (b < 0) throw new EndOfStreamException("EOF in xiph lacing size");
                        size += b;
                        if (b < 255) break;
                    }
                    frameSizes[i] = size;
                    totalSizes += size;
                }
            }
            frameSizes[framesCount - 1] = (int)(payloadEndPos - StreamPositionWithBuffer()) - totalSizes;
        }
        else if (lacing == LACING_EBML)
        {
            int totalSizes = 0;
            if (framesCount > 1)
            {
                long firstSize = await ReadVIntFromStreamAsync(ct).ConfigureAwait(false);
                frameSizes[0] = (int)firstSize;
                totalSizes += frameSizes[0];

                for (int i = 1; i < framesCount - 1; i++)
                {
                    long delta = await ReadSignedVIntFromStreamAsync(ct).ConfigureAwait(false);
                    frameSizes[i] = frameSizes[i - 1] + (int)delta;
                    totalSizes += frameSizes[i];
                }
            }
            frameSizes[framesCount - 1] = (int)(payloadEndPos - StreamPositionWithBuffer()) - totalSizes;
        }
        else if (lacing == LACING_FIXED)
        {
            int size = (int)(payloadEndPos - StreamPositionWithBuffer()) / framesCount;
            for (int i = 0; i < framesCount; i++) frameSizes[i] = size;
        }

        // Читаем фреймы и складируем в очередь
        for (int i = 0; i < framesCount; i++)
        {
            int size = frameSizes[i];
            if (size <= 0) continue;

            // Если вылезли за фактические размеры элемента — принудительно обрезаем
            if (StreamPositionWithBuffer() + size > payloadEndPos)
            {
                size = (int)(payloadEndPos - StreamPositionWithBuffer());
                if (size <= 0) break;
            }

            var mem = MemoryPool<byte>.Shared.Rent(size);
            await ReadExactAsync(mem.Memory[..size], ct).ConfigureAwait(false);
            _lacedFrames.Enqueue(new AudioBlock(mem, size, timestampMs, isKeyFrame));
        }

        // Финальный сейф-гард положения каретки
        if (StreamPositionWithBuffer() != payloadEndPos && _stream.CanSeek)
        {
            _stream.Position = payloadEndPos;
            _bufPos = 0;
            _bufLen = 0;
        }
    }

    #endregion

    #region Вспомогательные функции EBML VINT (Safe Bitwise)

    private async ValueTask<(uint Id, long Size)> ReadElementHeaderAsync(CancellationToken ct)
    {
        // 1. Пропускаем неограниченный нулевой паддинг (Zero Padding)
        int firstByte;
        while (true)
        {
            if (!await EnsureBufferedAsync(1, ct).ConfigureAwait(false)) return (0, 0);
            firstByte = BufferedReadByte();
            if (firstByte < 0) return (0, 0);
            if (firstByte != 0x00) break;
        }

        // 2. Парсим ID
        int idLength = GetVIntLength((byte)firstByte);
        if (idLength == 0 || idLength > 4)
            throw new InvalidDataException($"Invalid EBML ID VINT length: {idLength}");

        if (idLength > 1 && !await EnsureBufferedAsync(idLength - 1, ct).ConfigureAwait(false)) return (0, 0);

        uint id = (uint)firstByte;
        for (int i = 1; i < idLength; i++)
            id = (id << 8) | (uint)BufferedReadByte();

        // 3. Парсим Size
        if (!await EnsureBufferedAsync(1, ct).ConfigureAwait(false)) return (0, 0);
        firstByte = BufferedReadByte();
        if (firstByte < 0) return (0, 0);

        int sizeLength = GetVIntLength((byte)firstByte);
        if (sizeLength == 0 || sizeLength > 8)
            throw new InvalidDataException($"Invalid EBML Size VINT length: {sizeLength}");

        if (sizeLength > 1 && !await EnsureBufferedAsync(sizeLength - 1, ct).ConfigureAwait(false)) return (0, 0);

        long size = firstByte & (0xFF >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
            size = (size << 8) | (uint)BufferedReadByte();

        // Защита от Unknown Size (все биты 1)
        long maxVal = (1L << (7 * sizeLength)) - 1;
        if (size == maxVal) size = UNKNOWN_SIZE;

        return (id, size);
    }

    private async ValueTask<long> ReadVIntFromStreamAsync(CancellationToken ct)
    {
        await EnsureBufferedOrThrowAsync(1, "vint", ct).ConfigureAwait(false);
        int first = BufferedReadByte();
        int len = GetVIntLength((byte)first);
        if (len == 0 || len > 8)
            throw new InvalidDataException($"Invalid EBML VINT length: {len}");

        if (len == 1) return first & 0x7F;

        await EnsureBufferedOrThrowAsync(len - 1, "vint payload", ct).ConfigureAwait(false);
        long val = first & (0xFF >> len);
        for (int i = 1; i < len; i++)
            val = (val << 8) | (uint)BufferedReadByte();

        return val;
    }

    private async ValueTask<long> ReadSignedVIntFromStreamAsync(CancellationToken ct)
    {
        await EnsureBufferedOrThrowAsync(1, "signed vint", ct).ConfigureAwait(false);
        int first = BufferedReadByte();
        int len = GetVIntLength((byte)first);
        if (len == 0 || len > 8)
            throw new InvalidDataException($"Invalid EBML Signed VINT length: {len}");

        long val;
        if (len == 1)
        {
            val = first & 0x7F;
        }
        else
        {
            await EnsureBufferedOrThrowAsync(len - 1, "signed vint payload", ct).ConfigureAwait(false);
            val = first & (0xFF >> len);
            for (int i = 1; i < len; i++)
                val = (val << 8) | (uint)BufferedReadByte();
        }

        long shift = (1L << ((7 * len) - 1)) - 1;
        return val - shift;
    }

    private static long ReadVInt(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;

        int length = GetVIntLength(data[0]);
        if (length == 0 || length > data.Length) return 0;
        byte mask = (byte)(0xFF >> length);

        long value = data[0] & mask;
        for (int i = 1; i < length; i++)
            value = (value << 8) | data[i];

        return value;
    }

    private static int GetVIntLength(byte b)
    {
        if ((b & 0x80) != 0) return 1;
        if ((b & 0x40) != 0) return 2;
        if ((b & 0x20) != 0) return 3;
        if ((b & 0x10) != 0) return 4;
        if ((b & 0x08) != 0) return 5;
        if ((b & 0x04) != 0) return 6;
        if ((b & 0x02) != 0) return 7;
        if ((b & 0x01) != 0) return 8;
        return 0; // Invalid
    }

    private static long ReadUnsignedInt(ReadOnlySpan<byte> data)
    {
        long value = 0;
        foreach (byte b in data) value = (value << 8) | b;
        return value;
    }

    private static double ReadFloat(ReadOnlySpan<byte> data) => data.Length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(data),
        8 => BinaryPrimitives.ReadDoubleBigEndian(data),
        _ => 0
    };

    private async ValueTask EnsureBufferedOrThrowAsync(int needed, string context, CancellationToken ct)
    {
        if (needed <= 0) return;
        if (await EnsureBufferedAsync(needed, ct).ConfigureAwait(false)) return;
        throw new EndOfStreamException($"Unexpected EOF reading {context}.");
    }

    private static int CheckedSizeToInt32(long size, string context)
    {
        if (size == UNKNOWN_SIZE)
            throw new InvalidDataException($"WebM {context} has unknown size, which is unsupported for this element.");
        if (size is < 0 or > int.MaxValue)
            throw new InvalidDataException($"WebM {context} size {size} is out of supported memory range.");
        return (int)size;
    }

    #endregion

    #region Инструменты и Сброс

    private async ValueTask<bool> TryResyncToNextClusterAsync(CancellationToken ct)
    {
        _bufPos = 0;
        _bufLen = 0;
        if (!_stream.CanSeek) return false;

        long startPos = _stream.Position;
        long endPos = Math.Min(startPos + MaxResyncScanBytes, _stream.Length);

        int window = 0;
        while (_stream.Position < endPos && !ct.IsCancellationRequested)
        {
            await FillBufferAsync(ct).ConfigureAwait(false);
            if (_bufLen == 0) break;

            while (_bufPos < _bufLen)
            {
                window = (window << 8) | _readBuffer[_bufPos++];

                if (window == unchecked((int)CLUSTER_ID))
                {
                    long clusterStart = _stream.Position - (_bufLen - _bufPos) - 4;
                    _stream.Position = clusterStart;
                    _bufPos = 0;
                    _bufLen = 0;
                    _currentClusterTimecode = 0;
                    ResyncOccurred = true;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Находит позицию перехода для указанного времени в миллисекундах.
    /// </summary>
    public long? FindSeekPosition(long targetMs)
    {
        if (_cuePoints.Count == 0) return null;
        int low = 0, high = _cuePoints.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_cuePoints[mid].TimeMs <= targetMs) low = mid;
            else high = mid - 1;
        }
        return _segmentOffset + _cuePoints[low].ClusterOffset;
    }

    /// <summary>
    /// Сбрасывает состояние буферов парсера.
    /// </summary>
    public void Reset()
    {
        _bufPos = 0;
        _bufLen = 0;
        _currentClusterTimecode = 0;

        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}