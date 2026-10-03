using System;

namespace Bpcg.Numerics;

/// <summary>
/// numpy·Python 스칼라 연산 가운데 .NET 기본 함수와 의미가 다른 것들 (docs/csharp_port.md 5장).
/// </summary>
public static class NpMath
{
    /// <summary>numpy np.nan (0x7FF8000000000000). .NET double.NaN 은 부호 비트가 다를 수 있어 파일로 나가는 NaN 은 이것을 씁니다.</summary>
    public static readonly double NaN = BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL);

    /// <summary>
    /// np.clip(x, lo, hi) (스칼라 경계): NaN 은 그대로, −0.0 도 그대로 둡니다. Math.Clamp 는 lo &gt; hi 에서 예외를 내므로 쓰지 않습니다.
    /// </summary>
    public static double Clip(double x, double lo, double hi)
    {
        if (double.IsNaN(x))
        {
            return x;
        }
        if (x < lo)
        {
            return lo;
        }
        return x > hi ? hi : x;
    }

    /// <summary>np.nan_to_num: NaN → 0, +inf → double.MaxValue, −inf → double.MinValue.</summary>
    public static double NanToNum(double x)
    {
        if (double.IsNaN(x))
        {
            return 0.0;
        }
        if (double.IsPositiveInfinity(x))
        {
            return double.MaxValue;
        }
        return double.IsNegativeInfinity(x) ? double.MinValue : x;
    }

    /// <summary>np.spacing(x) (x ≥ 0): x 에서 다음 큰 double 까지의 거리.</summary>
    public static double Spacing(double x) => Math.BitIncrement(x) - x;

    /// <summary>
    /// Python math.radians(x) = x·(π/180). .NET double.DegreesToRadians 는 (x·π)/180 이라 비트가 다를 수 있습니다.
    /// </summary>
    public static double Radians(double x) => x * (Math.PI / 180.0);

    /// <summary>Python max(a, b): b &gt; a 일 때만 b (같거나 NaN 이면 a).</summary>
    public static double PyMax(double a, double b) => b > a ? b : a;

    /// <summary>Python min(a, b): b &lt; a 일 때만 b.</summary>
    public static double PyMin(double a, double b) => b < a ? b : a;

    /// <summary>
    /// numpy 배열 거듭제곱 x ** e (float64): 지수가 2·0.5·−1·1·0 이면 numpy 가 제곱·sqrt·역수로 바로 계산하므로 같게 하고,
    /// 나머지는 libm pow(Math.Pow) 입니다. Python 스칼라 ** 와 numba 커널의 ** 는 Math.Pow 를 그대로 씁니다.
    /// </summary>
    public static double Power(double x, double e) => e switch
    {
        2.0 => x * x,
        0.5 => Math.Sqrt(x),
        -1.0 => 1.0 / x,
        1.0 => x,
        0.0 => 1.0,
        _ => Math.Pow(x, e),
    };
}
