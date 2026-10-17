using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;

namespace Bpcg.Compare;

/// <summary>
/// 단층 (Galin 2019 3.1.2): 무작위 직선 단층마다 한쪽은 올리고 다른 쪽은 내립니다.
/// </summary>
/// <remarks>
/// 단층 i 의 변위 f_i(p) = a_i · sign(d) · (1 − g(|d|)) 입니다. d 는 단층선까지의 부호 있는 거리,
/// g(r) = (1 − (r/R)²)² (r &lt; R, 아니면 0) 은 논문의 4차 매끈한 계단 함수라서 단층선 근처 폭 R 안에서 변위가 0 에서 ±a_i 로
/// 부드럽게 바뀝니다. a_i = (i + 1)^−decay 로 뒤의 단층일수록 작습니다. 단층선은 영역보다 조금 넓은 범위의 무작위 점과 방향으로
/// 정하므로 양쪽이 고르게 뽑힙니다.
/// </remarks>
public static class FaultingMethod
{
    private const long Stream = NoiseMethods.StreamBase + 301;
    private static readonly int[] Marks = [1, 3, 10, 30, 100, 300, 1000, 3000];

    /// <summary>단층 (cx, cy, nx, ny) (F·4,): 점은 영역의 −10%~110%, 방향은 0~2π.</summary>
    public static double[] FaultLines(long seed, int nFaults, double lengthM)
    {
        double[] o = new double[nFaults * 4];
        for (int f = 0; f < nFaults; f++)
        {
            double u0 = Hashing.HashUnit(seed, f, Stream);
            double u1 = Hashing.HashUnit(seed, f, Stream + 1);
            double u2 = Hashing.HashUnit(seed, f, Stream + 2);
            double ang = 2.0 * Math.PI * u2;
            o[f * 4] = (-0.1 + (1.2 * u0)) * lengthM;
            o[(f * 4) + 1] = (-0.1 + (1.2 * u1)) * lengthM;
            o[(f * 4) + 2] = Math.Cos(ang);
            o[(f * 4) + 3] = Math.Sin(ang);
        }
        return o;
    }

    /// <summary>단층 f0 ≤ f &lt; f1 의 변위를 o 에 더합니다.</summary>
    private static void AddFaults(double[] x, double[] y, double[] lines, int f0, int f1, double radius, double decay, double[] o) =>
        Parallelism.For(0, x.Length, k =>
        {
            double acc = 0.0;
            for (int f = f0; f < f1; f++)
            {
                double d = ((x[k] - lines[f * 4]) * lines[(f * 4) + 2]) + ((y[k] - lines[(f * 4) + 1]) * lines[(f * 4) + 3]);
                double r = Math.Abs(d);
                double g = 0.0;
                if (r < radius)
                {
                    double t = 1.0 - ((r / radius) * (r / radius));
                    g = t * t;
                }
                double a = Math.Pow(f + 1.0, -decay);
                acc += a * (1.0 - g) * (d >= 0.0 ? 1.0 : -1.0);
            }
            o[k] += acc;
        });

    public static readonly CompareMethod Faulting = new(
        "faulting",
        "단층",
        "procedural",
        "Galin 2019 3.1.2 (그림 6); Mandelbrot 1982, Voss 1991",
        "무작위 직선(단층)을 그을 때마다 한쪽을 올리고 다른 쪽을 내리기를 수백 번 되풀이",
        [
            new Param("faults", 400L, "단층 수", Kind: "int", Lo: 1, Hi: 5000),
            new Param("radius_m", 300.0, "단층선 양쪽에서 높이가 바뀌는 폭 R (0이면 칼로 자른 계단)", Lo: 1.0, Unit: "m"),
            new Param("decay", 0.5, "뒤에 그은 단층일수록 덜 솟게 하는 지수 (i번째 단층 높이 a_i = (i+1)^−decay)", Lo: 0.0, Hi: 2.0),
        ],
        (grid, seed, p, ctx) =>
        {
            double[] x = grid.X();
            double[] y = grid.Y();
            int nf = p.I("faults");
            double[] lines = FaultLines(seed, nf, grid.LengthM);
            double[] o = new double[grid.Cells];
            // 기록 여부와 상관없이 같은 구간으로 나눠 더해야 덧셈 순서가 같아 결과가 비트까지 같습니다.
            List<int> marks = [.. Marks.Where(c => c < nf), nf];
            var partial = new List<(int Count, double[] Z)>();
            int done = 0;
            foreach (int c in marks)
            {
                AddFaults(x, y, lines, done, c, p.F("radius_m"), p.F("decay"), o);
                done = c;
                if (ctx.Recording && c < nf)
                {
                    using (ctx.Extra())
                    {
                        partial.Add((c, (double[])o.Clone()));
                    }
                }
            }
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    (double lo, double hi) = CompareGrid.MinMax(o);
                    double f = grid.ReliefM / Math.Max(hi - lo, 1e-12);
                    partial.Add((nf, o));
                    foreach ((int c, double[] arr) in partial)
                    {
                        ctx.Snapshot($"단층 {c}개", Array.ConvertAll(arr, v => (v - lo) * f), scale: "final", unit: "m",
                            note: $"처음 {c}개 단층의 변위를 더한 지형. 높이 축은 최종 지형과 같습니다.");
                    }
                    double decay = p.F("decay");
                    ctx.Trace("amp", Enumerable.Range(1, nf).Select(i => (double)i), Enumerable.Range(1, nf).Select(i => Math.Pow(i, -decay)),
                        label: "단층별 변위 크기 a_i", xlabel: "단층 번호");
                }
            }
            return new MethodOutput(CompareGrid.NormalizeRelief(o, grid.ReliefM));
        });
}
