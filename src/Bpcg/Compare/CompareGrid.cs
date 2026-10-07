using System;
using System.Collections.Generic;

namespace Bpcg.Compare;

/// <summary>
/// 비교 격자: 모든 방법이 같은 n×n 평면 격자에 고도를 냅니다 (docs/compare.md 1장).
/// </summary>
/// <remarks>
/// 격자 약속은 평면 그래프(<see cref="Core.Graph.FlatGraph"/>)와 같습니다. 배열은 (n, n) 행 우선, 0번 행이 북쪽 끝이고
/// 칸 (j, i) 의 중심은 동쪽 (i + 0.5)·dx, 남쪽 (j + 0.5)·dx [m] 입니다.
/// </remarks>
public sealed record CompareGrid(int N = 256, double Dx = 50.0, double ReliefM = 1500.0)
{
    /// <summary>한 변 칸 수 상한 (메모리·시간).</summary>
    public const int MaxSide = 2049;

    public int Cells => N * N;

    /// <summary>한 변 길이 [m].</summary>
    public double LengthM => N * Dx;

    /// <summary>값을 검사합니다. 틀리면 ArgumentException.</summary>
    public CompareGrid Validated()
    {
        if (N < 16 || N > MaxSide)
        {
            throw new ArgumentException($"grid.n 은 16 ~ {MaxSide} 이어야 합니다: {N}");
        }
        if (!(double.IsFinite(Dx) && Dx > 0.0))
        {
            throw new ArgumentException($"grid.dx 는 0 보다 큰 유한한 값이어야 합니다: {Dx}");
        }
        if (!(double.IsFinite(ReliefM) && ReliefM > 0.0))
        {
            throw new ArgumentException($"grid.relief_m 은 0 보다 큰 유한한 값이어야 합니다: {ReliefM}");
        }
        return this;
    }

    /// <summary>칸 중심 x (동쪽) [m], (n·n,).</summary>
    public double[] X()
    {
        double[] x = new double[Cells];
        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
            {
                x[(j * N) + i] = (i + 0.5) * Dx;
            }
        }
        return x;
    }

    /// <summary>칸 중심 y (북쪽 가장자리에서 남쪽으로) [m], (n·n,).</summary>
    public double[] Y()
    {
        double[] y = new double[Cells];
        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
            {
                y[(j * N) + i] = (j + 0.5) * Dx;
            }
        }
        return y;
    }

    public OrderedDictionary<string, object?> AsDict() => new()
    {
        ["n"] = (long)N,
        ["dx"] = Dx,
        ["relief_m"] = ReliefM,
    };

    /// <summary>TOML·JSON 의 grid 절 → 격자. 모르는 키는 ArgumentException.</summary>
    public static CompareGrid FromDict(IReadOnlyDictionary<string, object?>? d)
    {
        var g = new CompareGrid();
        if (d is null)
        {
            return g;
        }
        foreach ((string key, object? value) in d)
        {
            g = key switch
            {
                "n" => g with { N = (int)ParamValues.ToLong(value, "grid.n") },
                "dx" => g with { Dx = ParamValues.ToDouble(value, "grid.dx") },
                "relief_m" => g with { ReliefM = ParamValues.ToDouble(value, "grid.relief_m") },
                _ => throw new ArgumentException($"grid 에 모르는 키가 있습니다: {key}"),
            };
        }
        return g.Validated();
    }

    /// <summary>고도를 [0, reliefM] 으로 늘이거나 줄입니다. 평평하면 0 으로 채웁니다.</summary>
    public static double[] NormalizeRelief(double[] z, double reliefM)
    {
        (double lo, double hi) = MinMax(z);
        double span = hi - lo;
        double[] o = new double[z.Length];
        if (!(span > 0.0) || !double.IsFinite(span))
        {
            return o;
        }
        double f = reliefM / span;
        for (int k = 0; k < z.Length; k++)
        {
            o[k] = (z[k] - lo) * f;
        }
        return o;
    }

    /// <summary>(최솟값, 최댓값). NaN 은 건너뜁니다. 비어 있으면 (0, 0).</summary>
    public static (double Min, double Max) MinMax(double[] a)
    {
        double lo = double.PositiveInfinity;
        double hi = double.NegativeInfinity;
        foreach (double v in a)
        {
            if (double.IsNaN(v))
            {
                continue;
            }
            if (v < lo)
            {
                lo = v;
            }
            if (v > hi)
            {
                hi = v;
            }
        }
        return double.IsInfinity(lo) ? (0.0, 0.0) : (lo, hi);
    }
}
