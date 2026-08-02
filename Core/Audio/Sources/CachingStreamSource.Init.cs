using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- Section: InitializeAsync ---

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            Log.Info($"[CachingSource] Initialize: track={_trackId}, cacheKey={_cacheKey}, " +
                     $"format={_format}, codec={Codec}, bitrate={_bitrate}kbps, " +
                     $"contentLength={_contentLength}, " +
                     $"hasInitialUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

            _cacheManager.AcquireLease(_cacheKey);
            _leaseAcquired = true;

            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _currentUrl, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format), _bitrate,
                alignmentBytes: _requestAlignmentBytes);

            Log.Debug($"[CachingSource] Cache entry: downloaded={_cacheEntry.DownloadedBytes}, " +
                      $"total={_cacheEntry.TotalSize}, complete={_cacheEntry.IsComplete}, " +
                      $"alignment={_cacheEntry.AlignmentBytes}");

            if (_cacheEntry.DownloadedBytes > 0)
            {
                _requestAlignmentBytes = Math.Max(4096, _cacheEntry.AlignmentBytes);
                Log.Info($"[CachingSource] Resuming: {_cacheEntry.DownloadedBytes}/{_cacheEntry.TotalSize} bytes");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            int initialBytes = Math.Min(
                _config.InitialPrebufferBytes,
                (int)Math.Min(_contentLength, int.MaxValue));

            bool hasLocalBootstrap = HasLocalInitialBootstrapData(initialBytes);

            if (!hasLocalBootstrap)
            {
                if (string.IsNullOrWhiteSpace(_currentUrl))
                {
                    bool urlReady = await EnsureUrlAvailableAsync(_lifetimeCts.Token)
                        .ConfigureAwait(false);
                    if (!urlReady)
                        throw new InvalidOperationException(
                            "Failed to acquire continuation URL for source initialization");
                }

                await EnsureRangeAsync(0, initialBytes, _lifetimeCts.Token, isCritical: true)
                    .ConfigureAwait(false);
            }
            else
            {
                Log.Debug($"[CachingSource] Local bootstrap prefix is sufficient: {initialBytes} bytes");
            }

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;
            _initialized = true;

            Log.Info($"[CachingSource] Parser ready: track={_trackId}, codec={Codec}, " +
                     $"sampleRate={SampleRate}, channels={Channels}, duration={DurationMs}ms");

            // Startup Prefetch: заливаем warmup-буфер параллельно с decoder init.
            FireStartupPrefetchIfNeeded(initialBytes);

            _preloadTask = Task.Run(
                () => PreloadLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, " +
                     $"cached={_cacheEntry.DownloadProgress:F0}%");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    // --- Section: Startup Prefetch ---

    /// <summary>
    /// Запускает немедленный fire-and-forget prefetch после успешного parser init.
    /// <para>
    /// Перекрывает мёртвое время между завершением <see cref="InitializeAsync"/>
    /// и первой итерацией preload loop (<see cref="StreamingConfig.PreloadIntervalMs"/>).
    /// Без этого на канале с TTFB 1–2.5 с warmup ждёт 500 мс + N round-trips,
    /// задерживая playback на 1–1.5 с.
    /// </para>
    /// </summary>
    /// <param name="alreadyFetchedBytes">
    /// Объём данных, уже полученных initial fetch. Prefetch начинается встык после них.
    /// </param>
    private void FireStartupPrefetchIfNeeded(int alreadyFetchedBytes)
    {
        if (_cacheEntry is { IsComplete: true })
            return;

        long prefetchStart = alreadyFetchedBytes;
        long remaining = _contentLength - prefetchStart;
        if (remaining <= 0)
            return;

        int prefetchLength = (int)Math.Min(_config.StartupPrefetchBytes, remaining);
        if (prefetchLength <= 0)
            return;

        Log.Debug(
            $"[CachingSource] Startup prefetch: {prefetchLength / 1024}KB " +
            $"at offset {prefetchStart} (initial={alreadyFetchedBytes / 1024}KB)");

        _ = SafeStartupPrefetchAsync(prefetchStart, prefetchLength);
    }

    /// <summary>
    /// Best-effort фоновый prefetch для заполнения warmup-буфера.
    /// Ошибки не прерывают инициализацию — preload loop подхватит недокачанное.
    /// </summary>
    private async Task SafeStartupPrefetchAsync(long start, int length)
    {
        try
        {
            var token = CurrentDownloadToken;
            await EnsureRangeAsync(start, length, token, isCritical: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Startup prefetch failed (non-fatal): {ex.Message}");
        }
    }

    // --- Section: Bootstrap Check ---

    /// <summary>
    /// Проверяет, достаточно ли локально доступных данных от начала файла
    /// для безопасного bootstrap-старта parser/decoder без сетевого запроса.
    /// </summary>
    /// <param name="initialBytes">Минимальный стартовый префикс в байтах.</param>
    /// <returns>
    /// <c>true</c>, если contiguous local prefix от позиции 0 уже достаточен;
    /// иначе <c>false</c>.
    /// </returns>
    private bool HasLocalInitialBootstrapData(int initialBytes)
    {
        if (_cacheEntry == null || initialBytes <= 0)
            return false;

        long contiguous = _cacheEntry.GetContiguousDownloadedBytesFrom(0);
        return contiguous >= initialBytes;
    }

    // --- Section: Parser Factory ---

    /// <summary>Выбирает парсер контейнера на основе формата трека.</summary>
    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };
}