using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;

namespace Bpcg.Landscape;

/// <summary>정상상태 솔버 결과 (SolverResult). 배열은 모두 (N,).</summary>
public sealed class SolverResult
{
    public required double[] Z { get; init; }

    public required long[] Receiver { get; init; }

    public required long[] Order { get; init; }

    public required double[] Discharge { get; init; }

    public required double[] DrainageArea { get; init; }

    public required double[] SedimentFlux { get; init; }

    public required double[] Slope { get; init; }

    public required double[] KS { get; init; }

    public required double[] SCritSurface { get; init; }

    public required int Iterations { get; init; }

    public List<OrderedDictionary<string, object?>> History { get; init; } = [];

    public bool Converged { get; init; }

    public double Seconds { get; init; }

    public int NFrozen { get; init; }

    public bool[]? Frozen { get; init; }

    /// <summary>솔버가 실제로 쓴 U [m/yr].</summary>
    public double[]? UpliftEffective { get; init; }

    /// <summary>FIELDS 이름으로 된 필드 묶음. cast 면 FIELDS dtype 으로 바꿉니다.</summary>
    public FieldSet Fields(bool cast = false)
    {
        var o = new FieldSet
        {
            ["z_m"] = Z,
            ["receiver"] = Receiver,
            ["drainage_area_m2"] = DrainageArea,
            ["discharge_m3_per_yr"] = Discharge,
            ["sediment_flux_m3_per_yr"] = SedimentFlux,
            ["slope"] = Slope,
            ["k_s"] = KS,
            ["s_crit"] = SCritSurface,
        };
        if (cast)
        {
            var c = new FieldSet();
            foreach (KeyValuePair<string, Array> kv in o)
            {
                c[kv.Key] = FieldSet.CastToField(kv.Key, kv.Value);
            }
            return c;
        }
        return o;
    }

    /// <summary>진단값: iterations, converged, seconds, final_n_changed, final_max_dz_m, n_frozen.</summary>
    public OrderedDictionary<string, object?> Diag()
    {
        long nChanged = -1;
        double maxDz = double.NaN;
        if (History.Count > 0)
        {
            OrderedDictionary<string, object?> last = History[^1];
            nChanged = Convert.ToInt64(last["n_changed"], System.Globalization.CultureInfo.InvariantCulture);
            maxDz = (double)last["max_dz"]!;
        }
        return new OrderedDictionary<string, object?>
        {
            ["iterations"] = (long)Iterations,
            ["converged"] = Converged,
            ["seconds"] = Seconds,
            ["final_n_changed"] = nChanged,
            ["final_max_dz_m"] = maxDz,
            ["n_frozen"] = (long)NFrozen,
        };
    }
}

/// <summary>
/// 정상상태 솔버: 물길 방향 맞추기 반복 + 경사 법칙 + 층 경계별 적분 (src/bpcg/landscape/solver.py, docs/pipeline.md 7.1).
/// </summary>
public static class Solver
{
    public const double InitSlope = 1.0e-3;
    public const double InitNoiseM = 10.0;
    public const double InitNoiseWavelengthCells = 16.0;
    private const long StreamInitNoise = 7101;
    public const int FreezeAfterFlips = 16;

    private const int NSlots = CellGraph.NSlots;

    // 칸이 이보다 적으면 층별 병렬로 얻는 것보다 일꾼을 띄우는 비용이 커서 한 줄로 쌓습니다.
    private const int LevelParallelMinCells = 1 << 15;

    private readonly record struct LawParams(
        double Theta, double N, double KRef, double URef, double Diff, double GDep, double RRef, double SMin, double SCritDefault);

    // 고도 z 에서 위로 올라갈 때 지나는 층: bottom ≤ z 인 첫 층, 없으면 L.
    private static int UpperLayer(double[] bottom, int nl, int c, double z)
    {
        int b0 = c * nl;
        for (int i = 0; i < nl; i++)
        {
            if (bottom[b0 + i] <= z)
            {
                return i;
            }
        }
        return nl;
    }

    private static double LawSlope(double ksRef, double kPow, double qt, double hill, double scrit, double sMin)
    {
        double ks = ksRef * kPow;
        double s = 1.0 / ((qt / ks) + hill);
        if (s > scrit)
        {
            s = scrit;
        }
        if (s < sMin)
        {
            s = sMin;
        }
        return s;
    }

    private static double ReceiverSlotDistance(int c, long r, int[] nbr, double[] dist)
    {
        for (int s = 0; s < NSlots; s++)
        {
            if (nbr[(c * NSlots) + s] == r)
            {
                return dist[(c * NSlots) + s];
            }
        }
        return double.PositiveInfinity;
    }

    // 층 경계별 적분: 출구에서 상류로 칸마다 높이를 쌓습니다. levels 가 있으면 출구에서 같은 칸 수만큼 떨어진
    // 칸끼리 여러 코어로 쌓습니다(Parallelism.ForLevels). 칸마다 자기 높이만 쓰므로 어느 쪽이든 결과가 같습니다.
    private static void IntegrateKernel(
        long[] order, Levels? levels, long[] rcv, double[] rDist, double[] zOutlet, double[] q, double[] aUp, double[] qs,
        double[] u, double[] width, double[] bottom, int nl, LawTable law, in LawParams p, double invN, double riseMin,
        double[] zNew)
    {
        LawParams pp = p;
        if (levels is null)
        {
            foreach (long c in order)
            {
                IntegrateCell((int)c, rcv, rDist, zOutlet, q, aUp, qs, u, width, bottom, nl, law, pp, invN, riseMin, zNew);
            }
            return;
        }
        int nLevels = levels.Build(order, rcv);
        Parallelism.ForLevels(levels.Start, nLevels, levels.Cells,
            c => IntegrateCell(c, rcv, rDist, zOutlet, q, aUp, qs, u, width, bottom, nl, law, pp, invN, riseMin, zNew));
    }

    private static void IntegrateCell(
        int c, long[] rcv, double[] rDist, double[] zOutlet, double[] q, double[] aUp, double[] qs, double[] u,
        double[] width, double[] bottom, int nl, LawTable law, in LawParams p, double invN, double riseMin, double[] zNew)
    {
        int nr = nl + 1;
        long r = rcv[c];
        if (r == c)
        {
            zNew[c] = zOutlet[c];
            return;
        }
        double d = rDist[c];
        double z = zNew[r];
        double zFloor = z + riseMin;
        double e = u[c] + (p.GDep * p.RRef * qs[c] / q[c]);
        if (e <= 0.0)
        {
            z += p.SMin * d;
            zNew[c] = z >= zFloor ? z : zFloor;
            return;
        }
        double qt = Math.Exp(p.Theta * Math.Log(q[c]));
        double a = aUp[c] / width[c];
        double hill = p.Diff / (e * a);
        double ksRef = p.KRef * Math.Pow(e / p.URef, invN);
        double rem = d;
        while (true)
        {
            int i = UpperLayer(bottom, nl, c, z);
            int m = law.Slot((c * nr) + i);
            double s = LawSlope(ksRef, law.KPow[m], qt, hill, law.SCrit[m], p.SMin);
            if (i == 0)
            {
                z += rem * s;
                break;
            }
            double up = bottom[(c * nl) + i - 1];
            double need = (up - z) / s;
            if (need >= rem)
            {
                z += rem * s;
                break;
            }
            z = up;
            rem -= need;
        }
        zNew[c] = z >= zFloor ? z : zFloor;
    }

    // 칸마다 수신 셀까지 거리. 수신 셀이 이웃이 아닌 칸(내부 오류)은 0 으로 두고 그 수를 돌려줍니다.
    private static int ReceiverDistances(long[] rcv, int[] nbr, double[] dist, double[] rDist)
    {
        int[] bad = new int[Parallelism.RangeCount(rcv.Length)];
        Parallelism.ForRanges(rcv.Length, (j, lo, hi) =>
        {
            int count = 0;
            for (int c = lo; c < hi; c++)
            {
                long r = rcv[c];
                double d = r == c ? 0.0 : ReceiverSlotDistance(c, r, nbr, dist);
                if (!double.IsFinite(d))
                {
                    count++;
                    d = 0.0;
                }
                rDist[c] = d;
            }
            bad[j] = count;
        });
        int total = 0;
        foreach (int v in bad)
        {
            total += v;
        }
        return total;
    }

    // (K 배율, S_crit) 쌍은 보통 암석 종류 수만큼뿐입니다. 쌍마다 번호를 매겨 칸·층마다 1바이트만 읽게 하고,
    // K 배율^(−1/n) 은 쌍마다 한 번만 계산합니다. 쌍이 256가지를 넘으면 칸·층 배열을 그대로 씁니다.
    internal static LawTable BuildLawTable(double[] kMult, double[] sCrit, double invN)
    {
        var keyK = new List<double>();
        var keyS = new List<double>();
        byte[] index = new byte[kMult.Length];
        int last = -1;
        for (int j = 0; j < kMult.Length; j++)
        {
            double k = kMult[j];
            double sc = sCrit[j];
            int m = last >= 0 && keyK[last] == k && keyS[last] == sc ? last : -1;
            for (int t = 0; m < 0 && t < keyK.Count; t++)
            {
                if (keyK[t] == k && keyS[t] == sc)
                {
                    m = t;
                }
            }
            if (m < 0)
            {
                if (keyK.Count == 256)
                {
                    double[] kPowAll = new double[kMult.Length];
                    Parallelism.For(0, kMult.Length, i => kPowAll[i] = Math.Pow(kMult[i], -invN));
                    return new LawTable(null, kPowAll, sCrit);
                }
                m = keyK.Count;
                keyK.Add(k);
                keyS.Add(sc);
            }
            index[j] = (byte)m;
            last = m;
        }
        double[] kPow = new double[keyK.Count];
        for (int t = 0; t < kPow.Length; t++)
        {
            kPow[t] = Math.Pow(keyK[t], -invN);
        }
        return new LawTable(index, kPow, [.. keyS]);
    }

    // 칸·층마다 경사 법칙에 쓰는 (K 배율^(−1/n), S_crit). index 가 있으면 칸·층 번호 j 의 값은 짧은 표의
    // index[j] 번째에 있고, 없으면 배열의 j 번째에 있습니다.
    internal sealed class LawTable(byte[]? index, double[] kPow, double[] sCrit)
    {
        public double[] KPow { get; } = kPow;

        public double[] SCrit { get; } = sCrit;

        public int Slot(int j) => index is null ? j : index[j];
    }

    // 출구에서 몇 칸 떨어졌는지(층)로 칸을 묶은 표. 반복마다 수신 셀이 바뀌므로 반복마다 다시 만듭니다.
    private sealed class Levels(int n)
    {
        private readonly int[] _level = new int[n];
        private readonly int[] _fill = new int[n + 1];

        public int[] Start { get; } = new int[n + 1];

        public int[] Cells { get; } = new int[n];

        // order 는 하류가 먼저 오는 순서라 수신 셀의 층이 늘 먼저 정해집니다. 반환: 층 수.
        public int Build(long[] order, long[] rcv)
        {
            int nLevels = 0;
            foreach (long cl in order)
            {
                int c = (int)cl;
                int r = (int)rcv[c];
                int lv = r == c ? 0 : _level[r] + 1;
                _level[c] = lv;
                nLevels = Math.Max(nLevels, lv + 1);
            }
            Array.Clear(Start, 0, nLevels + 1);
            foreach (int lv in _level)
            {
                Start[lv + 1]++;
            }
            for (int lv = 0; lv < nLevels; lv++)
            {
                Start[lv + 1] += Start[lv];
            }
            Array.Copy(Start, _fill, nLevels);
            foreach (long cl in order)
            {
                int c = (int)cl;
                Cells[_fill[_level[c]]++] = c;
            }
            return nLevels;
        }
    }

    private static void SurfaceLawKernel(
        long[] rcv, int[] nbr, double[] dist, double[] z, double[] q, double[] qs, double[] u, double[] bottom, int nl,
        double[] kMult, double[] sCrit, in LawParams p, double invN, double[] slope, double[] kS, double[] sCritOut)
    {
        int nr = nl + 1;
        LawParams pp = p;
        Parallelism.For(0, rcv.Length, c =>
        {
            int i = Model.LayerIndexAt(bottom, nl, c, z[c]);
            sCritOut[c] = sCrit[(c * nr) + i];
            long r = rcv[c];
            if (r == c)
            {
                slope[c] = 0.0;
                kS[c] = 0.0;
                return;
            }
            double d = ReceiverSlotDistance(c, r, nbr, dist);
            slope[c] = (z[c] - z[r]) / d;
            double e = u[c] + (pp.GDep * pp.RRef * qs[c] / q[c]);
            kS[c] = e > 0.0 ? pp.KRef * Math.Pow(e / (pp.URef * kMult[(c * nr) + i]), invN) : 0.0;
        });
    }

    // 16번 넘게 뒤집힌 칸은 옛 방향에 묶습니다. 칸마다 자기 값만 바꾸므로 조각으로 나눠 돌리고 조각 순서로 셉니다.
    private static (int NChanged, int NFrozen) FreezeKernel(double[] zt, long[] prev, long[] rcv, int[] flips, int maxFlips)
    {
        int[] changed = new int[Parallelism.RangeCount(rcv.Length)];
        int[] frozen = new int[changed.Length];
        Parallelism.ForRanges(rcv.Length, (j, lo, hi) =>
        {
            int nChanged = 0;
            int nFrozen = 0;
            for (int c = lo; c < hi; c++)
            {
                long p = prev[c];
                if (rcv[c] != p)
                {
                    if (flips[c] >= maxFlips && p != c && zt[p] < zt[c])
                    {
                        rcv[c] = p;
                    }
                    else
                    {
                        flips[c]++;
                        nChanged++;
                    }
                }
                if (flips[c] >= maxFlips)
                {
                    nFrozen++;
                }
            }
            changed[j] = nChanged;
            frozen[j] = nFrozen;
        });
        int totalChanged = 0;
        int totalFrozen = 0;
        for (int j = 0; j < changed.Length; j++)
        {
            totalChanged += changed[j];
            totalFrozen += frozen[j];
        }
        return (totalChanged, totalFrozen);
    }

    private static bool HasEpsDrop(double[] z, long[] rcv, bool[] isOutlet, double eps)
    {
        for (int c = 0; c < z.Length; c++)
        {
            if (!isOutlet[c] && !(z[c] >= z[rcv[c]] + eps))
            {
                return false;
            }
        }
        return true;
    }

    private static LawParams ReadLawParams(Config cfg)
    {
        var p = new LawParams(
            cfg.F("landscape.theta"), cfg.F("landscape.slope_exponent_n"), cfg.F("landscape.k_ref"),
            cfg.F("landscape.u_ref_m_per_yr"), cfg.F("landscape.hillslope_diffusivity_m2_per_yr"),
            cfg.F("landscape.deposition_g"), cfg.F("climate.runoff_ref_m_per_yr"), cfg.F("landscape.s_min"),
            cfg.F("landscape.s_crit_default"));
        foreach ((string key, double v) in new[] { ("n", p.N), ("k_ref", p.KRef), ("u_ref", p.URef), ("r_ref", p.RRef), ("s_min", p.SMin), ("s_crit_default", p.SCritDefault) })
        {
            if (!(double.IsFinite(v) && v > 0.0))
            {
                throw new ArgumentException($"설정 값 {key} 는 0 보다 커야 합니다: {v}");
            }
        }
        foreach ((string key, double v) in new[] { ("theta", p.Theta), ("diff", p.Diff), ("g_dep", p.GDep) })
        {
            if (!(double.IsFinite(v) && v >= 0.0))
            {
                throw new ArgumentException($"설정 값 {key} 는 0 이상이어야 합니다: {v}");
            }
        }
        return p;
    }

    private static (double[] Bottom, int NLayers, double[] KMult, double[] SCrit) LayerArrays(LayerColumns? layers, int n, double sCritDefault)
    {
        if (layers is null)
        {
            double[] k = new double[n];
            double[] s = new double[n];
            Array.Fill(k, 1.0);
            Array.Fill(s, sCritDefault);
            return ([], 0, k, s);
        }
        if (layers.NCells != n)
        {
            throw new ArgumentException($"layers 의 칸 수 {layers.NCells} 가 그래프 칸 수 {n} 와 다릅니다");
        }
        foreach (double v in layers.Bottom)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("layers 의 bottom·k_mult 에 NaN 이나 inf 가 있습니다");
            }
        }
        for (int i = 0; i < layers.KMult.Length; i++)
        {
            if (!double.IsFinite(layers.KMult[i]))
            {
                throw new ArgumentException("layers 의 bottom·k_mult 에 NaN 이나 inf 가 있습니다");
            }
            if (!(layers.KMult[i] > 0.0) || !(layers.SCrit[i] > 0.0))
            {
                throw new ArgumentException("layers 의 k_mult 와 s_crit 는 모두 0 보다 커야 합니다");
            }
        }
        return (layers.Bottom, layers.NLayers, layers.KMult, layers.SCrit);
    }

    private static void CheckOutlets(bool[] isOutlet, int n)
    {
        if (isOutlet.Length != n)
        {
            throw new ArgumentException($"is_outlet 은 ({n},) bool 배열이어야 합니다");
        }
        if (Array.IndexOf(isOutlet, true) < 0)
        {
            throw new ArgumentException("출구 칸이 하나도 없습니다");
        }
    }

    private static double[] Vector(double[] x, int n, string name)
    {
        if (x.Length == 1 && n != 1)
        {
            double[] o = new double[n];
            Array.Fill(o, x[0]);
            return o;
        }
        if (x.Length != n)
        {
            throw new ArgumentException($"{name} 은 ({n},) 이어야 합니다: 길이 {x.Length}");
        }
        return x;
    }

    private static void CheckOutletValues(double[] zo, bool[] mask)
    {
        for (int c = 0; c < zo.Length; c++)
        {
            if (mask[c] && !double.IsFinite(zo[c]))
            {
                throw new ArgumentException("출구 칸의 z_outlet 에 NaN 이나 inf 가 있습니다");
            }
        }
    }

    /// <summary>
    /// 솔버의 초기 지형 z = z_outlet + 1e-3·(출구까지 거리) + 10 m·fbm (initial_surface).
    /// zOutlet 은 길이 1(스칼라) 또는 (N,).
    /// </summary>
    public static double[] InitialSurface(CellGraph graph, bool[] isOutlet, double[] zOutlet, Config cfg)
    {
        int n = graph.NCells;
        CheckOutlets(isOutlet, n);
        double[] zo = Vector(zOutlet, n, "z_outlet");
        CheckOutletValues(zo, isOutlet);
        (double[] dist, _, double[] zSrc) = Distance.NearestSourceValues(graph, isOutlet, zo);
        int bad = 0;
        foreach (double d in dist)
        {
            bad += double.IsFinite(d) ? 0 : 1;
        }
        if (bad > 0)
        {
            throw new ArgumentException($"출구에 닿지 않는 칸이 {bad}개 있습니다");
        }
        long seed = (long)(Hashing.Hash3(cfg.I("planet.seed"), StreamInitNoise, 0) >> 33);
        double scale = InitNoiseWavelengthCells * graph.Spacing;
        double[] pts = new double[graph.Pos.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = graph.Pos[i] / scale;
        }
        double[] noise = Noise.Fbm3(pts, seed);
        double[] z = new double[n];
        for (int c = 0; c < n; c++)
        {
            z[c] = isOutlet[c] ? zo[c] : zSrc[c] + (InitSlope * dist[c]) + (InitNoiseM * noise[c]);
        }
        return z;
    }

    /// <summary>
    /// 정상상태 지형을 물길 방향 맞추기 반복으로 풉니다 (solve_steady_state). zOutlet·uplift·runoffEff 등은
    /// 길이 1(스칼라) 또는 (N,). layers 가 null 이면 기준 암석 하나(K 배율 1, S_crit = s_crit_default).
    /// </summary>
    public static SolverResult SolveSteadyState(
        CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, LayerColumns? layers,
        Config cfg, double[]? zInit = null, double[]? extraInflow = null, int? maxIter = null, Action<string>? log = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = graph.NCells;
        CheckOutlets(isOutlet, n);
        bool[] mask = isOutlet;
        LawParams p = ReadLawParams(cfg);
        double[] zo = Vector(zOutlet, n, "z_outlet");
        CheckOutletValues(zo, mask);
        double[] u = Vector(uplift, n, "uplift");
        foreach (double v in u)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("uplift 에 NaN 이나 inf 가 있습니다");
            }
        }
        double[] rEff = Vector(runoffEff, n, "runoff_eff");
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(rEff[c]) || rEff[c] < 0.0)
            {
                throw new ArgumentException("runoff_eff 는 0 이상의 유한한 값이어야 합니다");
            }
            if (!mask[c] && rEff[c] <= 0.0)
            {
                throw new ArgumentException("출구가 아닌 칸의 runoff_eff 는 0 보다 커야 합니다 (유출 바닥값을 적용하세요)");
            }
        }
        double[]? inflow = extraInflow is null ? null : Vector(extraInflow, n, "extra_inflow");
        if (inflow is not null)
        {
            foreach (double v in inflow)
            {
                if (!double.IsFinite(v) || v < 0.0)
                {
                    throw new ArgumentException("extra_inflow 는 0 이상의 유한한 값이어야 합니다");
                }
            }
        }
        (double[] bottom, int nl, double[] kMult, double[] sCrit) = LayerArrays(layers, n, p.SCritDefault);
        int mIter = maxIter ?? (int)cfg.I("landscape.max_flow_iterations");
        if (mIter < 1)
        {
            throw new ArgumentException($"max_iter 는 1 이상이어야 합니다: {mIter}");
        }
        double eps = cfg.F("landscape.fill_epsilon_m");
        double eta = cfg.F("landscape.hysteresis_eta");
        double stopDz = cfg.F("landscape.stop_dz_m");
        int maxFlips = cfg.Sec("landscape").Contains("freeze_after_flips")
            ? (int)cfg.I("landscape.freeze_after_flips")
            : FreezeAfterFlips;
        if (maxFlips < 1)
        {
            throw new ArgumentException($"landscape.freeze_after_flips 는 1 이상이어야 합니다: {maxFlips}");
        }

        double[] z;
        if (zInit is null)
        {
            z = InitialSurface(graph, mask, zo, cfg);
        }
        else
        {
            z = (double[])Vector(zInit, n, "z_init").Clone();
            foreach (double v in z)
            {
                if (!double.IsFinite(v))
                {
                    throw new ArgumentException("z_init 에 NaN 이나 inf 가 있습니다");
                }
            }
        }
        for (int c = 0; c < n; c++)
        {
            if (mask[c])
            {
                z[c] = zo[c];
            }
        }

        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;
        double[] area = graph.Area;
        double[] width = new double[n];
        double[] wWater = new double[n];
        double[] wSed = new double[n];
        for (int c = 0; c < n; c++)
        {
            width[c] = Math.Sqrt(area[c]);
            wWater[c] = inflow is null ? area[c] * rEff[c] : (area[c] * rEff[c]) + inflow[c];
            wSed[c] = u[c] * area[c];
        }
        double invN = 1.0 / p.N;

        LawTable law = BuildLawTable(kMult, sCrit, invN);
        int maxDegree = Parallelism.MaxDegreeOfParallelism ?? Environment.ProcessorCount;
        Levels? levels = maxDegree > 1 && n >= LevelParallelMinCells ? new Levels(n) : null;
        double[] rDist = new double[n];
        long[]? rcv = null;
        int[] flips = new int[n];
        int nFrozen = 0;
        var history = new List<OrderedDictionary<string, object?>>();
        bool converged = false;
        int it = 0;
        double[] zNew = new double[n];
        long[] order = [];
        double[] q = [];
        double[] aUp = [];
        double[] qs = [];
        while (it < mIter)
        {
            it++;
            double[] zt = rcv is not null && HasEpsDrop(z, rcv, mask, eps) ? z : Depressions.FillEpsilon(z, nbr, mask, eps);
            int nChanged;
            if (rcv is null)
            {
                (rcv, _, nChanged) = Routing.D8Receivers(zt, nbr, dist, mask);
            }
            else
            {
                long[] prev = rcv;
                (rcv, _, _) = Routing.D8Receivers(zt, nbr, dist, mask, prev, eta);
                (nChanged, nFrozen) = FreezeKernel(zt, prev, rcv, flips, maxFlips);
            }
            order = Routing.TopoOrder(rcv);
            if (q.Length != n)
            {
                q = new double[n];
                aUp = new double[n];
                qs = new double[n];
            }
            Array.Copy(wWater, q, n);
            Array.Copy(area, aUp, n);
            Array.Copy(wSed, qs, n);
            AccumulateModule.AccumulateKernel(rcv, order, q, aUp, qs);
            for (int c = 0; c < n; c++)
            {
                qs[c] = Math.Max(qs[c], 0.0);
            }
            int nBad = ReceiverDistances(rcv, nbr, dist, rDist);
            if (nBad != 0)
            {
                throw new InvalidOperationException($"수신 셀이 이웃이 아닌 칸이 {nBad}개 있습니다 (내부 오류)");
            }
            IntegrateKernel(order, levels, rcv, rDist, zo, q, aUp, qs, u, width, bottom, nl, law, p, invN, eps, zNew);
            double maxDz = 0.0;
            for (int c = 0; c < n; c++)
            {
                double dz = Math.Abs(zNew[c] - z[c]);
                if (dz > maxDz || double.IsNaN(dz))
                {
                    maxDz = dz;
                }
            }
            (z, zNew) = (zNew, z);
            history.Add(new OrderedDictionary<string, object?>
            {
                ["iteration"] = (long)it,
                ["n_changed"] = (long)nChanged,
                ["max_dz"] = maxDz,
                ["n_frozen"] = (long)nFrozen,
            });
            log?.Invoke($"솔버 반복 {it}: 방향이 바뀐 칸 {LogText.N(nChanged)}개, 가장 크게 변한 높이 {maxDz:G4} m, 방향을 묶은 칸 {LogText.N(nFrozen)}개");
            if (nChanged == 0 && maxDz < stopDz)
            {
                converged = true;
                break;
            }
        }

        double[] slope = new double[n];
        double[] kS = new double[n];
        double[] sCritSurface = new double[n];
        SurfaceLawKernel(rcv!, nbr, dist, z, q, qs, u, bottom, nl, kMult, sCrit, p, invN, slope, kS, sCritSurface);
        bool[] frozen = new bool[n];
        for (int c = 0; c < n; c++)
        {
            frozen[c] = flips[c] >= maxFlips;
        }
        return new SolverResult
        {
            Z = z,
            Receiver = rcv!,
            Order = order,
            Discharge = q,
            DrainageArea = aUp,
            SedimentFlux = qs,
            Slope = slope,
            KS = kS,
            SCritSurface = sCritSurface,
            Iterations = it,
            History = history,
            Converged = converged,
            Seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds,
            NFrozen = nFrozen,
            Frozen = frozen,
            UpliftEffective = u,
        };
    }
}
