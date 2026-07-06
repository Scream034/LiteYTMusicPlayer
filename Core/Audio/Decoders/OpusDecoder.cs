using System.Numerics;
using System.Runtime.InteropServices;
using Concentus;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// OPUS декодер на базе Concentus.
/// Оптимизирован с SIMD конвертацией short→float.
/// </summary>
public sealed class OpusDecoder : IAudioDecoder
{
    private const int MaxFrameDurationMs = 120;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;
    private bool _disposed;

    public OpusDecoder(int sampleRate = 48000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MaxFrameSize = sampleRate * MaxFrameDurationMs / 1000;
        _shortBuffer = new short[MaxFrameSize * channels];

        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);

        Log.Debug($"[OpusDecoder] Created: {sampleRate}Hz, {channels}ch");
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int MaxFrameSize { get; }
    public AudioCodec Codec => AudioCodec.Opus;
    public bool IsInitialized => true; // Opus не требует отдельной инициализации

    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedData.IsEmpty)
            return DecodePLC(outputBuffer);

        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, MaxFrameSize * Channels);
            int samples = _decoder.Decode(encodedData, shortSpan, MaxFrameSize);

            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
            return samples;
        }
        catch (Exception ex)
        {
            Log.Warn($"[OpusDecoder] Decode error: {ex.Message}");
            throw new AudioDecoderException($"OPUS decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Декодирует фрейм после seek с seek-safe семантикой сброса состояния.
    /// </summary>
    /// <param name="encodedData">Сжатые данные одного Opus-фрейма.</param>
    /// <param name="outputBuffer">Выходной PCM float32 буфер.</param>
    /// <returns>Количество декодированных семплов на канал.</returns>
    /// <remarks>
    /// <para><b>Почему здесь нет <c>ResetState()</c>:</b> Concentus предоставляет hard reset,
    /// который обнуляет весь внутренний decoder state. Для seek это избыточно:
    /// pipeline уже декодирует и отбрасывает несколько warm-up skip-фреймов,
    /// которые естественно вытесняют переходное состояние без грубого hard reset.</para>
    /// <para>Такой подход уменьшает риск слышимого артефакта на границе seek и
    /// оставляет единую семантику: seek-подготовка выполняется через
    /// <see cref="FlushState"/>, а не через отдельный destructive reset-path.</para>
    /// </remarks>
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        FlushState();
        return Decode(encodedData, outputBuffer);
    }

    /// <summary>
    /// Выполняет лёгкий flush внутреннего состояния Opus-декодера.
    /// </summary>
    /// <remarks>
    /// <para>Метод намеренно ничего не делает. В Concentus нет отдельного soft flush:
    /// доступен только <c>ResetState()</c>, а это hard reset всего состояния декодера.</para>
    /// <para>Для seek в Opus это хуже, чем обычный decode с последующим discard нескольких
    /// warm-up фреймов: skip-фреймы сами вытесняют переходное состояние без грубого
    /// обнуления overlap/prediction state.</para>
    /// </remarks>
    public void FlushState()
    {
        // Intentionally no-op for Opus/Concentus.
    }

    private int DecodePLC(Span<float> outputBuffer)
    {
        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan();
            int samples = _decoder.Decode([], shortSpan, MaxFrameSize);
            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
            return samples;
        }
        catch
        {
            // Fallback: возвращаем тишину (960 samples = 20ms @ 48kHz)
            const int fallbackSamples = 960;
            int totalSamples = fallbackSamples * Channels;
            outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
            return fallbackSamples;
        }
    }

    /// <summary>
    /// SIMD-оптимизированная конвертация short[] → float[].
    /// На hot path (~50 вызовов/сек × 960 samples = ~48000 операций/сек).
    /// </summary>
    /// <remarks>
    /// <para>При наличии аппаратного SIMD (SSE2/AVX2/NEON) обрабатывает
    /// по 8 (Vector128) или 16 (Vector256) сэмплов за одну инструкцию.</para>
    /// </remarks>
    internal static void ConvertShortToFloat(ReadOnlySpan<short> src, Span<float> dst)
    {
        const float scale = 1f / 32768f;
        int len = Math.Min(src.Length, dst.Length);
        int i = 0;

        // SIMD путь — обрабатываем по Vector<short>.Count сэмплов за раз
        if (Vector.IsHardwareAccelerated && len >= Vector<short>.Count)
        {
            var scaleVec = new Vector<float>(scale);
            var shorts = MemoryMarshal.Cast<short, Vector<short>>(src[..len]);

            // Каждый Vector<short> содержит N shorts, из них получаются 2 Vector<float>
            // (потому что float вдвое шире short)
            int floatVecCount = Vector<float>.Count;

            for (int vi = 0; vi < shorts.Length; vi++)
            {
                // Widen: Vector<short> → 2 × Vector<int>
                Vector.Widen(shorts[vi], out var lo, out var hi);

                // Convert int→float и масштабируем
                var floatLo = Vector.ConvertToSingle(lo) * scaleVec;
                var floatHi = Vector.ConvertToSingle(hi) * scaleVec;

                // Записываем результат
                int baseIdx = vi * Vector<short>.Count;
                if (baseIdx + floatVecCount <= len)
                    floatLo.CopyTo(dst.Slice(baseIdx, floatVecCount));
                if (baseIdx + floatVecCount * 2 <= len)
                    floatHi.CopyTo(dst.Slice(baseIdx + floatVecCount, floatVecCount));
            }

            i = shorts.Length * Vector<short>.Count;
        }

        // Скалярный хвост
        for (; i < len; i++)
        {
            dst[i] = src[i] * scale;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _decoder.ResetState();

        if (_decoder is IDisposable d)
        {
            d.Dispose();
        }
    }
}