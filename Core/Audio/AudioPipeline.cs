using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Normalization → Gain → Backend.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    #region Constants

    private const int ShortTrackIdLength = 8;
    private const int BufferFullDelayMs = 5;
    private const int DrainMinDelayMs = 50;
    private const int DrainMaxDelayMs = 500;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int PrematureEndToleranceMs = 2_000;

    /// <summary>Максимальная длительность isolated pre-scan в секундах.</summary>

    private const float IsolatedScanMaxSeconds = 30f;

    /// <summary>Decimation для Opus — Concentus.Native достаточно быстр при 5.</summary>
    private const int ScanDecimationFactorOpus = 5;

    /// <summary>Decimation для AAC — SharpJaad медленнее, берём 8.</summary>
    private const int ScanDecimationFactorAac = 8;

    /// <summary>Opus @ 48kHz: 20ms фрейм = 960 samples.</summary>
    private const int OpusNominalSamplesPerFrame = 960;

    /// <summary>AAC @ 44100Hz: ~23ms фрейм = 1024 samples.</summary>
    private const int AacNominalSamplesPerFrame = 1024;

    private const double LufsOffset = -0.691;
    private const double AbsoluteGateThresholdLufs = -70.0;
    private const double GatingBlockSeconds = 0.4;

    #endregion

    #region Fields

    private readonly IAudioSource _source;
    private readonly IAudioDecoder _decoder;
    private readonly IPlaybackBackend _backend;
    private readonly LockFreeRingBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    private readonly AudioStreamInfo _streamInfo;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly EbuR128Analyzer _analyzer;
    private TruePeakLimiter? _truePeakLimiter;
    private GainCrossfader _gainCrossfader;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    private TaskCompletionSource? _warmupTcs;
    private int _warmupThreshold;

    private int _skipFramesCounter;
    private int _decoderResetNeeded;
    private long _decodedSamples;
    private long _seekTargetMs = -1;
    private volatile bool _deviceLost;
    private Action? _onDeviceLostExternal;
    private Action? _onDeviceAvailableExternal;
    private Action? _onStarvationExternal;

    private Task? _deviceEventTask;

#if DEBUG
    private int _decoderRestartCount;
#endif

    #endregion

    #region Properties

    /// <summary>Потеряно ли аудиоустройство.</summary>
    public bool IsDeviceLost => _deviceLost;

    /// <summary>Метаинформация об аудиопотоке.</summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>Источник сырых аудио-фреймов.</summary>
    public IAudioSource Source => _source;

    /// <summary>Декодер аудио.</summary>
    public IAudioDecoder Decoder => _decoder;

    /// <summary>Backend системного звука.</summary>
    public IPlaybackBackend Backend => _backend;

    /// <summary>Pipeline уничтожен.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Sample rate декодера.</summary>
    public int SampleRate => _decoder.SampleRate;

    /// <summary>Количество каналов декодера.</summary>
    public int Channels => _decoder.Channels;

    /// <summary>Количество воспроизведённых сэмплов (decoded - buffered).</summary>
    public long PlayedSamples => Interlocked.Read(ref _decodedSamples) - _pcmBuffer.Count;

    /// <summary>Количество сэмплов в backend буфере.</summary>
    public int BackendBufferedSamples => _backend.BufferedSamples;

    /// <summary>Количество сэмплов в PCM ring buffer.</summary>
    public int BufferedSamples => _pcmBuffer.Count;

    /// <summary>Токен отмены времени жизни pipeline.</summary>
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

    /// <summary>EBU R128 анализатор нормализации.</summary>
    public EbuR128Analyzer Analyzer => _analyzer;

#if DEBUG
    /// <summary>Количество перезапусков decoder loop.</summary>
    public int DecoderRestartCount => Volatile.Read(ref _decoderRestartCount);
#endif

    #endregion

    #region Constructor

    private AudioPipeline(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> pcmBuffer,
        float[] decodeBuffer,
        AudioStreamInfo streamInfo,
        CancellationTokenSource lifetimeCts)
    {
        _source = source;
        _decoder = decoder;
        _backend = backend;
        _pcmBuffer = pcmBuffer;
        _decodeBuffer = decodeBuffer;
        _streamInfo = streamInfo;
        _lifetimeCts = lifetimeCts;

        _analyzer = new EbuR128Analyzer(decoder.SampleRate, decoder.Channels);
        _truePeakLimiter = new TruePeakLimiter(decoder.SampleRate);
        _gainCrossfader = new GainCrossfader(1.0f);
    }

    #endregion

    #region Factory

    /// <summary>
    /// Создаёт pipeline с shared backend из <see cref="ResolvedStreamDescriptor"/>.
    /// </summary>
    public static async Task<AudioPipeline> CreateAsync(
        ResolvedStreamDescriptor descriptor,
        Func<CancellationToken, Task<string?>>? urlAcquirer,
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        IPlaybackBackend sharedBackend,
        CancellationToken ct)
    {
        Log.Info($"[AudioPipeline] CreateAsync -> {descriptor}");

        var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        float[]? decodeBuffer = null;

        try
        {
            source = await AudioSourceFactory.CreateAsync(
                descriptor,
                Http.SharedHttpClient.Instance,
                urlAcquirer,
                urlRefresher,
                options.StreamingConfig,
                lifetimeCts.Token).ConfigureAwait(false);

            if (!await source.InitializeAsync(lifetimeCts.Token).ConfigureAwait(false))
            {
                lifetimeCts.Token.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                throw new AudioSourceException("Failed to initialize audio source");
            }

            decoder = CreateDecoder(source);

            int rawSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            int bufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(rawSize, 16));
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(descriptor, source, decoder);

            Log.Debug($"[AudioPipeline] StreamInfo built: track={streamInfo.TrackId}, container={streamInfo.Container}, codec={streamInfo.Codec}, bitrate={streamInfo.Bitrate}kbps, duration={streamInfo.DurationMs}ms, fromCache={streamInfo.IsFromCache}");

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer, streamInfo, lifetimeCts);

            try
            {
                sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);
            }
            catch (AudioDeviceException ex)
            {
                pipeline._deviceLost = true;
                Log.Warn($"[AudioPipeline] Created in degraded mode: {ex.Message}");
            }

            sharedBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);
            sharedBackend.SetStarvationCallback(pipeline.NotifyStarvation);
            sharedBackend.SetDeviceAvailableCallback(pipeline.NotifyDeviceAvailable);

            return pipeline;
        }
        catch (OperationCanceledException) { CleanupOnError(source, decoder, decodeBuffer, lifetimeCts); throw; }
        catch (AudioSourceException) { CleanupOnError(source, decoder, decodeBuffer, lifetimeCts); throw; }
        catch (Exception ex)
        {
            CleanupOnError(source, decoder, decodeBuffer, lifetimeCts);
            if (CancellationHelper.IsCancellationOrTokenCancelled(ex, ct))
                throw new OperationCanceledException("Pipeline creation cancelled", ex, ct);
            throw new AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    private static void CleanupOnError(IAudioSource? s, IAudioDecoder? d, float[]? buf, CancellationTokenSource cts)
    {
        try { d?.Dispose(); } catch { }
        try { s?.Dispose(); } catch { }
        if (buf != null) ArrayPool<float>.Shared.Return(buf);
        try { cts.Dispose(); } catch { }
    }

    private static IAudioDecoder CreateDecoder(IAudioSource source)
    {
        int rate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate;
        int ch = source.Channels > 0 ? source.Channels : DefaultChannels;

        return source.Codec switch
        {
            AudioCodec.Opus => new OpusDecoder(rate, ch),
            AudioCodec.Aac => CreateAacDecoder(source, rate, ch),
            _ => throw new NotSupportedException($"Codec {source.Codec} not supported")
        };
    }

    private static AacDecoder CreateAacDecoder(IAudioSource source, int rate, int ch)
    {
        var dec = new AacDecoder(rate, ch);
        if (source.DecoderConfig != null) dec.Initialize(source.DecoderConfig);
        return dec;
    }

    /// <summary>
    /// Строит <see cref="AudioStreamInfo"/> из дескриптора и runtime-параметров.
    /// Прямое маппирование без fallback cascade и HTTP detect.
    /// </summary>
    private static AudioStreamInfo BuildStreamInfo(
        ResolvedStreamDescriptor descriptor,
        IAudioSource source,
        IAudioDecoder decoder)
    {
        bool isFromCache = descriptor.Origin == StreamSource.DiskCacheFull
            || (source is Sources.LocalFileSource);

        return AudioStreamInfo.FromDescriptor(
            descriptor,
            sampleRate: decoder.SampleRate > 0 ? decoder.SampleRate : DefaultSampleRate,
            channels: decoder.Channels > 0 ? decoder.Channels : DefaultChannels,
            durationMs: source.DurationMs,
            isFromCache: isFromCache);
    }

    #endregion

    #region Device Loss

    internal void NotifyDeviceLost()
    {
        if (_disposed || _deviceLost) return;
        _deviceLost = true;

        Log.Error("[AudioPipeline] Audio device lost — soft pause (pipeline alive)");
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        var handler = _onDeviceLostExternal;
        if (handler != null)
            Volatile.Write(ref _deviceEventTask, Task.Run(handler));
    }

    internal void SetDeviceLostHandler(Action handler) => _onDeviceLostExternal = handler;

    internal async Task RecoverFromDeviceLossAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        if (_disposed || !_deviceLost) return;

        await StopDecodingAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        _backend.Flush();
        _pcmBuffer.Clear();

        _backend.Reinitialize(SampleRate, Channels, AudioCallback);
        _backend.SetDeviceLostCallback(NotifyDeviceLost);
        _backend.SetStarvationCallback(NotifyStarvation);
        _backend.SetDeviceAvailableCallback(NotifyDeviceAvailable);

        ct.ThrowIfCancellationRequested();
        _deviceLost = false;

        StartDecoding(urlRefresher, options, onTrackEnded, onError);
        Log.Info("[AudioPipeline] Recovered from device loss");
    }

    internal void NotifyDeviceAvailable()
    {
        if (_disposed || !_deviceLost) return;
        var handler = _onDeviceAvailableExternal;
        if (handler != null)
            Volatile.Write(ref _deviceEventTask, Task.Run(handler));
    }

    internal void SetDeviceAvailableHandler(Action handler) => _onDeviceAvailableExternal = handler;
    internal void SetStarvationHandler(Action handler) => _onStarvationExternal = handler;

    #endregion

    #region Decoder Loop

    public void StartDecoding(
     Func<CancellationToken, Task<string?>>? urlRefresher,
     AudioPlayerOptions options,
     Action? onTrackEnded,
     Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decoderTask is { IsCompleted: false }) return;

        _decoderCts?.Dispose();
        _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var token = _decoderCts.Token;

#if DEBUG
        int restartCount = Interlocked.Increment(ref _decoderRestartCount);
#endif

        _decoderTask = Task.Run(
            () => DecoderLoopAsync(urlRefresher, options, onTrackEnded, onError, token));

#if DEBUG
        var trackIdShort = _streamInfo.TrackId?.Length > ShortTrackIdLength
            ? _streamInfo.TrackId[..ShortTrackIdLength]
            : _streamInfo.TrackId ?? "?";

        if (restartCount > 1)
            Log.Debug($"[AudioPipeline] Decoder restart #{restartCount}: {trackIdShort}");
        else
            Log.Debug($"[AudioPipeline] Decoder started: {trackIdShort}");
#endif
    }

    public async Task StopDecodingAsync(TimeSpan timeout)
    {
        var cts = _decoderCts;
        var task = _decoderTask;
        if (cts == null || task == null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        if (_source is Sources.CachingStreamSource cachingSource)
            cachingSource.CancelActiveReads();

        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { Log.Warn("[AudioPipeline] Decoder stop timeout"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}"); }

        _decoderTask = null;
        _decoderCts = null;
        try { cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    private async Task DecoderLoopAsync(
       Func<CancellationToken, Task<string?>>? urlRefresher,
       AudioPlayerOptions options,
       Action? onTrackEnded,
       Action<Exception>? onError,
       CancellationToken ct)
    {
        int retryCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    await Task.Delay(BufferFullDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct).ConfigureAwait(false);
                    retryCount = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read transient cancel (retry {retryCount}/{options.MaxRetryAttempts})");
                    try { await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                catch (OperationCanceledException ex)
                {
                    Log.Error($"[AudioPipeline] Read failed after {retryCount} transient retries: {ex.Message}");
                    onError?.Invoke(ex);
                    break;
                }
                catch (UrlExpiredException) when (urlRefresher != null)
                {
                    Log.Warn($"[AudioPipeline] UrlExpiredException: track={_streamInfo.TrackId}, attempting refresh");
                    var newUrl = await urlRefresher(ct).ConfigureAwait(false);
                    Log.Warn($"[AudioPipeline] UrlExpiredException refresh result: track={_streamInfo.TrackId}, success={!string.IsNullOrEmpty(newUrl)}");
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        if (_source is Sources.CachingStreamSource cachingSource)
                        {
                            cachingSource.UpdateUrl(newUrl);
                            Log.Warn($"[AudioPipeline] Refreshed URL applied to source: track={_streamInfo.TrackId}");
                        }

                        continue;
                    }
                    throw;
                }
                catch (ChunkDownloadFatalException) { throw; }
                catch (FileNotFoundException ex)
                {
                    throw new CacheInvalidatedException(
                        "Cache file was deleted during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (DirectoryNotFoundException ex)
                {
                    throw new CacheInvalidatedException(
                        "Cache directory was deleted during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (IOException ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
                {
                    throw new CacheInvalidatedException(
                        "Cache file became unavailable during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (InvalidDataException) { throw; }
                catch (EndOfStreamException ex)
                {
                    Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
                    throw;
                }
                catch (Exception ex) when (ex is not CacheInvalidatedException && retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    try { await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (frame == null)
                {
                    if (ct.IsCancellationRequested) break;

                    if (IsPrematureEndOfStream())
                    {
                        var posMs = _source.PositionMs;
                        var durMs = _streamInfo.DurationMs;
                        Log.Warn($"[AudioPipeline] Truncated cache detected after resync: " +
                                 $"pos={posMs}ms/{durMs}ms — invalidating cache entry");

                        throw new CacheInvalidatedException(
                            $"Cache file is truncated (resync was required): reached {posMs}ms of {durMs}ms",
                            CacheInvalidationKind.ParserResync,
                            isRecoverable: true,
                            trackId: _streamInfo.TrackId);
                    }

                    await DrainBufferAsync(ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested) onTrackEnded?.Invoke();
                    break;
                }

                try
                {
                    int skipCount = Volatile.Read(ref _skipFramesCounter);

                    if (skipCount > 0)
                    {
                        if (Interlocked.CompareExchange(ref _decoderResetNeeded, 0, 1) == 1)
                            _decoder.FlushState();

                        _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
                    }

                    long seekTarget = Volatile.Read(ref _seekTargetMs);
                    if (seekTarget >= 0)
                    {
                        if (frame.Value.TimestampMs < seekTarget)
                        {
                            // Мы обязаны "прокрутить" сжатый фрейм через декодер,
                            // чтобы не сломать его внутренний state (overlap-add для Opus/AAC).
                            // Иначе при достижении seekTarget мы получим звуковые артефакты.
                            _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                            continue;
                        }
                        Volatile.Write(ref _seekTargetMs, -1L);
                    }

                    int samplesDecoded = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);

                    if (samplesDecoded > 0)
                    {
                        int totalSamples = samplesDecoded * _decoder.Channels;
                        _pcmBuffer.Write(_decodeBuffer.AsSpan(0, totalSamples));
                        Interlocked.Add(ref _decodedSamples, totalSamples);

                        int threshold = Volatile.Read(ref _warmupThreshold);
                        if (threshold > 0 && _pcmBuffer.Count >= threshold)
                        {
                            Volatile.Write(ref _warmupThreshold, 0);
                            var tcs = Interlocked.Exchange(ref _warmupTcs, null);
                            tcs?.TrySetResult();
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[AudioPipeline] Decode error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (CacheInvalidatedException ex)
        {
            Log.Warn($"[AudioPipeline] Cache invalidated: {ex.Message}");
            onError?.Invoke(ex);
        }
        catch (Exception ex) when (ex is not CacheInvalidatedException)
        {
            Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
            onError?.Invoke(ex);
        }
    }

    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
        {
            int remainingSamples = _pcmBuffer.Count;
            int samplesPerSecond = SampleRate * Channels;
            int estimatedMs = samplesPerSecond > 0
                ? remainingSamples * 1000 / samplesPerSecond / 2
                : DrainMinDelayMs;

            await Task.Delay(Math.Clamp(estimatedMs, DrainMinDelayMs, DrainMaxDelayMs), ct).ConfigureAwait(false);
        }
    }

    private void ArmDecoderWarmupAfterSeek(long targetMs)
    {
        int skipFrames = GetSkipFramesAfterSeek(_source.Codec);

        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Interlocked.Exchange(ref _decoderResetNeeded, skipFrames > 0 ? 1 : 0);
        Volatile.Write(ref _seekTargetMs, targetMs);
    }

    private static int GetSkipFramesAfterSeek(AudioCodec codec)
    {
        return codec switch
        {
            AudioCodec.Opus => SkipFramesAfterSeekOpus,
            AudioCodec.Aac => SkipFramesAfterSeekAac,
            _ => 0
        };
    }

    #endregion

    #region Playback Control

    public void ActivateFillLoop()
    {
        if (_disposed) return;
        _backend.ActivateFillLoop();
    }

    public void ActivateBufferingMode()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(true);
        _backend.ActivateFillLoop();
    }

    public bool WaitForBackendWarmup(int timeoutMs = 100)
    {
        if (_disposed) return false;
        return _backend.WaitForWarmup(timeoutMs);
    }

    public void Start()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(true);
        _backend.Start();
    }

    public void Stop()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(false);
        _backend.Stop();
    }

    public void Flush()
    {
        if (_disposed) return;
        _backend.Flush();
        _pcmBuffer.Clear();

        Volatile.Write(ref _warmupThreshold, 0);
        var tcs = Interlocked.Exchange(ref _warmupTcs, null);
        tcs?.TrySetResult();

        Log.Debug("[AudioPipeline] Flushed");
    }

    internal void NotifyStarvation()
    {
        if (_disposed) return;

        var decoderAlive = _decoderTask is { IsCompleted: false };
        Log.Error($"[AudioPipeline] Starvation: decoder={(decoderAlive ? "alive" : "dead")}, ring={_pcmBuffer.Count}");

        var handler = _onStarvationExternal;
        if (handler != null)
            Volatile.Write(ref _deviceEventTask, Task.Run(handler));
    }

    public void PrepareForSeek(long targetMs = -1)
    {
        ArmDecoderWarmupAfterSeek(targetMs);

        _analyzer.PrepareForSeek();
        _truePeakLimiter?.Reset();

        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);

        Volatile.Write(ref _warmupThreshold, 0);
        var tcs = Interlocked.Exchange(ref _warmupTcs, null);
        tcs?.TrySetResult();
    }

    /// <summary>
    /// Выполняет pre-scan нормализации через isolated pipeline.
    /// Не затрагивает <see cref="_source"/>, <see cref="_decoder"/> или <see cref="_decodeBuffer"/>.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_analyzer.IsEnabled) return;
        if (_analyzer.IsGainLocked) return;

        if (_source is not Sources.LocalFileSource localSource)
        {
            Log.Debug("[AudioPipeline] Pre-scan skipped: source is not LocalFileSource");
            return;
        }

        try
        {
            var (integratedLufs, rawGain) = await RunIsolatedPreScanAsync(
                localSource.FilePath,
                localSource.Codec,
                _analyzer.CurrentConfig.TargetLufs,
                _analyzer.CurrentConfig.MaxGain,
                ct).ConfigureAwait(false);

            if (float.IsFinite(integratedLufs))
                _analyzer.NotifyIntegratedLufs(integratedLufs);

            _analyzer.LockGain(rawGain);

            Log.Debug($"[AudioPipeline] Isolated pre-scan complete: " +
                      $"lufs={integratedLufs:F2}, gain={rawGain:F4}x, file={Path.GetFileName(localSource.FilePath)}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Isolated pre-scan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет EBU R128 pre-scan через полностью изолированный pipeline.
    /// </summary>
    /// <remarks>
    /// Создаёт и уничтожает собственные FileStream, IContainerParser и IAudioDecoder.
    /// Shared <c>_source</c>, <c>_decoder</c> и <c>_decodeBuffer</c> не затрагиваются.
    /// <c>SeekAsync(0)</c> после завершения не нужен.
    /// <para>Decode buffer и K-weight filter buffer арендуются из <see cref="ArrayPool{T}.Shared"/>
    /// и возвращаются в <c>finally</c>.</para>
    /// </remarks>
    /// <param name="filePath">Путь к аудио файлу.</param>
    /// <param name="codec">Кодек файла (Opus/AAC).</param>
    /// <param name="targetLufs">Целевой уровень LUFS.</param>
    /// <param name="maxGain">Максимальный допустимый gain.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Пара (integratedLufs, rawGain). rawGain = 1.0f если scan не дал результата.</returns>
    private static async Task<(float IntegratedLufs, float RawGain)> RunIsolatedPreScanAsync(
     string filePath,
     AudioCodec codec,
     float targetLufs,
     float maxGain,
     CancellationToken ct,
     float scanMaxSeconds = IsolatedScanMaxSeconds)
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

            var format = await DetectIsolatedFormatAsync(fs, ct).ConfigureAwait(false);
            if (format == AudioFormat.Unknown)
                return (float.NaN, 1.0f);

            parser = CreateIsolatedParser(format, fs);
            if (!await parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                return (float.NaN, 1.0f);

            int sampleRate = parser.SampleRate > 0 ? parser.SampleRate : AudioConstants.DefaultSampleRate;
            int channels = parser.Channels > 0 ? parser.Channels : AudioConstants.DefaultChannels;

            decoder = CreateIsolatedDecoder(codec, parser, sampleRate, channels);

            int maxFrames = DecoderBufferFrames * channels;
            decodeBuffer = ArrayPool<float>.Shared.Rent(maxFrames);
            filteredBuffer = ArrayPool<double>.Shared.Rent(maxFrames);

            int decimationFactor = codec == AudioCodec.Aac
                ? ScanDecimationFactorAac
                : ScanDecimationFactorOpus;

            int nominalSamplesPerFrame = codec == AudioCodec.Aac
                ? AacNominalSamplesPerFrame
                : OpusNominalSamplesPerFrame;

            return await ScanFramesAsync(
                parser, decoder, sampleRate, channels,
                decodeBuffer, filteredBuffer,
                targetLufs, maxGain,
                decimationFactor, nominalSamplesPerFrame,
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

    /// <summary>
    /// Определяет формат по magic bytes и сбрасывает позицию потока.
    /// </summary>
    private static async Task<AudioFormat> DetectIsolatedFormatAsync(FileStream fs, CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(AudioConstants.FormatDetectionHeaderSize);
        try
        {
            int totalRead = 0;
            while (totalRead < AudioConstants.FormatDetectionHeaderSize)
            {
                int read = await fs.ReadAsync(
                    header.AsMemory(totalRead, AudioConstants.FormatDetectionHeaderSize - totalRead), ct)
                    .ConfigureAwait(false);
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

    /// <summary>Создаёт isolated parser для указанного формата и FileStream.</summary>
    private static IContainerParser CreateIsolatedParser(AudioFormat format, FileStream fs) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new Parsers.WebMContainerParser(fs),
        AudioFormat.Mp4 => new Parsers.Mp4ContainerParser(fs),
        _ => throw new NotSupportedException($"[AudioPipeline] Isolated scan: unsupported format {format}")
    };

    /// <summary>Создаёт isolated decoder с учётом codec-specific инициализации.</summary>
    private static IAudioDecoder CreateIsolatedDecoder(
        AudioCodec codec,
        IContainerParser parser,
        int sampleRate,
        int channels) => codec switch
        {
            AudioCodec.Opus => new Decoders.OpusDecoder(sampleRate, channels),
            AudioCodec.Aac => CreateIsolatedAacDecoder(parser, sampleRate, channels),
            _ => throw new NotSupportedException($"[AudioPipeline] Isolated scan: unsupported codec {codec}")
        };

    private static IAudioDecoder CreateIsolatedAacDecoder(IContainerParser parser, int sampleRate, int channels)
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
        var scanFilter = new Helpers.KWeightingFilter(sampleRate, channels);
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
                    double val = System.Runtime.CompilerServices.Unsafe.Add(ref filteredRef, offset + ch);
                    System.Runtime.CompilerServices.Unsafe.Add(ref sumSqRef, ch) += val * val;
                }

                if (++blockFrameCount >= gatingBlockSize)
                {
                    double channelPowerSum = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                        channelPowerSum += System.Runtime.CompilerServices.Unsafe.Add(ref sumSqRef, ch) / blockFrameCount;

                    double blockLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(channelPowerSum, 1e-20));

                    if (blockLufs > AbsoluteGateThresholdLufs && blockCount < EbuR128Analyzer.MaxScanGatingBlocks)
                        blockPowers[blockCount++] = channelPowerSum;

                    Array.Clear(blockSumSq, 0, channels);
                    blockFrameCount = 0;
                }
            }
        }

        float integratedLufs = EbuR128Analyzer.ComputeIntegratedLufsFromBlocks(blockPowers, blockCount);
        float rawGain = EbuR128Analyzer.ComputeIntegratedGainFromBlocks(blockPowers, blockCount, targetLufs, maxGain);

        return (integratedLufs, rawGain);
    }

    public void SetDecodedSamplesPosition(long samples) =>
        Interlocked.Exchange(ref _decodedSamples, samples);

    public float GetLockedNormalizationGain() => _analyzer.GetLockedGain();

    public void SetInitialNormalizationGain(float gain)
    {
        if (_disposed) return;
        _analyzer.SetInitialGain(gain);
    }

    public void SnapCrossfaderToGain()
    {
        if (_disposed) return;
        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);
    }

    private bool IsPrematureEndOfStream()
    {
        long durationMs = _streamInfo.DurationMs;
        if (durationMs <= 0) return false;

        long positionMs = _source.PositionMs;
        if (positionMs < 0) return false;

        return positionMs + PrematureEndToleranceMs < durationMs;
    }

    #endregion

    #region Audio Callback

    private int AudioCallback(Span<float> buffer)
    {
        if (_disposed) { buffer.Clear(); return 0; }

        int read = _pcmBuffer.Read(buffer);
        if (read < buffer.Length) buffer[read..].Clear();

        if (read > 0)
        {
            var samples = buffer[..read];

            float normGain = _analyzer.IsEnabled
                ? _analyzer.ProcessSamples(samples)
                : 1.0f;

            _gainCrossfader.SetTarget(normGain, _decoder.SampleRate, _decoder.Channels);

            bool canBypassLimiter = normGain <= 1.0f && _truePeakLimiter!.EnvelopeGain >= 0.999f;

            if (canBypassLimiter)
            {
                if (_gainCrossfader.IsActive)
                {
                    ApplyGainWithCrossfade(samples, ref _gainCrossfader);
                }
                else if (MathF.Abs(normGain - 1.0f) > 0.0001f)
                {
                    ApplyConstantGain(samples, normGain);
                }
            }
            else
            {
                _truePeakLimiter!.Process(samples, ref _gainCrossfader);
            }
        }

        return read / _decoder.Channels;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyConstantGain(Span<float> samples, float gain)
    {
        int i = 0;
        int vectorSize = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && samples.Length >= vectorSize)
        {
            var gainVector = new Vector<float>(gain);
            for (; i <= samples.Length - vectorSize; i += vectorSize)
            {
                var vector = new Vector<float>(samples.Slice(i, vectorSize));
                (vector * gainVector).CopyTo(samples.Slice(i, vectorSize));
            }
        }

        for (; i < samples.Length; i++)
            samples[i] *= gain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGainWithCrossfade(Span<float> samples, ref GainCrossfader crossfader)
    {
        ref float samplesRef = ref MemoryMarshal.GetReference(samples);
        int len = samples.Length;
        for (int i = 0; i < len; i++)
            Unsafe.Add(ref samplesRef, i) *= crossfader.Advance();
    }

    #endregion

    #region Buffer Info

    public async Task<bool> WaitForBufferAsync(int minSamples, int maxWaitMs, CancellationToken ct)
    {
        if (_disposed || _pcmBuffer.Count >= minSamples) return true;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _warmupTcs, tcs);
        Volatile.Write(ref _warmupThreshold, minSamples);

        if (_pcmBuffer.Count >= minSamples)
        {
            Volatile.Write(ref _warmupThreshold, 0);
            tcs.TrySetResult();
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var delayTask = Task.Delay(maxWaitMs, timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

            if (completedTask == tcs.Task)
            {
                timeoutCts.Cancel();
                return true;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }
        finally
        {
            Volatile.Write(ref _warmupThreshold, 0);
            Interlocked.CompareExchange(ref _warmupTcs, null, tcs);
        }
    }

    #endregion

    #region Dispose

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        var deviceTask = Volatile.Read(ref _deviceEventTask);
        if (deviceTask != null && !deviceTask.IsCompleted)
        {
            try
            {
                await deviceTask
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        if (_decoderTask != null)
        {
            try
            {
                await _decoderTask
                    .WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        _decoder.Dispose();
        await _source.DisposeAsync().ConfigureAwait(false);
        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); } catch (ObjectDisposedException) { }
        try { _lifetimeCts.Dispose(); } catch (ObjectDisposedException) { }

#if DEBUG
        Log.Debug($"[AudioPipeline] Disposed (decoder restarts: {_decoderRestartCount})");
#else
        Log.Info("[AudioPipeline] Disposed");
#endif
    }

    #endregion
}