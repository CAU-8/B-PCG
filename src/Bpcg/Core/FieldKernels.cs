using System;
using System.Numerics;

namespace Bpcg.Core;

/// <summary>연속 필드 배열에 재사용하는 element-wise CPU kernel.</summary>
public static class FieldKernels
{
    /// <summary>output[i] = max(max(primary[i], scale * secondary[i]), floor).</summary>
    /// <remarks>유한한 입력에서 scalar 산술 순서를 유지하며, SIMD 지원이 없으면 scalar로 계산합니다.</remarks>
    public static void MaxProductFloor(
        ReadOnlySpan<double> primary, ReadOnlySpan<double> secondary, double scale, double floor,
        Span<double> output, bool useSimd = true)
    {
        if (primary.Length != secondary.Length || primary.Length != output.Length)
        {
            throw new ArgumentException("필드 배열은 모두 같은 길이여야 합니다");
        }

        int i = 0;
        if (useSimd && Vector.IsHardwareAccelerated && primary.Length >= Vector<double>.Count * 4)
        {
            var scaleVector = new Vector<double>(scale);
            var floorVector = new Vector<double>(floor);
            int width = Vector<double>.Count;
            int simdEnd = primary.Length - (primary.Length % width);
            for (; i < simdEnd; i += width)
            {
                Vector<double> a = new(primary.Slice(i, width));
                Vector<double> b = new(secondary.Slice(i, width));
                Vector.Max(Vector.Max(a, b * scaleVector), floorVector).CopyTo(output.Slice(i, width));
            }
        }

        for (; i < primary.Length; i++)
        {
            output[i] = Math.Max(Math.Max(primary[i], scale * secondary[i]), floor);
        }
    }

    /// <summary>병렬 작업 덩어리마다 SIMD를 사용해 독립적인 연속 필드를 계산합니다.</summary>
    public static void MaxProductFloorParallel(
        double[] primary, double[] secondary, double scale, double floor,
        double[] output, int chunkSize = 16_384, bool useSimd = true)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(output);
        if (primary.Length != secondary.Length || primary.Length != output.Length)
        {
            throw new ArgumentException("필드 배열은 모두 같은 길이여야 합니다");
        }
        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        Parallelism.ForChunks(0, primary.Length, chunkSize, (start, end) =>
        {
            MaxProductFloor(primary.AsSpan(start, end - start), secondary.AsSpan(start, end - start),
                scale, floor, output.AsSpan(start, end - start), useSimd);
        });
    }
}
