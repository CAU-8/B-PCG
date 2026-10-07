using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Compare;

/// <summary>
/// 침식 시뮬레이션 (Galin 2019 4.1, 4.3.2): fBm 지형을 열 침식이나 물방울 입자 침식으로 깎습니다.
/// </summary>
/// <remarks>
/// 시작 지형은 <see cref="NoiseMethods.BaseFbm"/> 이라서, 같은 시드·같은 fBm 매개변수면 'fbm' 방법의 결과와 같습니다.
/// 논문 4.3.3 이 지적하듯 이것은 노이즈 지형을 꾸미는 후처리이고, 융기로 생긴 지형이 아닙니다.
/// thermal: 이웃과의 높이 차가 안식각 높이(tan θ·거리)를 넘으면 넘친 만큼의 일부를 낮은 이웃에 나눠 줍니다
/// (Musgrave 외 1989, Olsen 2004 의 나누는 방식). 칸 순서대로 제자리에서 고칩니다.
/// hydraulic: 물방울 입자(라그랑주 방식, Chiba 외 1998 계열)를 해시로 정한 자리에 떨어뜨려, 경사를 따라 굴리며 운반 능력보다
/// 적게 실었으면 깎고 많이 실었으면 쌓습니다(Beyer 2015 의 구현 꼴). 높이는 원래 구현처럼 [0, 1] 로 맞춰 계산하고
/// (매개변수 기본값이 이 단위에 맞춰져 있음) 0 아래로는 깎지 않습니다.
/// </remarks>
public static class ErosionMethods
{
    private const long StreamDrop = NoiseMethods.StreamBase + 401;
    private static readonly int[] Dj = [-1, -1, -1, 0, 0, 1, 1, 1];
    private static readonly int[] Di = [-1, 0, 1, -1, 1, -1, 0, 1];
    private static readonly int[] ThermalMarks = [1, 5, 15, 30, 60, 120, 250, 500];

    /// <summary>제자리 열 침식 iterations 번. moved[k]: k 번째 반복에서 옮긴 칸 수.</summary>
    private static void ThermalSteps(double[] z, int n, double dx, double tanT, double rate, int iterations, long[] moved, int movedOffset)
    {
        double[] exc = new double[8];
        for (int it = 0; it < iterations; it++)
        {
            long count = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    double h = z[(j * n) + i];
                    double eMax = 0.0;
                    double eTot = 0.0;
                    for (int k = 0; k < 8; k++)
                    {
                        exc[k] = 0.0;
                        int jj = j + Dj[k];
                        int ii = i + Di[k];
                        if (jj < 0 || jj >= n || ii < 0 || ii >= n)
                        {
                            continue;
                        }
                        double dl = (Dj[k] != 0 && Di[k] != 0) ? Math.Sqrt(2.0) : 1.0;
                        double e = h - z[(jj * n) + ii] - (tanT * dx * dl);
                        if (e > 0.0)
                        {
                            exc[k] = e;
                            eTot += e;
                            eMax = Math.Max(eMax, e);
                        }
                    }
                    if (eTot <= 0.0)
                    {
                        continue;
                    }
                    double move = rate * eMax;
                    for (int k = 0; k < 8; k++)
                    {
                        if (exc[k] > 0.0)
                        {
                            z[((j + Dj[k]) * n) + i + Di[k]] += move * exc[k] / eTot;
                        }
                    }
                    z[(j * n) + i] = h - move;
                    count++;
                }
            }
            moved[movedOffset + it] = count;
        }
    }

    private static double[] Diff(double[] a, double[] b)
    {
        double[] o = new double[a.Length];
        for (int k = 0; k < a.Length; k++)
        {
            o[k] = a[k] - b[k];
        }
        return o;
    }

    public static readonly CompareMethod Thermal = new(
        "thermal",
        "열 침식",
        "simulation",
        "Galin 2019 4.1.1; Musgrave 외 1989",
        "fBm 지형에서 흙이 버티는 가장 가파른 각(안식각)보다 가파른 비탈의 흙을 아래 이웃으로 옮겨 다듬음",
        [
            .. NoiseMethods.FbmParams,
            new Param("talus_deg", 33.0, "흙이 무너지지 않고 버티는 가장 가파른 비탈 각(안식각)", Lo: 1.0, Hi: 80.0, Unit: "°"),
            new Param("rate", 0.25, "안식각을 넘은 높이 가운데 반복 한 번에 옮기는 비율", Lo: 0.01, Hi: 0.5),
            new Param("iterations", 60L, "반복 수", Kind: "int", Lo: 1, Hi: 5000),
        ],
        (grid, seed, p, ctx) =>
        {
            double[] z = NoiseMethods.BaseFbm(grid, seed, p);
            double[] start = (double[])z.Clone();
            double tanT = Math.Tan(p.F("talus_deg") * Math.PI / 180.0);
            int iters = p.I("iterations");
            long[] moved = new long[iters];
            List<int> marks = ctx.Recording ? [.. ThermalMarks.Where(c => c < iters), iters] : [iters];
            ctx.Snapshot("시작 지형 (fbm)", start, unit: "m", note: "같은 시드의 fbm 과 같은 지형입니다.");
            int done = 0;
            foreach (int c in marks)
            {
                ThermalSteps(z, grid.N, grid.Dx, tanT, p.F("rate"), c - done, moved, done);
                done = c;
                ctx.Snapshot($"{c}번 반복", z, unit: "m", scale: "final", note: $"안식각 {p.F("talus_deg"):G}° 를 넘는 비탈을 {c}번 고친 지형입니다.");
            }
            if (ctx.Recording)
            {
                ctx.Snapshot("높이 변화", Diff(z, start), kind: "diverging", unit: "m", note: "최종 − 시작. 음수(파랑)는 깎인 곳, 양수(빨강)는 쌓인 곳입니다.");
                ctx.Trace("moved", Enumerable.Range(1, iters).Select(i => (double)i), moved.Select(v => (double)v),
                    label: "반복마다 옮긴 칸 수", unit: "칸", xlabel: "반복");
            }
            var output = new MethodOutput(z);
            output.Info["moved_cells_last_iter"] = moved[^1];
            return output;
        });

    /// <summary>깎기 붓: 반지름 안 칸의 (dj, di, 가중치), 가중치 합 1.</summary>
    public static (int[] Dj, int[] Di, double[] W) Brush(int radius)
    {
        var dj = new List<int>();
        var di = new List<int>();
        var w = new List<double>();
        for (int a = -radius; a <= radius; a++)
        {
            for (int b = -radius; b <= radius; b++)
            {
                double d = double.Hypot(a, b);
                if (d <= radius)
                {
                    dj.Add(a);
                    di.Add(b);
                    w.Add(Math.Max(0.0, radius + 1.0 - d));
                }
            }
        }
        double sum = NpReduce.Sum(w.ToArray()); // numpy pairwise 합과 같게 (붓 무게의 마지막 비트가 물방울 경로를 바꿈)
        return ([.. dj], [.. di], [.. w.Select(v => v / sum)]);
    }

    private static double Bilinear(double[] h, int n, int ix, int iy, double fx, double fy) =>
        (h[(iy * n) + ix] * (1 - fx) * (1 - fy)) + (h[(iy * n) + ix + 1] * fx * (1 - fy))
        + (h[((iy + 1) * n) + ix] * (1 - fx) * fy) + (h[((iy + 1) * n) + ix + 1] * fx * fy);

    /// <summary>
    /// 물방울 d0 ≤ d &lt; d0 + count 를 굴립니다 (제자리). h: (n·n,) [0, 1] 높이. 물방울 d 의 출발점은 d 만으로 정해지므로
    /// 나눠 돌려도 한 번에 돌린 것과 같습니다. 반환: (깎은 양, 쌓은 양).
    /// </summary>
    private static (double Eroded, double Deposited) Droplets(double[] h, int n, long seed, int d0, int count, ParamValues p, (int[] Dj, int[] Di, double[] W) brush)
    {
        double inertia = p.F("inertia");
        double capacity = p.F("capacity");
        double minSlope = p.F("min_slope");
        double erode = p.F("erode");
        double deposit = p.F("deposit");
        double evaporate = p.F("evaporate");
        double gravity = p.F("gravity");
        int maxSteps = p.I("max_steps");
        double eroded = 0.0;
        double deposited = 0.0;
        for (int d = d0; d < d0 + count; d++)
        {
            double x = Hashing.HashUnit(seed, d, StreamDrop) * (n - 1);
            double y = Hashing.HashUnit(seed, d, StreamDrop + 1) * (n - 1);
            double dirx = 0.0;
            double diry = 0.0;
            double speed = 1.0;
            double water = 1.0;
            double sed = 0.0;
            for (int step = 0; step < maxSteps; step++)
            {
                int ix = (int)x;
                int iy = (int)y;
                if (ix < 0 || iy < 0 || ix >= n - 1 || iy >= n - 1)
                {
                    break;
                }
                double fx = x - ix;
                double fy = y - iy;
                double gx = ((h[(iy * n) + ix + 1] - h[(iy * n) + ix]) * (1 - fy)) + ((h[((iy + 1) * n) + ix + 1] - h[((iy + 1) * n) + ix]) * fy);
                double gy = ((h[((iy + 1) * n) + ix] - h[(iy * n) + ix]) * (1 - fx)) + ((h[((iy + 1) * n) + ix + 1] - h[(iy * n) + ix + 1]) * fx);
                double hOld = Bilinear(h, n, ix, iy, fx, fy);
                dirx = (dirx * inertia) - (gx * (1 - inertia));
                diry = (diry * inertia) - (gy * (1 - inertia));
                double len = Math.Sqrt((dirx * dirx) + (diry * diry));
                if (len < 1e-12)
                {
                    break;
                }
                dirx /= len;
                diry /= len;
                double nx = x + dirx;
                double ny = y + diry;
                int jx = (int)nx;
                int jy = (int)ny;
                if (jx < 0 || jy < 0 || jx >= n - 1 || jy >= n - 1)
                {
                    break;
                }
                double dh = Bilinear(h, n, jx, jy, nx - jx, ny - jy) - hOld;
                double cap = Math.Max(-dh, minSlope) * speed * water * capacity;
                if (sed > cap || dh > 0.0)
                {
                    double amt = dh > 0.0 ? Math.Min(dh, sed) : (sed - cap) * deposit;
                    sed -= amt;
                    deposited += amt;
                    h[(iy * n) + ix] += amt * (1 - fx) * (1 - fy);
                    h[(iy * n) + ix + 1] += amt * fx * (1 - fy);
                    h[((iy + 1) * n) + ix] += amt * (1 - fx) * fy;
                    h[((iy + 1) * n) + ix + 1] += amt * fx * fy;
                }
                else
                {
                    double amt = Math.Min((cap - sed) * erode, -dh);
                    for (int b = 0; b < brush.W.Length; b++)
                    {
                        int jj = iy + brush.Dj[b];
                        int ii = ix + brush.Di[b];
                        if (jj < 0 || jj >= n || ii < 0 || ii >= n)
                        {
                            continue;
                        }
                        double take = Math.Min(amt * brush.W[b], h[(jj * n) + ii]);
                        h[(jj * n) + ii] -= take;
                        sed += take;
                        eroded += take;
                    }
                }
                speed = Math.Sqrt(Math.Max((speed * speed) - (dh * gravity), 0.0));
                water *= 1.0 - evaporate;
                x = nx;
                y = ny;
            }
        }
        return (eroded, deposited);
    }

    public static readonly CompareMethod Hydraulic = new(
        "hydraulic",
        "수력 침식 (입자)",
        "simulation",
        "Galin 2019 4.3.2; Chiba 외 1998, Beyer 2015",
        "fBm 지형 위로 물방울을 칸마다 1.5개씩 굴려 비탈을 따라 깎고 쌓음. 게임에서 흔한 후처리",
        [
            .. NoiseMethods.FbmParams,
            new Param("drops_per_cell", 1.5, "칸당 물방울 수", Lo: 0.01, Hi: 50.0),
            new Param("inertia", 0.05, "방향 관성 (0 이면 늘 가장 가파른 쪽)", Lo: 0.0, Hi: 0.99),
            new Param("capacity", 4.0, "물방울이 실어 나를 수 있는 흙의 양 배율(운반 능력)", Lo: 0.01, Hi: 64.0),
            new Param("min_slope", 0.01, "운반 능력을 셀 때 쓰는 가장 작은 경사 (평지에서도 조금은 나르게)", Lo: 0.0, Hi: 1.0),
            new Param("erode", 0.3, "더 실을 수 있는 양 가운데 한 걸음에 깎아 싣는 비율", Lo: 0.0, Hi: 1.0),
            new Param("deposit", 0.3, "실을 수 있는 양을 넘친 흙 가운데 한 걸음에 내려놓는 비율", Lo: 0.0, Hi: 1.0),
            new Param("evaporate", 0.01, "걸음마다 물방울이 마르는 비율", Lo: 0.0, Hi: 0.5),
            new Param("gravity", 4.0, "내려간 높이가 물방울 속도를 얼마나 올리는지", Lo: 0.0, Hi: 64.0),
            new Param("max_steps", 30L, "물방울 하나의 최대 걸음 수", Kind: "int", Lo: 1, Hi: 4096),
            new Param("radius", 3L, "한 번 깎을 때 둘레 몇 칸까지 함께 깎나 (붓 반지름, 칸)", Kind: "int", Lo: 0, Hi: 16),
        ],
        (grid, seed, p, ctx) =>
        {
            double[] z = NoiseMethods.BaseFbm(grid, seed, p);
            (double lo, double hi) = CompareGrid.MinMax(z);
            double span = Math.Max(hi - lo, 1e-12);
            double[] h = Array.ConvertAll(z, v => (v - lo) / span);
            (int[] Dj, int[] Di, double[] W) brush = Brush(p.I("radius"));
            int nDrops = (int)Math.Round(p.F("drops_per_cell") * grid.Cells);
            List<int> marks = ctx.Recording
                ? [.. new SortedSet<int>(new[] { nDrops / 64, nDrops / 16, nDrops / 4, nDrops / 2, nDrops }.Where(c => c > 0))]
                : [nDrops];
            ctx.Snapshot("시작 지형 (fbm)", z, unit: "m", note: "같은 시드의 fbm 과 같은 지형입니다.");
            double cell = grid.Dx * grid.Dx * span;
            double eroded = 0.0;
            double deposited = 0.0;
            var xs = new List<double>();
            var ero = new List<double>();
            var dep = new List<double>();
            int done = 0;
            foreach (int c in marks)
            {
                (double e, double d) = Droplets(h, grid.N, seed, done, c - done, p, brush);
                eroded += e;
                deposited += d;
                done = c;
                if (ctx.Recording)
                {
                    ctx.Snapshot($"물방울 {c:N0}개", Array.ConvertAll(h, v => (v * span) + lo), unit: "m", scale: "final", note: $"물방울 {c:N0}개를 굴린 뒤의 지형입니다.");
                    xs.Add(c);
                    ero.Add(eroded * cell);
                    dep.Add(deposited * cell);
                }
            }
            double[] zf = Array.ConvertAll(h, v => (v * span) + lo);
            if (ctx.Recording)
            {
                ctx.Snapshot("높이 변화", Diff(zf, z), kind: "diverging", unit: "m", note: "최종 − 시작. 음수(파랑)는 깎인 곳, 양수(빨강)는 쌓인 곳입니다.");
                ctx.Trace("eroded", xs, ero, label: "깎은 부피 누적", unit: "m³", xlabel: "물방울 수");
                ctx.Trace("deposited", xs, dep, label: "쌓은 부피 누적", unit: "m³", xlabel: "물방울 수");
            }
            var output = new MethodOutput(zf);
            output.Info["drops"] = (long)nDrops;
            output.Info["eroded_m3"] = eroded * cell;
            output.Info["deposited_m3"] = deposited * cell;
            return output;
        });
}
