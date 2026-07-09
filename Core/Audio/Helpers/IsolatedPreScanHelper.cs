using System.Buffers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Статический helper для EBU R128 pre-scan через полностью изолированный pipeline.
/// </summary>
/// <remarks>
/// <para>Создаёт и уничтожает собственные <see cref="FileStream"/>,
/// <see cref="IContainerParser"/> и <see cref="IAudioDecoder"/>.
/// Shared-ресурсы вызывающего <c>AudioPipeline</c> не затрагиваются.</para>
/// <para>Все decode/filter буферы арендуются из <see cref="ArrayPool{T}.Shared"/>
/// и возвращаются в <c>finally</c>.</para>
/// </remarks>
internal static class IsolatedPreScanHelper
{
    #region Constants

    /// <summary>Decimation для Opus — Concentus.Native достаточно быстр при 5.</summary>
    private const int DecimationFactorOpus = 5;

    /// <summary>Decimation для AAC — SharpJaad медленнее, берём 8.</summary>
    private const int DecimationFactorAac = 8;

    /// <summary>Opus @ 48kHz: 20ms фрейм = 960 samples.</summary>
    private const int NominalSamplesOpus = 960;

    /// <summary>AAC @ 44100Hz: ~23ms фрейм = 1024 samples.</summary>
    private const int NominalSamplesAac = 1024;

    private const double LufsOffset = -0.691;
    private const double AbsoluteGateThresholdLufs = -70.0;
    private const double GatingBlockSeconds = 0.4;

    #endregion

    /// <summary>
    /// Выполняет EBU R128 pre-scan для указанного аудио файла.
    /// </summary>
    /// <param name="filePath">Путь к аудио файлу (local или cache).</param>
    /// <param name="codec">Кодек файла (Opus / AAC).</param>
    /// <param name="targetLufs">Целевой уровень LUFS нормализации.</param>
    /// <param name="maxGain">Максимальный допустимый gain.</param>
    /// <param name="scanMaxSeconds">Максимальная длительность scan в секундах.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Пара <c>(IntegratedLufs, RawGain)</c>.
    /// <c>RawGain = 1.0f</c>, если scan не дал результата.
    /// <c>IntegratedLufs = <see cref="float.NaN"/></c> при ошибке формата.
    /// </returns>
    public static async Task<(float IntegratedLufs, float RawGain)> RunAsync(
        string filePath,
        AudioCodec codec,
        float targetLufs,
        float maxGain,
        float scanMaxSeconds,
        CancellationToken ct)
    {
        FileStream? fs = null;
        IContainerParser? parser = null;
        IAudioDecoder? decoder = null;
        float[]? decodeBuffer = null;
        double[]? filteredBuffer = null;

        try
        {
            fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: AudioConstants.CacheFileBufferSize,
                useAsync: false);

            var format = await DetectFormatAsync(fs, ct).ConfigureAwait(false);
            if (format == AudioFormat.Unknown)
                return (float.NaN, 1.0f);

            parser = CreateParser(format, fs);
            if (!await parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                return (float.NaN, 1.0f);

            int sampleRate = parser.SampleRate > 0
                ? parser.SampleRate
                : AudioConstants.DefaultSampleRate;
            int channels = parser.Channels > 0
                ? parser.Channels
                : AudioConstants.DefaultChannels;

            decoder = CreateDecoder(codec, parser, sampleRate, channels);

            int maxFrames = AudioConstants.DecoderBufferFrames * channels;
            decodeBuffer = ArrayPool<float>.Shared.Rent(maxFrames);
            filteredBuffer = ArrayPool<double>.Shared.Rent(maxFrames);

            int decimationFactor = codec == AudioCodec.Aac ? DecimationFactorAac : DecimationFactorOpus;
            int nominalSamples = codec == AudioCodec.Aac ? NominalSamplesAac : NominalSamplesOpus;

            return await ScanFramesAsync(
                parser, decoder,
                sampleRate, channels,
                decodeBuffer, filteredBuffer,
                targetLufs, maxGain,
                decimationFactor, nominalSamples,
                scanMaxSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            decoder?.Dispose();
            if (parser != null) await parser.DisposeAsync().ConfigureAwait(false);
            if (fs != null) await fs.DisposeAsync().ConfigureAwait(false);
            if (decodeBuffer != null) ArrayPool<float>.Shared.Return(decodeBuffer);
            if (filteredBuffer != null) ArrayPool<double>.Shared.Return(filteredBuffer);
        }
    }

    #region Private Helpers

    /// <summary>Определяет формат по magic bytes и сбрасывает позицию потока.</summary>
    private static async Task<AudioFormat> DetectFormatAsync(FileStream fs, CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(AudioConstants.FormatDetectionHeaderSize);
        try
        {
            int totalRead = 0;
            while (totalRead < AudioConstants.FormatDetectionHeaderSize)
            {
                int read = await fs.ReadAsync(
                    header.AsMemory(totalRead, AudioConstants.FormatDetectionHeaderSize - totalRead),
                    ct).ConfigureAwait(false);

                if (read == 0) break;
                totalRead += read;
            }
            fs.Position = 0;
            return AudioSourceFactory.DetectFormatByMagic(header.AsSpan(0, totalRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>Создаёт изолированный parser для указанного формата.</summary>
    /// <exception cref="NotSupportedException">Неподдерживаемый формат.</exception>
    private static IContainerParser CreateParser(AudioFormat format, FileStream fs) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new Parsers.WebMContainerParser(fs),
        AudioFormat.Mp4 => new Parsers.Mp4ContainerParser(fs),
        _ => throw new NotSupportedException(
            $"[IsolatedPreScanHelper] Unsupported format: {format}")
    };

    /// <summary>Создаёт изолированный decoder с учётом codec-specific инициализации.</summary>
    /// <exception cref="NotSupportedException">Неподдерживаемый кодек.</exception>
    private static IAudioDecoder CreateDecoder(
        AudioCodec codec, IContainerParser parser, int sampleRate, int channels) => codec switch
        {
            AudioCodec.Opus => new Decoders.OpusDecoder(sampleRate, channels),
            AudioCodec.Aac => CreateAacDecoder(parser, sampleRate, channels),
            _ => throw new NotSupportedException(
                $"[IsolatedPreScanHelper] Unsupported codec: {codec}")
        };

    private static Decoders.AacDecoder CreateAacDecoder(
        IContainerParser parser, int sampleRate, int channels)
    {
        var dec = new Decoders.AacDecoder(sampleRate, channels);
        if (parser.DecoderConfig != null)
            dec.Initialize(parser.DecoderConfig);
        return dec;
    }

    /// <summary>
    /// Основной цикл чтения фреймов и вычисления EBU R128 integrated LUFS.
    /// </summary>
    private static async Task<(float IntegratedLufs, float RawGain)> ScanFramesAsync(
        IContainerParser parser,
        IAudioDecoder decoder,
        int sampleRate,
        int channels,
        float[] decodeBuffer,
        double[] filteredBuffer,
        float targetLufs,
        float maxGain,
        int decimationFactor,
        int nominalSamplesPerFrame,
        float scanMaxSeconds,
        CancellationToken ct)
    {
        var scanFilter = new KWeightingFilter(sampleRate, channels);
        var blockSumSq = new double[channels];
        var blockPowers = new double[EbuR128Analyzer.MaxScanGatingBlocks];

        int blockCount = 0;
        int blockFrameCount = 0;
        long totalFrames = 0;
        long maxFrames = (long)(sampleRate * scanMaxSeconds);
        int gatingBlockSize = (int)(sampleRate * GatingBlockSeconds);
        int frameIndex = 0;

        while (!ct.IsCancellationRequested && totalFrames < maxFrames)
        {
            var frame = await parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
            if (frame == null) break;

            totalFrames += nominalSamplesPerFrame;
            frameIndex++;

            if (frameIndex % decimationFactor != 0)
                continue;

            scanFilter.Reset();

            int decoded = decoder.Decode(frame.Value.Data.Span, decodeBuffer);
            if (decoded <= 0) continue;

            int samplesToProcess = decoded * channels;

            scanFilter.ProcessBlock(
                decodeBuffer.AsSpan(0, samplesToProcess),
                filteredBuffer.AsSpan(0, samplesToProcess));

            ref double filteredRef = ref System.Runtime.InteropServices.MemoryMarshal
                .GetArrayDataReference(filteredBuffer);
            ref double sumSqRef = ref System.Runtime.InteropServices.MemoryMarshal
                .GetArrayDataReference(blockSumSq);

            for (int f = 0; f < decoded; f++)
            {
                int offset = f * channels;
                for (int ch = 0; ch < channels; ch++)
                {
                    double val = System.Runtime.CompilerServices.Unsafe
                        .Add(ref filteredRef, offset + ch);
                    System.Runtime.CompilerServices.Unsafe.Add(ref sumSqRef, ch) += val * val;
                }

                if (++blockFrameCount >= gatingBlockSize)
                {
                    double channelPowerSum = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                        channelPowerSum += System.Runtime.CompilerServices.Unsafe
                            .Add(ref sumSqRef, ch) / blockFrameCount;

                    double blockLufs = LufsOffset + 10.0 * Math.Log10(
                        Math.Max(channelPowerSum, 1e-20));

                    if (blockLufs > AbsoluteGateThresholdLufs
                        && blockCount < EbuR128Analyzer.MaxScanGatingBlocks)
                    {
                        blockPowers[blockCount++] = channelPowerSum;
                    }

                    Array.Clear(blockSumSq, 0, channels);
                    blockFrameCount = 0;
                }
            }
        }

        float integratedLufs = EbuR128Analyzer.ComputeIntegratedLufsFromBlocks(
            blockPowers, blockCount);
        float rawGain = EbuR128Analyzer.ComputeIntegratedGainFromBlocks(
            blockPowers, blockCount, targetLufs, maxGain);

        return (integratedLufs, rawGain);
    }

    #endregion
}