using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Статические SIMD-ускоренные операции над <see cref="Span{T}"/> PCM-сэмплов.
/// </summary>
/// <remarks>
/// Заменяет <c>System.Numerics.Tensors.TensorPrimitives</c> без внешней зависимости.
/// Векторизация реализована через <see cref="Vector{T}"/> (SSE / AVX / NEON),
/// который входит в shared framework начиная с .NET Core 1.0.
/// </remarks>
internal static class SpanMathHelper
{
    /// <summary>
    /// Умножает каждый элемент <paramref name="samples"/> на константный
    /// <paramref name="gain"/> in-place.
    /// </summary>
    /// <remarks>
    /// Hot path: обрабатывает вектор из <see cref="Vector{T}.Count"/> float за итерацию,
    /// хвост (&lt; Count) — скалярно.
    /// Прямая перезапись через <c>ref var vRef</c> исключает промежуточные копии.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyByConstant(Span<float> samples, float gain)
    {
        ref float ptr = ref MemoryMarshal.GetReference(samples);
        int len = samples.Length;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int lastBlock = len - Vector<float>.Count;

            for (; i <= lastBlock; i += Vector<float>.Count)
            {
                ref var vRef = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref ptr, i));
                vRef *= vGain;
            }
        }

        for (; i < len; i++)
            Unsafe.Add(ref ptr, i) *= gain;
    }

    /// <summary>
    /// Умножает каждый элемент <paramref name="samples"/> на плавно меняющийся коэффициент
    /// от <paramref name="crossfader"/> in-place.
    /// </summary>
    /// <remarks>
    /// IIR-зависимость между сэмплами исключает SIMD — каждый gain вычисляется
    /// на основе предыдущего. Bounds elision через <see cref="Unsafe.Add{T}(ref T, int)"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyByCrossfade(Span<float> samples, ref GainCrossfader crossfader)
    {
        ref float ptr = ref MemoryMarshal.GetReference(samples);
        int len = samples.Length;

        for (int i = 0; i < len; i++)
            Unsafe.Add(ref ptr, i) *= crossfader.Advance();
    }
}