using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Bpcg.Tests;

/// <summary>
/// 대조 시험의 비교 도우미 (docs/csharp_port.md 7장). 정수·범주는 완전히 같아야 하고, 실수는 비트 단위 비교가
/// 기본입니다(NaN 끼리는 같다고 봄, −0.0 과 +0.0 은 다르다고 봄). 초월 함수를 거친 값만 근거를 단 ulp 허용 오차를 씁니다.
/// </summary>
public static class Compare
{
    /// <summary>실수 배열이 비트 단위로 같아야 합니다 (NaN 은 payload 무관).</summary>
    public static void Bits(double[] expected, double[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: 길이 {expected.Length} ≠ {actual.Length}");
        int bad = 0;
        int first = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!SameBits(expected[i], actual[i]))
            {
                bad++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }
        if (bad > 0)
        {
            Assert.Fail(
                $"{what}: {bad}/{expected.Length} 개가 비트 단위로 다릅니다. 첫 위치 {first}: 기대 {R(expected[first])}, "
                + $"실제 {R(actual[first])}");
        }
    }

    /// <summary>float32 배열 비트 비교.</summary>
    public static void Bits(float[] expected, float[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: 길이 {expected.Length} ≠ {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            bool same = (float.IsNaN(expected[i]) && float.IsNaN(actual[i]))
                || BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]);
            if (!same)
            {
                Assert.Fail($"{what}: 위치 {i} 가 다릅니다: 기대 {expected[i].ToString("R", CultureInfo.InvariantCulture)}, "
                    + $"실제 {actual[i].ToString("R", CultureInfo.InvariantCulture)}");
            }
        }
    }

    /// <summary>스칼라 비트 비교.</summary>
    public static void Bits(double expected, double actual, string what)
    {
        if (!SameBits(expected, actual))
        {
            Assert.Fail($"{what}: 기대 {R(expected)}, 실제 {R(actual)}");
        }
    }

    /// <summary>정수·불리언·글자 배열이 완전히 같아야 합니다.</summary>
    public static void Equal<T>(T[] expected, T[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: 길이 {expected.Length} ≠ {actual.Length}");
        EqualityComparer<T> eq = EqualityComparer<T>.Default;
        int bad = 0;
        int first = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!eq.Equals(expected[i], actual[i]))
            {
                bad++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }
        if (bad > 0)
        {
            Assert.Fail($"{what}: {bad}/{expected.Length} 개가 다릅니다. 첫 위치 {first}: 기대 {expected[first]}, 실제 {actual[first]}");
        }
    }

    /// <summary>
    /// 실수 배열이 ulp 단위로 maxUlps 안에서 같아야 합니다. 초월 함수(libm)를 거친 값을 다른 OS 에서 비교할 때만 쓰고,
    /// 부르는 쪽에서 허용 오차의 근거를 주석으로 남깁니다.
    /// </summary>
    public static void Ulps(double[] expected, double[] actual, long maxUlps, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: 길이 {expected.Length} ≠ {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            if (SameBits(expected[i], actual[i]))
            {
                continue;
            }
            long d = UlpDistance(expected[i], actual[i]);
            if (d > maxUlps)
            {
                Assert.Fail($"{what}: 위치 {i} 가 {d} ulp 다릅니다 (허용 {maxUlps}): 기대 {R(expected[i])}, 실제 {R(actual[i])}");
            }
        }
    }

    /// <summary>
    /// golden 배열과 C# 배열을 원소 형까지 같은지 보고 비교합니다: 실수는 비트 단위, 나머지는 완전히 같음.
    /// </summary>
    public static void Array(Bpcg.IO.NpyArray expected, System.Array actual, string what)
    {
        Type te = expected.Data.GetType();
        Assert.True(te == actual.GetType(), $"{what}: 원소 형 {te.Name} ≠ {actual.GetType().Name}");
        switch (expected.Data)
        {
            case double[] d:
                Bits(d, (double[])actual, what);
                break;
            case float[] f:
                Bits(f, (float[])actual, what);
                break;
            case bool[] b:
                Equal(b, (bool[])actual, what);
                break;
            case byte[] u8:
                Equal(u8, (byte[])actual, what);
                break;
            case sbyte[] i8:
                Equal(i8, (sbyte[])actual, what);
                break;
            case int[] i32:
                Equal(i32, (int[])actual, what);
                break;
            case long[] i64:
                Equal(i64, (long[])actual, what);
                break;
            default:
                Assert.Fail($"{what}: 비교할 수 없는 원소 형 {te.Name}");
                break;
        }
    }

    public static bool SameBits(double a, double b) =>
        (double.IsNaN(a) && double.IsNaN(b)) || BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static long UlpDistance(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            return long.MaxValue;
        }
        long ia = BitConverter.DoubleToInt64Bits(a);
        long ib = BitConverter.DoubleToInt64Bits(b);
        if (ia < 0)
        {
            ia = long.MinValue - ia;
        }
        if (ib < 0)
        {
            ib = long.MinValue - ib;
        }
        return Math.Abs(ia - ib);
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
