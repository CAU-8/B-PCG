using System;

namespace Bpcg.Numerics;

/// <summary>
/// 3성분 벡터 연산을 numpy·numba 와 같은 결합 순서로 계산합니다 (docs/csharp_port.md 6장 core ②).
/// </summary>
/// <remarks>
/// 벡터는 행 우선 배열의 한 행(<c>ReadOnlySpan&lt;double&gt;</c> 길이 3)으로 받습니다.
/// FMA 와 식 재배열을 쓰지 않습니다.
/// </remarks>
public static class LinAlg
{
    /// <summary>np.cross 의 한 행: (a1·b2 − a2·b1, a2·b0 − a0·b2, a0·b1 − a1·b0), 곱을 따로 반올림.</summary>
    public static void Cross3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        double c0 = (a[1] * b[2]) - (a[2] * b[1]);
        double c1 = (a[2] * b[0]) - (a[0] * b[2]);
        double c2 = (a[0] * b[1]) - (a[1] * b[0]);
        o[0] = c0;
        o[1] = c1;
        o[2] = c2;
    }

    /// <summary>
    /// numpy 축 축약 <c>np.sum(a * b, axis=-1)</c>·<c>einsum("...k,...k->...")</c> 의 한 행:
    /// +0.0 에서 시작하는 순차 합.
    /// </summary>
    public static double Dot3Np(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0.0;
        s += a[0] * b[0];
        s += a[1] * b[1];
        s += a[2] * b[2];
        return s;
    }

    /// <summary>numba 커널 안의 <c>ax*bx + ay*by + az*bz</c> (0 에서 시작하지 않음).</summary>
    public static double Dot3Numba(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        return ((a[0] * b[0]) + (a[1] * b[1])) + (a[2] * b[2]);
    }

    /// <summary>np.linalg.norm(x, axis=-1) 의 한 행: sqrt(+0.0 에서 시작하는 제곱의 순차 합).</summary>
    public static double Norm3(ReadOnlySpan<double> x)
    {
        double s = 0.0;
        s += x[0] * x[0];
        s += x[1] * x[1];
        s += x[2] * x[2];
        return Math.Sqrt(s);
    }
}
