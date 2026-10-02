using System;

namespace Bpcg.Numerics;

/// <summary>
/// numpy 축약(<c>np.add.reduce</c>)의 덧셈 순서를 그대로 옮긴 합과 평균.
/// </summary>
/// <remarks>
/// numpy 는 1차원 연속 배열의 합을 <c>0.0 + pairwise(x)</c> 로 계산합니다(축약의 항등원 0 에서 시작).
/// pairwise 는 길이 8 미만이면 0 에서 차례로, 128 이하이면 누적기 8개로, 그보다 길면 반(8의 배수)으로
/// 나눠 재귀합니다(numpy loops_utils.h.src). 무작위 배열 수백 가지에서 비트 단위로 같음을 확인했습니다
/// (numpy 2.5.3, docs/csharp_port.md 5장). numba 커널 안의 합은 순차 합이므로 이것을 쓰지 않습니다.
/// </remarks>
public static class NpReduce
{
    private const int BlockSize = 128; // numpy PW_BLOCKSIZE

    /// <summary>np.sum(a) (1차원 연속 float64).</summary>
    public static double Sum(ReadOnlySpan<double> a) => 0.0 + Pairwise(a);

    /// <summary>np.sum(a) (1차원 연속 float32, float32 로 누적).</summary>
    public static float Sum(ReadOnlySpan<float> a) => 0.0f + Pairwise(a);

    /// <summary>np.mean(a) (float64): Sum(a) / n.</summary>
    public static double Mean(ReadOnlySpan<double> a) => Sum(a) / a.Length;

    /// <summary>np.mean(a) (float32): float32 합을 float32 로 나눕니다.</summary>
    public static float Mean(ReadOnlySpan<float> a) => Sum(a) / a.Length;

    private static double Pairwise(ReadOnlySpan<double> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++)
            {
                res += a[i];
            }
            return res;
        }
        if (n <= BlockSize)
        {
            double r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3];
            double r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i = 8;
            int end = n - (n % 8);
            for (; i < end; i += 8)
            {
                r0 += a[i];
                r1 += a[i + 1];
                r2 += a[i + 2];
                r3 += a[i + 3];
                r4 += a[i + 4];
                r5 += a[i + 5];
                r6 += a[i + 6];
                r7 += a[i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++)
            {
                res += a[i];
            }
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return Pairwise(a[..n2]) + Pairwise(a[n2..]);
    }

    private static float Pairwise(ReadOnlySpan<float> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            float res = 0.0f;
            for (int i = 0; i < n; i++)
            {
                res += a[i];
            }
            return res;
        }
        if (n <= BlockSize)
        {
            float r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3];
            float r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i = 8;
            int end = n - (n % 8);
            for (; i < end; i += 8)
            {
                r0 += a[i];
                r1 += a[i + 1];
                r2 += a[i + 2];
                r3 += a[i + 3];
                r4 += a[i + 4];
                r5 += a[i + 5];
                r6 += a[i + 6];
                r7 += a[i + 7];
            }
            float res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++)
            {
                res += a[i];
            }
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return Pairwise(a[..n2]) + Pairwise(a[n2..]);
    }
}
