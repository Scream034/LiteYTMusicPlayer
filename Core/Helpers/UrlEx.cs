using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Утилиты для работы с URL. Все операции сохраняют исходный порядок параметров
/// и НЕ перекодируют значения, которые не были изменены.
/// </summary>
internal static class UrlEx
{
    //  SetQueryParameter — IN-PLACE замена с сохранением порядка

    /// <summary>
    /// Устанавливает значение query-параметра. Если параметр уже существует,
    /// заменяет его значение НА МЕСТЕ, сохраняя порядок всех остальных параметров.
    /// Если параметра нет — добавляет в конец.
    /// Значение кодируется через <see cref="Uri.EscapeDataString"/>,
    /// но остальные параметры остаются нетронутыми (zero re-encoding).
    /// </summary>
    public static string SetQueryParameter(string url, string key, string value)
    {
        var urlSpan = url.AsSpan();

        // Ищем позицию существующего параметра
        var (keyStart, valueStart, valueEnd) = FindParameterBounds(urlSpan, key);

        // Кодируем ТОЛЬКО новое значение
        var encodedValue = Uri.EscapeDataString(value);

        if (keyStart >= 0)
        {
            // IN-PLACE REPLACEMENT
            // url = [prefix][old_value][suffix]
            //        ^0..valueStart    ^valueEnd..
            // Заменяем old_value на encodedValue, всё остальное — побитово копируем.

            int newLength = valueStart + encodedValue.Length + (url.Length - valueEnd);

            return string.Create(newLength, (url, valueStart, valueEnd, encodedValue),
                static (span, state) =>
            {
                var (originalUrl, valStart, valEnd, newVal) = state;
                var src = originalUrl.AsSpan();

                // Prefix: всё до старого значения
                src[..valStart].CopyTo(span);
                int pos = valStart;

                // New value
                newVal.AsSpan().CopyTo(span[pos..]);
                pos += newVal.Length;

                // Suffix: всё после старого значения
                src[valEnd..].CopyTo(span[pos..]);
            });
        }

        // APPEND — параметра нет, добавляем в конец
        var separator = urlSpan.Contains('?') ? '&' : '?';
        var encodedKey = Uri.EscapeDataString(key);

        int appendLength = url.Length + 1 + encodedKey.Length + 1 + encodedValue.Length;

        return string.Create(appendLength, (url, separator, encodedKey, encodedValue),
            static (span, state) =>
        {
            var (originalUrl, sep, eKey, eVal) = state;
            int pos = 0;

            originalUrl.AsSpan().CopyTo(span);
            pos += originalUrl.Length;

            span[pos++] = sep;

            eKey.AsSpan().CopyTo(span[pos..]);
            pos += eKey.Length;

            span[pos++] = '=';

            eVal.AsSpan().CopyTo(span[pos..]);
        });
    }

    /// <summary>
    /// Удаляет query-параметр из URL. Не трогает и не перекодирует остальные параметры.
    /// </summary>
    public static string RemoveQueryParameter(string url, string key)
    {
        var urlSpan = url.AsSpan();
        var (keyStart, _, valueEnd) = FindParameterBounds(urlSpan, key);

        if (keyStart < 0)
            return url; // Параметра нет — возвращаем как есть

        // Определяем полный диапазон удаления (включая разделитель '&' или '?')
        int removeStart;
        int removeEnd = valueEnd;

        if (keyStart > 0 && urlSpan[keyStart - 1] == '&')
        {
            // Параметр НЕ первый — удаляем вместе с предшествующим '&'
            removeStart = keyStart - 1;
        }
        else if (keyStart > 0 && urlSpan[keyStart - 1] == '?')
        {
            // Параметр первый после '?'
            if (removeEnd < urlSpan.Length && urlSpan[removeEnd] == '&')
            {
                // Есть ещё параметры — удаляем '&' после
                removeStart = keyStart;
                removeEnd++;
            }
            else
            {
                // Единственный параметр — удаляем вместе с '?'
                removeStart = keyStart - 1;
            }
        }
        else
        {
            // Параметр в начале строки (signatureCipher без '?')
            removeStart = keyStart;
            if (removeEnd < urlSpan.Length && urlSpan[removeEnd] == '&')
                removeEnd++;
        }

        int newLength = removeStart + (url.Length - removeEnd);
        if (newLength <= 0)
            return url[..Math.Max(0, removeStart)];

        return string.Create(newLength, (url, removeStart, removeEnd),
            static (span, state) =>
        {
            var (originalUrl, rStart, rEnd) = state;
            var src = originalUrl.AsSpan();

            src[..rStart].CopyTo(span);
            src[rEnd..].CopyTo(span[rStart..]);
        });
    }

    //  Query parameter reading — zero-alloc fast path

    /// <summary>
    /// Возвращает raw (не декодированное) значение параметра, или null если не найден.
    /// Для большинства случаев декодирование не нужно.
    /// </summary>
    public static string? TryGetQueryParameterValue(string url, string key)
    {
        var urlSpan = url.AsSpan();
        var (keyStart, valueStart, valueEnd) = FindParameterBounds(urlSpan, key);

        if (keyStart < 0)
            return null;

        var rawValue = urlSpan[valueStart..valueEnd];

        // Если значение содержит %XX — декодируем, иначе возвращаем как есть
        if (rawValue.Contains('%'))
            return Uri.UnescapeDataString(rawValue.ToString());

        return rawValue.ToString();
    }

    /// <summary>
    /// Извлекает время истечения срока действия URL из параметра &amp;expire= (UTC).
    /// </summary>
    /// <param name="url">URL для проверки.</param>
    /// <param name="expireUtc">Распарсенное время в UTC.</param>
    /// <returns><c>true</c>, если параметр найден и корректно распарсен; иначе <c>false</c>.</returns>
    public static bool TryGetExpireUtc(string url, out DateTime expireUtc)
    {
        var expireStr = TryGetQueryParameterValue(url, "expire");
        if (expireStr is not null && long.TryParse(expireStr, out var unixSeconds))
        {
            expireUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            return true;
        }

        expireUtc = DateTime.MaxValue;
        return false;
    }

    /// <summary>
    /// Проверяет, истёк ли срок действия URL или истекает ли он в ближайшее время.
    /// </summary>
    /// <param name="url">URL для проверки.</param>
    /// <param name="safetyMargin">Запас времени до фактического истечения (по умолчанию 5 минут).</param>
    /// <returns><c>true</c>, если URL протух или скоро протухнет; <c>false</c> если ссылка ещё валидна или expire отсутствует.</returns>
    public static bool IsUrlExpiredOrExpiringSoon(string url, TimeSpan? safetyMargin = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (!TryGetExpireUtc(url, out var expireUtc))
            return false;

        var margin = safetyMargin ?? TimeSpan.FromMinutes(5);
        return DateTime.UtcNow.Add(margin) >= expireUtc;
    }

    public static bool ContainsQueryParameter(string url, string key)
    {
        var (keyStart, _, _) = FindParameterBounds(url.AsSpan(), key);
        return keyStart >= 0;
    }

    public static IReadOnlyDictionary<string, string> GetQueryParameters(string url)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var urlSpan = url.AsSpan();

        int queryStart = urlSpan.IndexOf('?');
        int pos = queryStart >= 0 ? queryStart + 1 : 0;

        while (pos < urlSpan.Length)
        {
            // Находим конец текущего параметра
            int ampPos = urlSpan[pos..].IndexOf('&');
            int segEnd = ampPos >= 0 ? pos + ampPos : urlSpan.Length;

            if (segEnd > pos)
            {
                var segment = urlSpan[pos..segEnd];
                int eqPos = segment.IndexOf('=');

                string paramKey;
                string paramValue;

                if (eqPos >= 0)
                {
                    var rawKey = segment[..eqPos];
                    var rawVal = segment[(eqPos + 1)..];

                    paramKey = rawKey.Contains('%')
                        ? Uri.UnescapeDataString(rawKey.ToString())
                        : rawKey.ToString();
                    paramValue = rawVal.Contains('%')
                        ? Uri.UnescapeDataString(rawVal.ToString())
                        : rawVal.ToString();
                }
                else
                {
                    paramKey = segment.Contains('%')
                        ? Uri.UnescapeDataString(segment.ToString())
                        : segment.ToString();
                    paramValue = "";
                }

                if (paramKey.Length > 0)
                    dict[paramKey] = paramValue;
            }

            pos = segEnd + 1;
        }

        return dict;
    }

    //  CORE: FindParameterBounds — span-based, zero-allocation

    /// <summary>
    /// Находит точные границы параметра в URL.
    /// Возвращает (keyStart, valueStart, valueEnd):
    ///   keyStart  — индекс первого символа ключа
    ///   valueStart — индекс первого символа значения (после '=')
    ///   valueEnd   — индекс символа ПОСЛЕ последнего символа значения ('&' или конец строки)
    /// Если параметр не найден, keyStart = -1.
    /// 
    /// Поиск по RAW ключу (без декодирования), т.к. YouTube ключи всегда plain ASCII.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int keyStart, int valueStart, int valueEnd) FindParameterBounds(
        ReadOnlySpan<char> url, string key)
    {
        var keySpan = key.AsSpan();
        int queryStart = url.IndexOf('?');
        int searchPos = queryStart >= 0 ? queryStart + 1 : 0;

        while (searchPos < url.Length)
        {
            // Находим конец текущего сегмента
            int ampPos = url[searchPos..].IndexOf('&');
            int segmentEnd = ampPos >= 0 ? searchPos + ampPos : url.Length;

            // Ищем '=' в текущем сегменте
            var segment = url[searchPos..segmentEnd];
            int eqPos = segment.IndexOf('=');

            if (eqPos >= 0)
            {
                var paramKey = segment[..eqPos];

                // Точное сравнение ключа (case-sensitive, как YouTube и требует)
                if (paramKey.SequenceEqual(keySpan))
                {
                    int keyStart = searchPos;
                    int valueStart = searchPos + eqPos + 1;
                    int valueEnd = segmentEnd;
                    return (keyStart, valueStart, valueEnd);
                }
            }
            else
            {
                // Параметр без значения (key без =)
                if (segment.SequenceEqual(keySpan))
                {
                    return (searchPos, segmentEnd, segmentEnd);
                }
            }

            searchPos = segmentEnd + 1;
        }

        return (-1, -1, -1);
    }
}