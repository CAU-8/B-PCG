using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;

namespace Bpcg.Compare;

/// <summary>
/// 부분 나누기 (Galin 2019 3.1.1): 다이아몬드-스퀘어 중점 변위.
/// </summary>
/// <remarks>
/// 한 변 2^k + 1 격자의 네 모서리에서 시작해, 정사각형 가운데(다이아몬드 단계)와 변 가운데(스퀘어 단계)를 이웃 평균 +
/// 무작위 변위로 채웁니다. 단계마다 변위 크기를 2^−H 배로 줄입니다. 처음 나눈 자리의 극값이 눈에 띄는 것이 이 방법의
/// 알려진 흔적입니다(논문 그림 5). 과정 기록은 단계마다 그때까지 채운 격자점을 선형 보간해 보여 줍니다.
/// </remarks>
public static class SubdivisionMethod
{
    private const long Stream = NoiseMethods.StreamBase + 201;

    private static double Rand(long seed, int j, int i, int m) => (2.0 * Hashing.HashUnit(seed, ((long)j * m) + i, Stream)) - 1.0;

    /// <summary>한 단계: 간격 step 격자를 half 간격으로 채웁니다. h: (m·m,).</summary>
    private static void Level(int m, long seed, int step, double amp, double[] h)
    {
        int half = step / 2;
        for (int j = half; j < m; j += step)
        {
            for (int i = half; i < m; i += step)
            {
                double avg = 0.25 * (h[((j - half) * m) + i - half] + h[((j - half) * m) + i + half]
                    + h[((j + half) * m) + i - half] + h[((j + half) * m) + i + half]);
                h[(j * m) + i] = avg + (amp * Rand(seed, j, i, m));
            }
        }
        for (int j = 0; j < m; j += half)
        {
            int start = (j / half) % 2 == 0 ? half : 0;
            for (int i = start; i < m; i += step)
            {
                double s = 0.0;
                int c = 0;
                if (j - half >= 0)
                {
                    s += h[((j - half) * m) + i];
                    c++;
                }
                if (j + half < m)
                {
                    s += h[((j + half) * m) + i];
                    c++;
                }
                if (i - half >= 0)
                {
                    s += h[(j * m) + i - half];
                    c++;
                }
                if (i + half < m)
                {
                    s += h[(j * m) + i + half];
                    c++;
                }
                h[(j * m) + i] = (s / c) + (amp * Rand(seed, j, i, m));
            }
        }
    }

    /// <summary>간격 spacing 격자점만 쓴 값을 왼쪽 위 n×n 으로 선형 보간 (가장자리는 가까운 값).</summary>
    private static double[] Upsample(double[] h, int m, int spacing, int n)
    {
        int l = ((m - 1) / spacing) + 1;
        double[] o = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            double fy = Math.Min((double)j / spacing, l - 1);
            int j0 = Math.Min((int)fy, l - 2);
            double ty = fy - j0;
            for (int i = 0; i < n; i++)
            {
                double fx = Math.Min((double)i / spacing, l - 1);
                int i0 = Math.Min((int)fx, l - 2);
                double tx = fx - i0;
                double a = h[(j0 * spacing * m) + (i0 * spacing)];
                double b = h[(j0 * spacing * m) + ((i0 + 1) * spacing)];
                double c = h[((j0 + 1) * spacing * m) + (i0 * spacing)];
                double d = h[((j0 + 1) * spacing * m) + ((i0 + 1) * spacing)];
                o[(j * n) + i] = (a * (1 - tx) * (1 - ty)) + (b * tx * (1 - ty)) + (c * (1 - tx) * ty) + (d * tx * ty);
            }
        }
        return o;
    }

    public static readonly CompareMethod DiamondSquare = new(
        "diamond_square",
        "다이아몬드-스퀘어",
        "procedural",
        "Galin 2019 3.1.1 (그림 5); Fournier 외 1982",
        "거친 격자를 반씩 나누며 이웃 평균에 무작위 변위를 더하는 중점 변위",
        [new Param("h", 0.9, "거칠기 지수 H (클수록 매끈함, 단계마다 변위 2^−H 배)", Lo: 0.0, Hi: 2.0)],
        (grid, seed, p, ctx) =>
        {
            int k = (int)Math.Ceiling(Math.Log2(grid.N - 1));
            int m = (1 << k) + 1;
            double[] h = new double[m * m];
            h[0] = Rand(seed, 0, 0, m);
            h[m - 1] = Rand(seed, 0, m - 1, m);
            h[(m - 1) * m] = Rand(seed, m - 1, 0, m);
            h[(m * m) - 1] = Rand(seed, m - 1, m - 1, m);
            double scale = Math.Pow(2.0, -p.F("h"));
            double amp = 1.0;
            var partial = new List<(int Level, double[] Z, double Amp)>();
            int level = 0;
            for (int step = m - 1; step > 1; step /= 2)
            {
                Level(m, seed, step, amp, h);
                level++;
                if (ctx.Recording && step / 2 > 1)
                {
                    using (ctx.Extra())
                    {
                        partial.Add((level, Upsample(h, m, step / 2, grid.N), amp));
                    }
                }
                amp *= scale;
            }
            double[] z = new double[grid.Cells];
            for (int j = 0; j < grid.N; j++)
            {
                Array.Copy(h, j * m, z, j * grid.N, grid.N);
            }
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    (double lo, double hi) = CompareGrid.MinMax(z);
                    double f = grid.ReliefM / Math.Max(hi - lo, 1e-12);
                    foreach ((int lv, double[] arr, double a) in partial)
                    {
                        ctx.Snapshot($"{lv}단계", Array.ConvertAll(arr, v => (v - lo) * f), scale: "final", unit: "m",
                            note: $"{lv}번 나눈 뒤 채운 격자점(간격 {1 << (k - lv)}칸)을 이어 그린 지형. 이 단계 변위 크기 {a:G3}.");
                    }
                    ctx.Snapshot("마지막 단계", Array.ConvertAll(z, v => (v - lo) * f), scale: "final", unit: "m", note: "모든 칸을 채운 결과입니다.");
                    ctx.Trace("amp", Enumerable.Range(1, k).Select(i => (double)i), Enumerable.Range(0, k).Select(i => Math.Pow(scale, i)),
                        label: "단계별 변위 크기", xlabel: "단계");
                }
            }
            var output = new MethodOutput(CompareGrid.NormalizeRelief(z, grid.ReliefM));
            output.Info["side"] = (long)m;
            return output;
        });
}
