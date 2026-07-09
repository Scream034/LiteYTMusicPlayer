using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private const int BufferFullDelayMs = 5;
    private const int DrainMinDelayMs = 50;
    private const int DrainMaxDelayMs = 500;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int PrematureEndToleranceMs = 2_000;

    /// <summary>Максимальная длительность isolated pre-scan в секундах.</summary>
    private const float IsolatedScanMaxSeconds = 30f;

    /// <summary>Минимум секунд аудио для адекватного EBU R128 pre-scan (±1-2 LU).</summary>
    private const float MinPreScanSeconds = 10f;

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
    private readonly TruePeakLimiter? _truePeakLimiter;
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

        _analyzer = new EbuR128Analyzer();
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
        const int ShortTrackIdLength = 8;

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
        int requiredSpace = _decoder.MaxFrameSize * _decoder.Channels;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Быстрый путь: проверяем место по кэшированному Tail, не дергая шину памяти
                if (_pcmBuffer.ProducerCachedAvailable < requiredSpace)
                {
                    // Медленный путь: жесткая синхронизация (внутри обновит кэш, если Tail сдвинулся)
                    if (_pcmBuffer.Available < requiredSpace)
                    {
                        await Task.Delay(BufferFullDelayMs, ct).ConfigureAwait(false);
                        continue;
                    }
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

        Volatile.Write(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _decoderResetNeeded, skipFrames > 0 ? 1 : 0);
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
    /// Поддерживает <see cref="Sources.LocalFileSource"/> (полный кэш)
    /// и <see cref="Sources.CachingStreamSource"/> (partial cache с достаточным contiguous prefix).
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_analyzer.IsEnabled) return;
        if (_analyzer.IsGainLocked) return;

        if (_source is Sources.LocalFileSource localSource)
        {
            await RunPreScanForFileAsync(
                localSource.FilePath, localSource.Codec, ct).ConfigureAwait(false);
            return;
        }

        if (_source is Sources.CachingStreamSource cachingSource)
        {
            var cacheManager = AudioSourceFactory.GlobalCache;
            if (cacheManager == null) return;

            long contiguousBytes = cachingSource.ContiguousPrefixBytes;
            int bitrate = cachingSource.Bitrate;
            long minBytes = (long)(Math.Max(1, bitrate) * 1000.0 / 8.0 * MinPreScanSeconds);

            if (contiguousBytes < minBytes)
            {
                Log.Debug($"[AudioPipeline] Pre-scan skipped: insufficient contiguous prefix " +
                          $"({contiguousBytes / 1024}KB < {minBytes / 1024}KB for {MinPreScanSeconds}s)");
                return;
            }

            string cachePath = cacheManager.GetCachePath(cachingSource.CacheKey);
            if (!File.Exists(cachePath)) return;

            float prefixSeconds = (float)(contiguousBytes / (Math.Max(1, bitrate) * 1000.0 / 8.0));
            float scanSeconds = Math.Min(prefixSeconds, IsolatedScanMaxSeconds);

            await RunPreScanForFileAsync(
                cachePath, cachingSource.Codec, ct, scanSeconds).ConfigureAwait(false);
            return;
        }

        Log.Debug($"[AudioPipeline] Pre-scan skipped: unsupported source type {_source.GetType().Name}");
    }

    private async Task RunPreScanForFileAsync(
     string filePath, AudioCodec codec, CancellationToken ct,
     float scanMaxSeconds = IsolatedScanMaxSeconds)
    {
        try
        {
            var (integratedLufs, rawGain) = await IsolatedPreScanHelper.RunAsync(
                filePath, codec,
                _analyzer.CurrentConfig.TargetLufs,
                _analyzer.CurrentConfig.MaxGain,
                scanMaxSeconds,
                ct).ConfigureAwait(false);

            if (float.IsFinite(integratedLufs))
                _analyzer.NotifyIntegratedLufs(integratedLufs);

            _analyzer.LockGain(rawGain);

            Log.Debug($"[AudioPipeline] Pre-scan complete: lufs={integratedLufs:F2}, " +
                      $"gain={rawGain:F4}x, limit={scanMaxSeconds:F1}s, " +
                      $"file={Path.GetFileName(filePath)}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Pre-scan failed: {ex.Message}");
        }
    }

    public void SetDecodedSamplesPosition(long samples) =>
        Volatile.Write(ref _decodedSamples, samples);

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
                ? _analyzer.ProcessSamples()
                : 1.0f;

            _gainCrossfader.SetTarget(normGain, _decoder.SampleRate, _decoder.Channels);

            bool canBypassLimiter = normGain <= 1.0f && _truePeakLimiter!.EnvelopeGain >= 0.999f;

            if (canBypassLimiter)
            {
                if (_gainCrossfader.IsActive)
                    SpanMathHelper.MultiplyByCrossfade(samples, ref _gainCrossfader);
                else if (MathF.Abs(normGain - 1.0f) > 0.0001f)
                    SpanMathHelper.MultiplyByConstant(samples, normGain);
            }
            else
            {
                _truePeakLimiter!.Process(samples, ref _gainCrossfader);
            }
        }

        return read / _decoder.Channels;
    }

    #endregion

    #region Buffer Info

    public async Task<bool> WaitForBufferAsync(int minSamples, int maxWaitMs, CancellationToken ct)
    {
        if (_disposed || _pcmBuffer.Count >= minSamples) return true;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _warmupTcs, tcs);
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