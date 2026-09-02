namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для сетевых адресов <see cref="Uri"/>.
/// </summary>
internal static class UriExtensions
{
    extension(Uri uri)
    {
        /// <summary>
        /// Возвращает базовый домен (схему и хост) через <see cref="UriPartial.Authority"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="uri"/> равен null.</exception>
        public string GetDomain()
        {
            ArgumentNullException.ThrowIfNull(uri);
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }
}