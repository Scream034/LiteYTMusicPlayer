using System.Buffers;
using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Bridge;

namespace LMP.Core.Youtube.Search;

internal class SearchController(HttpClient http)
{
    // Используем FrozenDictionary для O(1) маппинг фильтров
    private static readonly FrozenDictionary<SearchFilter, string> MusicFilterParams = new Dictionary<SearchFilter, string>
    {
        [SearchFilter.Music] = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.MusicSong] = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D",
        [SearchFilter.MusicVideo] = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D",
        [SearchFilter.MusicAlbum] = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D",
        [SearchFilter.MusicArtist] = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D",
        [SearchFilter.MusicPlaylist] = "EgeKAQQoAEABagoQAxAEEAoQCRAF",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<SearchFilter, string> WebFilterParams = new Dictionary<SearchFilter, string>
    {
        [SearchFilter.Video] = "EgIQAQ%3D%3D",
        [SearchFilter.Playlist] = "EgIQAw%3D%3D",
        [SearchFilter.Channel] = "EgIQAg%3D%3D",
    }.ToFrozenDictionary();

    private static readonly FrozenSet<SearchFilter> MusicFilters = new[]
    {
        SearchFilter.Music, SearchFilter.MusicSong, SearchFilter.MusicVideo,
        SearchFilter.MusicAlbum, SearchFilter.MusicArtist, SearchFilter.MusicPlaylist
    }.ToFrozenSet();

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    // Кэшированные UTF-8 байты
    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8User = "user"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();
    private static readonly byte[] Utf8Web = "WEB"u8.ToArray();
    private static readonly byte[] Utf8Query = "query"u8.ToArray();
    private static readonly byte[] Utf8Continuation = "continuation"u8.ToArray();
    private static readonly byte[] Utf8Params = "params"u8.ToArray();

    public async ValueTask<SearchResponse> GetSearchResponseAsync(
        string searchQuery,
        SearchFilter searchFilter,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        bool isMusicContext = MusicFilters.Contains(searchFilter);

        string? searchParams = continuationToken == null
            ? GetSearchParams(searchFilter, isMusicContext)
            : null;

        var url = isMusicContext
            ? "https://music.youtube.com/youtubei/v1/search?prettyPrint=false"
            : "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Формируем JSON через Utf8JsonWriter с ArrayBufferWriter
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();

            // По спецификации InnerTube: при наличии continuation токена поля query и params запрещены
            if (continuationToken != null)
            {
                writer.WriteString(Utf8Continuation, continuationToken);
            }
            else
            {
                writer.WriteString(Utf8Query, searchQuery);
                if (searchParams != null)
                    writer.WriteString(Utf8Params, searchParams);
            }

            // Inline context — без промежуточного JsonDocument
            writer.WritePropertyName(Utf8Context);
            writer.WriteStartObject();

            writer.WritePropertyName(Utf8Client);
            writer.WriteStartObject();

            if (isMusicContext)
            {
                writer.WriteString(Utf8ClientName, Utf8WebRemix);
                writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
            }
            else
            {
                writer.WriteString(Utf8ClientName, Utf8Web);
                writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.WebClientVersion);
            }

            writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
            writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

            writer.WriteEndObject(); // client

            if (isMusicContext)
            {
                writer.WritePropertyName(Utf8User);
                writer.WriteStartObject();
                writer.WriteEndObject(); // user
            }

            writer.WriteEndObject(); // context
            writer.WriteEndObject(); // root
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        request.Content = content;

        // Читаем response с ResponseHeadersRead для streaming
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Парсим из потока без промежуточной строки
        return await SearchResponse.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken);
    }

    private static string? GetSearchParams(SearchFilter filter, bool isMusicContext)
    {
        if (isMusicContext)
            return MusicFilterParams.GetValueOrDefault(filter);

        return WebFilterParams.GetValueOrDefault(filter);
    }
}