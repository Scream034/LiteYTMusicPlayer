using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistMutationController(HttpClient http)
{
    private const string ApiUrl = "https://www.youtube.com/youtubei/v1";

    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Request = "request"u8.ToArray();
    private static readonly byte[] Utf8UseSsl = "useSsl"u8.ToArray();
    private static readonly byte[] Utf8Web = "WEB"u8.ToArray();

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    /// <summary>
    /// Minimal WEB context — no hl/gl, with useSsl: false.
    /// </summary>
    private static void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8Web);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8Request);
        writer.WriteStartObject();
        writer.WriteBoolean(Utf8UseSsl, false);
        writer.WriteEndObject(); // request

        writer.WriteEndObject(); // context
    }

    /// <summary>
    /// Создаёт HTTP-контент с использованием ReadOnlyMemoryContent для нулевых аллокаций.
    /// </summary>
    private static ReadOnlyMemoryContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}?prettyPrint=false");
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await Json.ParseAsync(stream, ct);
    }

    private async Task PostFireAndForgetAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}?prettyPrint=false");
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Checks response for STATUS_FAILED and extracts error message from dialogMessages.
    /// </summary>
    private static void CheckStatus(JsonElement root)
    {
        var status = root.GetPropertyOrNull("status")?.GetStringOrNull();
        if (status == null || status.Equals("STATUS_SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            return;

        if (!status.Equals("STATUS_FAILED", StringComparison.OrdinalIgnoreCase))
            return;

        // Extract error message from confirmDialogEndpoint
        string? message = null;
        try
        {
            var actions = root.GetPropertyOrNull("actions");
            if (actions?.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.Value.EnumerateArray())
                {
                    var dialogMessages = action
                        .GetPropertyOrNull("confirmDialogEndpoint")
                        ?.GetPropertyOrNull("content")
                        ?.GetPropertyOrNull("confirmDialogRenderer")
                        ?.GetPropertyOrNull("dialogMessages");

                    if (dialogMessages?.ValueKind != JsonValueKind.Array) continue;

                    var parts = new List<string>(4);
                    foreach (var msg in dialogMessages.Value.EnumerateArray())
                    {
                        var runs = msg.GetPropertyOrNull("runs");
                        if (runs?.ValueKind != JsonValueKind.Array) continue;

                        foreach (var run in runs.Value.EnumerateArray())
                        {
                            var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                            if (!string.IsNullOrEmpty(text))
                                parts.Add(text);
                        }
                    }

                    if (parts.Count > 0)
                    {
                        message = string.Join("\n", parts);
                        break;
                    }
                }
            }
        }
        catch
        {
            // Best effort parsing
        }

        throw new YoutubeExplodeException(
            message ?? $"Playlist operation failed with status: {status}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string SanitizePlaylistId(string playlistId)
    {
        var span = playlistId.AsSpan();
        if (span.StartsWith("VL") && span.Length > 2)
            return playlistId[2..];
        return playlistId;
    }

    #region Public API

    /// <summary>
    /// Creates a playlist with optional batch track addition.
    /// </summary>
    /// <returns>YouTube playlist ID.</returns>
    public async Task<string> CreatePlaylistAsync(
        string title,
        IReadOnlyList<string>? videoIds = null,
        CancellationToken ct = default)
    {
        var root = await PostAsync("playlist/create", writer =>
        {
            writer.WriteString("title", title);
            writer.WriteString("params", "ICE%3D");

            if (videoIds is { Count: > 0 })
            {
                writer.WriteStartArray("videoIds");
                for (int i = 0; i < videoIds.Count; i++)
                    writer.WriteStringValue(videoIds[i]);
                writer.WriteEndArray();
            }
        }, ct);

        return root.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException(
                "Failed to create playlist: no playlistId in response.");
    }

    /// <summary>
    /// Batch adds tracks to a playlist in a single request.
    /// </summary>
    /// <returns>setVideoId for each added track (null if individual track failed).</returns>
    public async Task<List<string?>> AddTracksAsync(
        string playlistId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default)
    {
        if (videoIds.Count == 0) return [];

        var root = await PostAsync("browse/edit_playlist?prettyPrint=false", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
            writer.WriteString("params", "ICE%3D");

            writer.WriteStartArray("actions");
            for (int i = 0; i < videoIds.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_ADD_VIDEO");
                writer.WriteString("addedVideoId", videoIds[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct);

        CheckStatus(root);

        // Parse setVideoIds from response
        var result = new List<string?>(videoIds.Count);
        var editResults = root.GetPropertyOrNull("playlistEditResults");

        if (editResults?.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in editResults.Value.EnumerateArray())
            {
                var setVideoId = item
                    .GetPropertyOrNull("playlistEditVideoAddedResultData")
                    ?.GetPropertyOrNull("setVideoId")
                    ?.GetStringOrNull();
                result.Add(setVideoId);
            }
        }

        // Pad with nulls if response has fewer results than requested
        while (result.Count < videoIds.Count)
            result.Add(null);

        return result;
    }

    /// <summary>
    /// Batch removes tracks from a playlist. Only setVideoId is needed.
    /// </summary>
    public async Task RemoveTracksAsync(
        string playlistId,
        IReadOnlyList<string> setVideoIds,
        CancellationToken ct = default)
    {
        if (setVideoIds.Count == 0) return;

        var root = await PostAsync("browse/edit_playlist?prettyPrint=false", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
            writer.WriteString("params", "ICE%3D");

            writer.WriteStartArray("actions");
            for (int i = 0; i < setVideoIds.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_REMOVE_VIDEO");
                writer.WriteString("setVideoId", setVideoIds[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct);

        CheckStatus(root);
    }

    /// <summary>
    /// Renames a playlist.
    /// </summary>
    public async Task RenamePlaylistAsync(
        string playlistId,
        string newTitle,
        CancellationToken ct = default)
    {
        var root = await PostAsync("browse/edit_playlist?prettyPrint=false", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
            writer.WriteString("params", "ICE%3D");

            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_SET_PLAYLIST_NAME");
            writer.WriteString("playlistName", newTitle);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }, ct);

        CheckStatus(root);
    }

    /// <summary>
    /// Updates playlist description.
    /// </summary>
    public async Task SetPlaylistDescriptionAsync(
        string playlistId,
        string description,
        CancellationToken ct = default)
    {
        var root = await PostAsync("browse/edit_playlist?prettyPrint=false", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
            writer.WriteString("params", "ICE%3D");

            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_SET_PLAYLIST_DESCRIPTION");
            writer.WriteString("playlistDescription", description);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }, ct);

        CheckStatus(root);
    }

    /// <summary>
    /// Deletes a playlist from YouTube account.
    /// </summary>
    public async Task DeletePlaylistAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        await PostFireAndForgetAsync("playlist/delete", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
            writer.WriteString("params", "ICE%3D");
        }, ct);
    }

    /// <summary>
    /// Uploads a custom thumbnail for a playlist via Scotty Upload Protocol.
    /// 
    /// <para><b>3-step process:</b></para>
    /// <list type="number">
    ///   <item>Initiate: POST with X-Goog-Upload-Command: start → get upload_url</item>
    ///   <item>Upload: POST binary data → get scottyEncryptedBlobId (в JSON ответе)</item>
    ///   <item>Apply: edit_playlist with ACTION_SET_CUSTOM_THUMBNAIL</item>
    /// </list>
    /// 
    /// <para><b>Важно:</b> HttpClient уже обрабатывает Cookie и Authorization через YoutubeHttpHandler.</para>
    /// </summary>
    /// <param name="playlistId">YouTube playlist ID.</param>
    /// <param name="imageData">Image bytes (JPEG/PNG, recommended 1280x720 or square).</param>
    /// <param name="mimeType">MIME type: "image/jpeg" or "image/png".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if thumbnail was successfully set.</returns>
    public async Task<bool> UploadPlaylistThumbnailAsync(
        string playlistId,
        byte[] imageData,
        CancellationToken ct = default)
    {
        if (imageData.Length == 0)
            throw new ArgumentException("Image data cannot be empty.", nameof(imageData));

        if (imageData.Length > 20 * 1024 * 1024) // 20MB limit
            throw new ArgumentException("Image too large. Maximum size: 20MB.", nameof(imageData));

        try
        {
            // ═══ STEP 1: Initiate upload session ═══
            var initiateUrl = "https://music.youtube.com/playlist_image_upload/playlist_custom_thumbnail";

            using var initiateRequest = new HttpRequestMessage(HttpMethod.Post, initiateUrl);

            // Scotty-специфичные заголовки
            initiateRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            initiateRequest.Headers.Add("X-Goog-Upload-Command", "start");
            initiateRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", imageData.Length.ToString());

            // Content-Type для пустого тела
            initiateRequest.Content = new ByteArrayContent([]);
            initiateRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "UTF-8"
            };

            using var initiateResponse = await http.SendAsync(initiateRequest, ct);
            initiateResponse.EnsureSuccessStatusCode();

            // Upload URL в заголовке X-Goog-Upload-URL
            if (!initiateResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                throw new InvalidOperationException("No X-Goog-Upload-URL in response headers");

            var uploadUrl = uploadUrls.First();
            Log.Debug($"[Scotty] Got upload URL: {uploadUrl[..Math.Min(100, uploadUrl.Length)]}...");

            // ═══ STEP 2: Upload binary data ═══
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);

            uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");

            uploadRequest.Content = new ByteArrayContent(imageData);
            uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "utf-8"
            };

            using var uploadResponse = await http.SendAsync(uploadRequest, ct);
            uploadResponse.EnsureSuccessStatusCode();

            // Parse JSON response для получения blobId
            using var uploadStream = await uploadResponse.Content.ReadAsStreamAsync(ct);
            var uploadResult = await Json.ParseAsync(uploadStream, ct);

            // Формат ответа: {"encryptedBlobId": "..."}
            var blobId = uploadResult.GetPropertyOrNull("encryptedBlobId")?.GetStringOrNull();
            if (string.IsNullOrEmpty(blobId))
            {
                Log.Error("[Scotty] No encryptedBlobId in upload response");
                return false;
            }

            Log.Debug($"[Scotty] Got blobId: {blobId[..Math.Min(50, blobId.Length)]}...");

            // ═══ STEP 3: Apply thumbnail via edit_playlist API ═══
            var applyRoot = await PostAsync("browse/edit_playlist?prettyPrint=false", writer =>
            {
                writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
                writer.WriteString("params", "ICE%3D");

                writer.WriteStartArray("actions");
                writer.WriteStartObject();

                writer.WriteString("action", "ACTION_SET_CUSTOM_THUMBNAIL");

                writer.WriteStartObject("addedCustomThumbnail");

                writer.WriteStartObject("imageKey");
                writer.WriteString("type", "PLAYLIST_IMAGE_TYPE_CUSTOM_THUMBNAIL");
                writer.WriteString("name", "studio_square_thumbnail");
                writer.WriteEndObject(); // imageKey

                writer.WriteString("playlistScottyEncryptedBlobId", blobId);

                writer.WriteEndObject(); // addedCustomThumbnail

                writer.WriteEndObject(); // action
                writer.WriteEndArray(); // actions
            }, ct);

            CheckStatus(applyRoot);

            Log.Info($"[Scotty] Thumbnail uploaded successfully for playlist {playlistId} ({imageData.Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Scotty] Upload failed for playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    #endregion
}