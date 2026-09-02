using System.Net;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для HTTP-запросов и клиента.
/// </summary>
internal static class HttpExtensions
{
    private sealed class NonDisposableHttpContent(HttpContent content) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await content.CopyToAsync(stream).ConfigureAwait(false);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    extension(HttpRequestMessage request)
    {
        /// <summary>
        /// Создает глубокую копию запроса с сохранением заголовков и недеструктивным контентом.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="request"/> равен null.</exception>
        public HttpRequestMessage Clone()
        {
            ArgumentNullException.ThrowIfNull(request);

            var clonedRequest = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                Content = request.Content is not null
                    ? new NonDisposableHttpContent(request.Content)
                    : null,
            };

            foreach (var (key, value) in request.Headers)
                clonedRequest.Headers.TryAddWithoutValidation(key, value);

            if (request.Content is not null && clonedRequest.Content is not null)
            {
                foreach (var (key, value) in request.Content.Headers)
                    clonedRequest.Content.Headers.TryAddWithoutValidation(key, value);
            }

            return clonedRequest;
        }
    }

    extension(HttpClient http)
    {
        /// <summary>
        /// Выполняет HEAD-запрос с чтением только заголовков ответа.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="http"/> или <paramref name="requestUri"/> равны null.</exception>
        public async ValueTask<HttpResponseMessage> HeadAsync(
            string requestUri,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(requestUri);

            using var request = new HttpRequestMessage(HttpMethod.Head, requestUri);

            return await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ).ConfigureAwait(false);
        }
    }
}