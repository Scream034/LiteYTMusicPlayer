using System.Runtime.CompilerServices;
using System.Text;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для строк.
/// </summary>
internal static class StringExtensions
{
    extension(string? s)
    {
        /// <summary>
        /// Усекает строку до длины <paramref name="len"/> с добавлением многоточия.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Truncate(int len = 20)
        {
            if (s is null) return "null";
            return s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
        }
    }

    extension(string str)
    {
        /// <summary>
        /// Возвращает <see langword="null"/>, если строка пуста или состоит только из пробелов.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? NullIfWhiteSpace() =>
            !string.IsNullOrWhiteSpace(str) ? str : null;

        /// <summary>
        /// Возвращает подстроку до первого вхождения <paramref name="sub"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string SubstringUntil(string sub, StringComparison comparison = StringComparison.Ordinal)
        {
            var index = str.IndexOf(sub, comparison);
            return index < 0 ? str : str[..index];
        }

        /// <summary>
        /// Возвращает подстроку после первого вхождения <paramref name="sub"/> или пустую строку.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string SubstringAfter(string sub, StringComparison comparison = StringComparison.Ordinal)
        {
            var index = str.IndexOf(sub, comparison);
            return index < 0
                ? string.Empty
                : str[(index + sub.Length)..];
        }

        /// <summary>
        /// Удаляет все нецифровые символы из строки.
        /// </summary>
        public string StripNonDigit()
        {
            var allDigits = true;
            foreach (var c in str)
            {
                if (!char.IsDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }

            return allDigits ? str : str.StripNonDigitOptimized();
        }

        /// <summary>
        /// Удаляет нецифровые символы с выделением буфера под длину строки.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string StripNonDigitOptimized()
        {
            var builder = new StringBuilder(str.Length);
            foreach (var c in str)
            {
                if (char.IsDigit(c))
                    builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>
        /// Разворачивает строку без аллокации массивов.
        /// </summary>
        public string Reverse()
        {
            return string.Create(str.Length, str, static (span, state) =>
            {
                var stateSpan = state.AsSpan();
                for (var i = 0; i < stateSpan.Length; i++)
                {
                    span[i] = stateSpan[stateSpan.Length - 1 - i];
                }
            });
        }

        /// <summary>
        /// Меняет местами символы по индексам <paramref name="firstCharIndex"/> и <paramref name="secondCharIndex"/>.
        /// </summary>
        public string SwapChars(int firstCharIndex, int secondCharIndex)
        {
            return string.Create(str.Length, (str, firstCharIndex, secondCharIndex), static (span, state) =>
            {
                state.str.AsSpan().CopyTo(span);
                (span[state.firstCharIndex], span[state.secondCharIndex]) =
                    (span[state.secondCharIndex], span[state.firstCharIndex]);
            });
        }
    }
}