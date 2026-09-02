namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Обобщённые методы пайплайн-трансформации значений.
/// </summary>
internal static class GenericExtensions
{
    extension<TIn>(TIn input)
    {
        /// <summary>
        /// Передаёт значение в функцию трансформации.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="transform"/> равен null.</exception>
        public TOut Pipe<TOut>(Func<TIn, TOut> transform)
        {
            ArgumentNullException.ThrowIfNull(transform);
            return transform(input);
        }
    }
}