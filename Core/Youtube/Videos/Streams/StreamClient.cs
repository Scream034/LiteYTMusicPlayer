using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Обеспечивает доступ к медиа-потокам YouTube видео.
/// <para>
/// <b>Кэш-стратегия (3-уровневая):</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Disk manifest cache</b> (<see cref="SessionCacheStore"/>):
///     Полный манифест (все варианты) сохраняется на диск после первого API call.
///     Переживает рестарт приложения. Инвалидируется по HTTP 403/410 при probe.
///   </item>
///   <item>
///     <b>Network resolve</b>: Только если disk cache пуст или probe провалился.
///     Один API call → полный манифест → записывается на диск.
///   </item>
///   <item>
///     <b>CDN connection pre-warming</b>: TCP+TLS prewarm к известным CDN-нодам
///     параллельно с API call.
///   </item>
/// </list>
/// </summary>
public sealed class StreamClient
{
    private readonly VideoController _controller;
    private readonly HttpClient _http;
    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly PlayerContextManager _playerContextManager;
    private readonly Func<bool>? _isAuthenticatedCheck;
    private CipherManifest? _cipherManifest;
    private readonly PoTokenProvider? _poTokenProvider;

    /// <summary>
    /// Создает экземпляр StreamClient.
    /// </summary>
    public StreamClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        PoTokenProvider? poTokenProvider = null)
    {
        _http = http;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _isAuthenticatedCheck = isAuthenticatedCheck;
        _playerContextManager = sigCipherDecryptor.PlayerManager;
        _controller = new VideoController(http, _playerContextManager);
        _poTokenProvider = poTokenProvider;
    }

    /// <summary>
    /// Извлекает signatureTimestamp из кэша <see cref="PlayerContextManager"/>.
    /// Пробрасывает сетевые ошибки и отмены для корректной диагностики.
    /// </summary>
    private async ValueTask<CipherManifest> ResolveCipherManifestAsync(CancellationToken cancellationToken)
    {
        if (_cipherManifest is not null)
            return _cipherManifest;

        try
        {
            var context = await _playerContextManager.GetOrLoadAsync(cancellationToken).ConfigureAwait(false);
            var sts = context.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(context.BaseJs))
                sts = YoutubeAstSolver.ExtractSts(context.BaseJs);

            _cipherManifest = new CipherManifest(sts ?? "");
            return _cipherManifest;
        }
        catch (YoutubeNetworkException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[StreamClient] CipherManifest resolution failed: {ex.Message}");
            _cipherManifest = new CipherManifest("");
            return _cipherManifest;
        }
    }

    /// <summary>
    /// Генерирует аудиопотоки из сырых данных ответа YouTube.
    /// </summary>
    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
      VideoId videoId,
      IEnumerable<IStreamData> streamDatas,
      string? clientName,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool? isNTokenDecryptionRequired = null;

        string? pot = null;
        bool skipPoToken = string.Equals(clientName, "ANDROID_VR", StringComparison.OrdinalIgnoreCase);

        if (_poTokenProvider != null && !skipPoToken)
        {
            try
            {
                pot = await _poTokenProvider
                    .GetContentTokenAsync(videoId.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(pot))
                    Log.Debug($"[StreamClient] PoToken ready ({pot.Length} chars)");
                else
                    Log.Warn("[StreamClient] PoToken unavailable — proceeding without");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[StreamClient] PoToken fetch failed: {ex.Message} — proceeding without");
            }
        }

        foreach (var streamData in streamDatas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itag = streamData.Itag;
            if (itag is null) continue;

            var mimeType = streamData.MimeType;
            if (string.IsNullOrEmpty(mimeType) ||
                !mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                continue;

            var audioCodec = streamData.AudioCodec;
            if (string.IsNullOrWhiteSpace(audioCodec)) continue;

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            Log.Debug($"[StreamClient] itag={itag} raw URL (first 200): " +
                      $"{url[..Math.Min(url.Length, 200)]}");

            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption");
                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature, cancellationToken).ConfigureAwait(false);

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = UrlEx.SetQueryParameter(url, sigParam, decryptedSig);
                    Log.Debug($"[StreamClient] itag={itag} sig decrypted");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue;
                }
            }

            var nToken = UrlEx.TryGetQueryParameterValue(url, "n");
            bool hasEncryptedNToken = false;

            if (!string.IsNullOrEmpty(nToken))
            {
                // Для WEB и WEB_REMIX YouTube ВСЕГДА шифрует n-token.
                // Выполнять сетевой HEAD-запрос ценой 1.5-2 секунды ради получения заведомого HTTP 403 бессмысленно:
                // расшифровка в QuickJS занимает всего 5 мс и выполняется упреждающе.
                if (isNTokenDecryptionRequired == null)
                {
                    bool isKnownEncryptedClient = string.Equals(clientName, "WEB_REMIX", StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(clientName, "WEB", StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(clientName, "TVHTML5_SIMPLY_EMBEDDED_PLAYER", StringComparison.OrdinalIgnoreCase);

                    if (isKnownEncryptedClient)
                    {
                        isNTokenDecryptionRequired = true;
                        Log.Debug($"[StreamClient] itag={itag}: Proactively decrypting n-token for web client '{clientName}' (bypassing 2s HEAD probe)");
                    }
                    else
                    {
                        try
                        {
                            using var headResponse = await _http.HeadAsync(url, cancellationToken).ConfigureAwait(false);
                            isNTokenDecryptionRequired = !headResponse.IsSuccessStatusCode;
                            Log.Debug($"[StreamClient] itag={itag} HEAD: {headResponse.StatusCode}, needsNToken={isNTokenDecryptionRequired}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            isNTokenDecryptionRequired = true;
                            Log.Debug($"[StreamClient] HEAD probe failed ({ex.Message}), fallback to proactive decryption");
                        }
                    }
                }

                if (isNTokenDecryptionRequired == true)
                {
                    try
                    {
                        var decryptedN = await _nTokenDecryptor.DecryptAsync(
                            nToken, contextId: videoId.Value, ct: cancellationToken).ConfigureAwait(false);

                        hasEncryptedNToken = string.Equals(decryptedN, nToken, StringComparison.Ordinal);
                        url = UrlEx.SetQueryParameter(url, "n", decryptedN);
                        Log.Debug($"[StreamClient] itag={itag} n-token: '{nToken}' → '{decryptedN}'");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        hasEncryptedNToken = true;
                        Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                        Log.Warn($"[StreamClient] ⚠ URL will have ENCRYPTED n-token → expect 403!");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            url = UrlEx.RemoveQueryParameter(url, "ump");
            url = UrlEx.RemoveQueryParameter(url, "alr");
            url = UrlEx.RemoveQueryParameter(url, "srfvp");

            url = !string.IsNullOrEmpty(pot)
                ? UrlEx.SetQueryParameter(url, "pot", pot)
                : UrlEx.RemoveQueryParameter(url, "pot");

            Log.Debug($"[StreamClient] itag={itag} FINAL URL ready.");

            var contentLength = streamData.ContentLength ?? 0;
            if (contentLength == 0) continue;

            var container = streamData.Container is { } c ? new Container(c) : (Container?)null;
            if (container is null) continue;

            var bitrate = streamData.Bitrate is { } b ? new Bitrate(b) : (Bitrate?)null;
            if (bitrate is null) continue;

            Language? audioLanguage = null;
            if (!string.IsNullOrWhiteSpace(streamData.AudioLanguageCode))
            {
                audioLanguage = new Language(
                    streamData.AudioLanguageCode,
                    streamData.AudioLanguageName ?? "");
            }

            yield return new AudioOnlyStreamInfo(
                itag.Value,
                url,
                container.Value,
                new FileSize(contentLength),
                bitrate.Value,
                audioCodec,
                audioLanguage,
                streamData.IsAudioLanguageDefault,
                hasEncryptedNToken);
        }
    }

    /// <summary>
    /// Получает манифест потоков для видео.
    /// <para>
    /// <b>Всегда идёт в сеть.</b> Кэширование выполняется вызывающим кодом
    /// (<see cref="SessionCacheStore.RecordManifest"/>).
    /// Это разделение ответственности гарантирует, что StreamClient
    /// не смешивает полные и неполные манифесты.
    /// </para>
    /// </summary>
    /// <param name="videoId">ID видео.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест со всеми доступными аудио-потоками.</returns>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        PlayerResponse playerResponse;
        string? clientName = null;
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;
        VideoUnplayableException? fallbackChainException = null;

        try
        {
            (playerResponse, clientName) = await _controller.GetPlayerResponseWithFallbackAsync(
                videoId, cancellationToken, isAuthenticated: isAuth).ConfigureAwait(false);
        }
        catch (VideoUnplayableException ex)
        {
            fallbackChainException = ex;

            var cipherManifest = await ResolveCipherManifestAsync(cancellationToken).ConfigureAwait(false);
            playerResponse = await _controller.GetPlayerResponseAsync(
                videoId,
                cipherManifest.SignatureTimestamp,
                cancellationToken
            ).ConfigureAwait(false);
        }

        if (!playerResponse.IsPlayable)
        {
            if (fallbackChainException != null)
                throw fallbackChainException;

            throw new VideoUnplayableException(
                $"Video {videoId} is not playable: {playerResponse.PlayabilityError}");
        }

        var streams = new List<IStreamInfo>();
        await foreach (var stream in GetAudioStreamInfosAsync(videoId, playerResponse.Streams, clientName, cancellationToken).ConfigureAwait(false))
        {
            streams.Add(stream);
        }

        if (streams.Count == 0)
            throw new VideoUnplayableException($"No audio streams available for {videoId}");

        return new StreamManifest(streams, playerResponse.PerceptualLoudnessDb);
    }

    /// <summary>
    /// Загружает аудио-поток в файл.
    /// </summary>
    public async ValueTask DownloadAsync(
        IStreamInfo streamInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var destination = File.Create(filePath);
        using var input = new MediaStream(_http, streamInfo);

        await input.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await input.CopyToAsync(destination, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Инвалидирует CipherManifest и signatureTimestamp.
    /// </summary>
    public void InvalidateCipherManifest()
    {
        _cipherManifest = null;
        _controller.InvalidateSignatureTimestamp();
        Log.Debug("[StreamClient] CipherManifest and STS invalidated");
    }
}