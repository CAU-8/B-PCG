using System;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hero;
using Bpcg.Hydro;
using Bpcg.Metrics;

namespace Bpcg.Compare;

/// <summary>
/// 지각 융기 + 하천 침식 (Galin 2019 4.2.1; Cordonnier 외 2016): 시간을 한 걸음씩 돌리는 방식.
/// </summary>
/// <remarks>
/// ∂h/∂t = u(p) − K·A(p)^m·s(p)^n (n = 1) 을 Braun &amp; Willett 2013 의 암시적 방법으로 풉니다. 걸음마다 ε 채움 위에서 D8 물길을
/// 다시 고르고, 하류부터 z_i ← (z_i + u·Δt + F·z_r) / (1 + F), F = K·A^m·Δt / 거리 로 고칩니다. 원하면 사면 확산 D·∇²h 를
/// 명시적으로 더합니다. B-PCG 와 가장 가까운 기존 방법입니다. 다른 점은 (1) 정상상태를 바로 푸는 대신 시간을 돌리고,
/// (2) 암석이 하나이고(층 없음), (3) 땅속을 만들지 않는다는 것입니다. 기본 융기·출구는 B-PCG 평면 히어로와 같게 두어
/// (uplift = ridge, outlet = south) 같은 조건에서 두 풀이법을 비교합니다. 물길 계산은 이 저장소의 Hydro 코드를 그대로 씁니다.
/// </remarks>
public static class StreamPowerMethod
{
    public const double FillEpsM = 1.0e-3;

    /// <summary>융기 지도 (n·n,) [m/yr]. ridge: 평면 히어로와 같은 남북 단면, uniform: 최댓값 고르게, noise: fBm 을 [최솟값, 최댓값] 으로.</summary>
    public static double[] UpliftMap(CompareGrid grid, long seed, string kind)
    {
        if (kind == "ridge")
        {
            return Flat.FlatUplift(grid.Y(), grid.LengthM);
        }
        if (kind == "uniform")
        {
            return Enumerable.Repeat(Flat.UPeakMPerYr, grid.Cells).ToArray();
        }
        (double[] px, double[] py) = NoiseMethods.Points(grid, grid.LengthM / 2.0);
        double[] t = NoiseMethods.FbmKernel(px, py, NoiseMethods.Tables(seed, 4, 2.0, 11), NoiseMethods.GainAmps(4, 0.5));
        (double lo, double hi) = CompareGrid.MinMax(t);
        double span = Math.Max(hi - lo, 1e-12);
        return Array.ConvertAll(t, v => Flat.UBaseMPerYr + ((Flat.UPeakMPerYr - Flat.UBaseMPerYr) * (v - lo) / span));
    }

    /// <summary>출구 칸: south 는 남쪽 가장자리 가운데 5칸(평면 히어로와 같음), edges 는 네 가장자리.</summary>
    public static bool[] OutletMask(int n, string kind)
    {
        bool[] m = new bool[n * n];
        if (kind == "south")
        {
            int lo = (n / 2) - (Flat.OutletCells / 2);
            for (int i = lo; i < lo + Flat.OutletCells; i++)
            {
                m[((n - 1) * n) + i] = true;
            }
            return m;
        }
        for (int k = 0; k < n; k++)
        {
            m[k] = true;
            m[((n - 1) * n) + k] = true;
            m[k * n] = true;
            m[(k * n) + n - 1] = true;
        }
        return m;
    }

    private static void ImplicitStep(long[] order, long[] rcv, double[] distR, double[] z, double[] u, double[] a, double k, double m, double dt, double[] zNew)
    {
        foreach (long cl in order)
        {
            int c = (int)cl;
            int r = (int)rcv[c];
            if (r == c)
            {
                zNew[c] = z[c];
                continue;
            }
            double f = k * Math.Pow(a[c], m) * dt / distR[c];
            zNew[c] = (z[c] + (u[c] * dt) + (f * zNew[r])) / (1.0 + f);
        }
    }

    /// <summary>명시적 사면 확산 한 걸음 (가장자리는 닫힘, 출구 칸은 고정). k = D·Δt/dx².</summary>
    private static void Diffuse(double[] z, int n, double k, bool[] isOutlet, double[] o) =>
        Parallelism.For(0, n * n, c =>
        {
            if (isOutlet[c])
            {
                o[c] = z[c];
                return;
            }
            int j = c / n;
            int i = c % n;
            double s = 0.0;
            int cnt = 0;
            if (j > 0)
            {
                s += z[c - n];
                cnt++;
            }
            if (j < n - 1)
            {
                s += z[c + n];
                cnt++;
            }
            if (i > 0)
            {
                s += z[c - 1];
                cnt++;
            }
            if (i < n - 1)
            {
                s += z[c + 1];
                cnt++;
            }
            o[c] = z[c] + (k * (s - (cnt * z[c])));
        });

    public static readonly CompareMethod StreamPower = new(
        "stream_power",
        "융기 + 하천 침식 (시간 적분)",
        "simulation",
        "Galin 2019 4.2.1 (그림 20); Cordonnier 외 2016, Braun & Willett 2013",
        "융기 지도와 하천 침식 법칙을 수백만 년 동안 시간 순서로 적분. 암석 하나, 땅속 없음",
        [
            new Param("uplift", "ridge", "융기 지도", Kind: "str", Choices: ["ridge", "uniform", "noise"]),
            new Param("outlet", "south", "출구", Kind: "str", Choices: ["south", "edges"]),
            new Param("k", 2.0e-5, "침식 계수 K", Lo: 1e-9, Hi: 1e-1, Unit: "m^(1−2m)/yr"),
            new Param("m", 0.5, "면적 지수 m (n = 1)", Lo: 0.1, Hi: 1.0),
            new Param("diffusion", 0.01, "사면 확산 D", Lo: 0.0, Hi: 10.0, Unit: "m²/yr"),
            new Param("dt_yr", 2.0e4, "시간 걸음", Lo: 1.0, Hi: 1e7, Unit: "yr"),
            new Param("steps", 250L, "걸음 수", Kind: "int", Lo: 1, Hi: 100000),
            new Param("init_relief_m", 1.0, "처음 지형(작은 fBm)의 기복", Lo: 0.0, Hi: 1e4, Unit: "m"),
        ],
        (grid, seed, p, ctx) =>
        {
            int n = grid.N;
            CellGraph g = Graph.FlatGraph(n, n, grid.Dx);
            bool[] isOutlet = OutletMask(n, p.S("outlet"));
            double[] u = UpliftMap(grid, seed, p.S("uplift"));
            (double[] px, double[] py) = NoiseMethods.Points(grid, grid.LengthM / 8.0);
            double[] z = NoiseMethods.FbmKernel(px, py, NoiseMethods.Tables(seed, 6, 2.0, 12), NoiseMethods.GainAmps(6, 0.5));
            (double lo, double hi) = CompareGrid.MinMax(z);
            double f0 = p.F("init_relief_m") / Math.Max(hi - lo, 1e-12);
            for (int c = 0; c < z.Length; c++)
            {
                if (isOutlet[c])
                {
                    u[c] = 0.0;
                    z[c] = 0.0;
                }
                else
                {
                    z[c] = (z[c] - lo) * f0;
                }
            }
            double dt = p.F("dt_yr");
            int steps = p.I("steps");
            double kDiff = p.F("diffusion") * dt / (grid.Dx * grid.Dx);
            int sub = Math.Max(1, (int)Math.Ceiling(kDiff / 0.2)); // 명시적 확산 안정 조건 k ≤ 0.25
            ctx.Snapshot("융기 지도", Array.ConvertAll(u, v => v * 1e3), kind: "field", unit: "mm/yr", note: $"융기 '{p.S("uplift")}'. 출구 칸은 0 입니다.");
            ctx.Snapshot("처음 지형", z, unit: "m", note: "대칭을 깨려고 넣은 작은 fBm 입니다.");
            int[] snapAt = [.. new[] { 1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2 }.Select(f => Math.Max(1, (int)Math.Round(steps * f))).Distinct()];
            double[] rates = new double[steps];
            double[] means = new double[steps];
            double[] peaks = new double[steps];
            double[] zNew = new double[z.Length];
            double[] tmp = new double[z.Length];
            double[] a = new double[z.Length];
            for (int step = 0; step < steps; step++)
            {
                double[] zt = Depressions.FillEpsilon(z, g.Nbr, isOutlet, FillEpsM);
                (long[] rcv, _, _) = Routing.D8Receivers(zt, g.Nbr, g.Dist, isOutlet);
                long[] order = Routing.TopoOrder(rcv);
                a = AccumulateModule.Accumulate(rcv, order, g.Area);
                double[] distR = Drainage.ReceiverDistance(g, rcv);
                for (int c = 0; c < distR.Length; c++)
                {
                    if (rcv[c] == c)
                    {
                        distR[c] = grid.Dx;
                    }
                }
                ImplicitStep(order, rcv, distR, z, u, a, p.F("k"), p.F("m"), dt, zNew);
                if (kDiff > 0.0)
                {
                    for (int s = 0; s < sub; s++)
                    {
                        Diffuse(zNew, n, kDiff / sub, isOutlet, tmp);
                        (zNew, tmp) = (tmp, zNew);
                    }
                }
                double maxDz = 0.0;
                for (int c = 0; c < z.Length; c++)
                {
                    maxDz = Math.Max(maxDz, Math.Abs(zNew[c] - z[c]));
                }
                rates[step] = maxDz / dt;
                (z, zNew) = (zNew, z);
                means[step] = z.Average();
                peaks[step] = z.Max();
                if (snapAt.Contains(step + 1))
                {
                    ctx.Snapshot($"{(step + 1) * dt / 1e6:F2} 백만 년", z, unit: "m", scale: "final", note: $"{step + 1}걸음 뒤 지형입니다.");
                }
                if ((step + 1) % 50 == 0)
                {
                    ctx.Say($"    stream_power {step + 1}/{steps} 걸음");
                }
            }
            if (ctx.Recording)
            {
                double[] years = Enumerable.Range(1, steps).Select(i => i * dt / 1e6).ToArray();
                ctx.Snapshot("최종 지형", z, unit: "m", scale: "final", note: $"{steps * dt / 1e6:F2} 백만 년 뒤 지형입니다.");
                ctx.Snapshot("상류 면적", Array.ConvertAll(a, v => Math.Log10(v)), kind: "field", unit: "log₁₀ m²", note: "마지막 걸음의 물길로 모은 상류 면적입니다.");
                ctx.Trace("rate", years, rates, label: "걸음당 최대 높이 변화율", unit: "m/yr", xlabel: "백만 년");
                ctx.Trace("mean", years, means, label: "평균 고도", unit: "m", xlabel: "백만 년");
                ctx.Trace("peak", years, peaks, label: "가장 높은 고도", unit: "m", xlabel: "백만 년");
            }
            var output = new MethodOutput(z);
            output.Layers["uplift_m_per_yr"] = u;
            output.Info["years"] = dt * steps;
            output.Info["final_max_rate_m_per_yr"] = rates[^1];
            output.Info["steady_ratio"] = rates[^1] / Flat.UPeakMPerYr;
            return output;
        });
}
