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

    public double RoutingSeconds { get; init; }

    public double AccumulationSeconds { get; init; }

    public double IntegrationSeconds { get; init; }

    public double ConvergenceSeconds { get; init; }

    public double SurfaceLawSeconds { get; init; }

    public int NFrozen { get; init; }

    public int IncrementalIterations { get; init; }

    public int FullFallbackIterations { get; init; }

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
            ["routing_seconds"] = RoutingSeconds,
            ["accumulation_seconds"] = AccumulationSeconds,
            ["integration_seconds"] = IntegrationSeconds,
            ["convergence_seconds"] = ConvergenceSeconds,
            ["other_seconds"] = Math.Max(0.0, Seconds - RoutingSeconds - AccumulationSeconds
                - IntegrationSeconds - ConvergenceSeconds),
            ["surface_law_seconds"] = SurfaceLawSeconds,
            ["final_n_changed"] = nChanged,
            ["final_max_dz_m"] = maxDz,
            ["n_frozen"] = (long)NFrozen,
            ["incremental_iterations"] = (long)IncrementalIterations,
            ["full_fallback_iterations"] = (long)FullFallbackIterations,
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

    // 칸 수를 셀 때 나누는 조각 크기. 조각 경계가 칸 수로만 정해져 실행 수와 상관없이 같은 값이 나옵니다.
    private const int CountChunk = 8192;

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

    // ks: 층 배율을 곱한 침식 계수 (ksRef·kmult^(−1/n)). 나머지는 지난 판과 같은 식입니다.
    private static double LawSlope(double ks, double qt, double hill, double scrit, double sMin)
    {
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

    private static double ReceiverSlotDistance(int c, int r, int[] nbr, double[] dist)
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

    /// <summary>
    /// 칸마다 고도 적분에 쓰는 값 가운데 고도와 상관없는 것 (받는 칸까지 거리 d, 침식률 e, qt = Q^θ, hill, ksRef).
    /// 수신 셀이나 Q·A·Qs 가 바뀐 칸만 다시 계산합니다. 식과 연산 순서는 고치기 전의 IntegrateCell 과 같아서
    /// 값이 비트까지 같습니다. 층 배율 kmult^(−1/n) 과 S_crit 은 <see cref="LawTable"/> 이 맡습니다.
    /// </summary>
    private sealed class CellLaw(int n)
    {
        public readonly double[] D = new double[n];
        public readonly double[] E = new double[n];
        public readonly double[] Qt = new double[n];
        public readonly double[] Hill = new double[n];
        public readonly double[] KsRef = new double[n];

        public void Update(int c, int[] rcv, int[] nbr, double[] dist, double[] q, double[] aUp, double[] qs,
            double[] u, double[] width, in LawParams p, double invN)
        {
            int r = rcv[c];
            if (r == c)
            {
                return;
            }
            D[c] = ReceiverSlotDistance(c, r, nbr, dist);
            double e = u[c] + (p.GDep * p.RRef * qs[c] / q[c]);
            E[c] = e;
            if (e <= 0.0)
            {
                return;
            }
            Qt[c] = Math.Exp(p.Theta * Math.Log(q[c]));
            double a = aUp[c] / width[c];
            Hill[c] = p.Diff / (e * a);
            KsRef[c] = p.KRef * Math.Pow(e / p.URef, invN);
        }
    }

    // 고도 적분: 출구에서 상류로 칸마다 높이를 쌓습니다. levels 가 있으면 출구에서 같은 칸 수만큼 떨어진
    // 칸끼리 여러 코어로 쌓습니다(Parallelism.ForLevels). 칸마다 자기 높이(와 dirty 표시)만 쓰고 아래 칸 높이만
    // 읽으므로 어느 쪽이든 결과가 같습니다.
    private static void IntegrateKernel(
        int[] order, Levels? levels, int[] rcv, CellLaw law, LawTable table, double[] zOutlet, double[] bottom, int nl,
        in LawParams p, double riseMin, double[] zNew, bool[]? dirty, double[] zOld)
    {
        LawParams pp = p;
        if (levels is null)
        {
            foreach (int c in order)
            {
                IntegrateIfDirty(c, dirty, zOld, rcv, law, table, zOutlet, bottom, nl, pp, riseMin, zNew);
            }
            return;
        }
        int nLevels = levels.Build(order, rcv);
        Parallelism.ForLevels(levels.Start, nLevels, levels.Cells, c => IntegrateIfDirty(c, dirty, zOld, rcv, law,
            table, zOutlet, bottom, nl, pp, riseMin, zNew));
    }

    private static void IntegrateIfDirty(
        int c, bool[]? dirty, double[] zOld, int[] rcv, CellLaw law, LawTable table, double[] zOutlet, double[] bottom,
        int nl, in LawParams p, double riseMin, double[] zNew)
    {
        if (dirty is not null && !dirty[c]
            && (rcv[c] == c || zNew[rcv[c]] == zOld[rcv[c]]))
        {
            zNew[c] = zOld[c];
            return;
        }
        if (dirty is not null)
        {
            dirty[c] = true;
        }
        IntegrateCell(c, rcv, law, table, zOutlet, bottom, nl, p, riseMin, zNew);
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
        public int Build(int[] order, int[] rcv)
        {
            int nLevels = 0;
            foreach (int c in order)
            {
                int r = rcv[c];
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
            foreach (int c in order)
            {
                Cells[_fill[_level[c]]++] = c;
            }
            return nLevels;
        }
    }

    private static int MarkPath(int start, int[] receiver, bool[] marked)
    {
        int added = 0;
        int c = start;
        while (true)
        {
            if (marked[c])
            {
                break;
            }
            marked[c] = true;
            added++;
            int next = receiver[c];
            if (next == c)
            {
                break;
            }
            c = next;
        }
        return added;
    }

    // dirty 의 모든 칸을 새로 씁니다 (미리 비울 필요 없음).
    private static int BuildHydrologyDirty(int[] oldReceiver, int[] receiver, bool[] changed, bool[] dirty,
        bool[] oldPath, bool[] newPath)
    {
        Array.Clear(oldPath);
        Array.Clear(newPath);
        int count = 0;
        for (int c = 0; c < changed.Length; c++)
        {
            if (changed[c])
            {
                MarkPath(c, oldReceiver, oldPath);
                MarkPath(c, receiver, newPath);
            }
        }
        for (int c = 0; c < dirty.Length; c++)
        {
            dirty[c] = oldPath[c] || newPath[c];
            if (dirty[c])
            {
                count++;
            }
        }
        return count;
    }

    // Q·A·Qs 세 필드를 한 번에 증분 누적합니다. 필드마다 더하는 순서는 필드를 하나씩 세 번 돈 것과 같고,
    // 기여 셀 정렬은 칸마다 한 번만 합니다. valueChanged 는 세 값 가운데 하나라도 바뀐 칸입니다.
    private static void AccumulateDirty3(int[] donorStart, int[] donors, int[] order,
        int[] topologicalPosition, int[] donorScratch, bool[] dirty, bool[] valueChanged,
        double[] w1, double[] a1, double[] w2, double[] a2, double[] w3, double[] a3)
    {
        // Match the full reverse-topological accumulation order exactly. A DFS topological order
        // does not in general order sibling donors by cell id, so sort this small donor set by rank.
        for (int k = order.Length - 1; k >= 0; k--)
        {
            int c = order[k];
            if (!dirty[c])
            {
                continue;
            }
            int donorCount = donorStart[c + 1] - donorStart[c];
            for (int i = 0; i < donorCount; i++)
            {
                donorScratch[i] = donors[donorStart[c] + i];
            }
            for (int i = 1; i < donorCount; i++)
            {
                int donor = donorScratch[i];
                int j = i - 1;
                while (j >= 0 && topologicalPosition[donorScratch[j]] < topologicalPosition[donor])
                {
                    donorScratch[j + 1] = donorScratch[j];
                    j--;
                }
                donorScratch[j + 1] = donor;
            }
            double v1 = w1[c];
            double v2 = w2[c];
            double v3 = w3[c];
            for (int i = 0; i < donorCount; i++)
            {
                int d = donorScratch[i];
                v1 += a1[d];
                v2 += a2[d];
                v3 += a3[d];
            }
            if (v1 != a1[c] || v2 != a2[c] || v3 != a3[c])
            {
                valueChanged[c] = true;
            }
            a1[c] = v1;
            a2[c] = v2;
            a3[c] = v3;
        }
    }

    // dirty 의 모든 칸을 새로 씁니다 (미리 비울 필요 없음).
    private static int BuildIntegrationDirty(bool[] receiverChanged, bool[] hydrologyChanged, bool[] dirty)
    {
        int count = 0;
        for (int c = 0; c < dirty.Length; c++)
        {
            dirty[c] = receiverChanged[c] || hydrologyChanged[c];
            if (dirty[c])
            {
                count++;
            }
        }
        return count;
    }

    private static int BuildIntegrationDependencyUpperBound(int[] receiver, int[] order,
        bool[] receiverChanged, bool[] hydrologyDirty, bool[] dirty)
    {
        int count = 0;
        for (int k = 0; k < order.Length; k++)
        {
            int c = order[k];
            int downstream = receiver[c];
            bool affected = receiverChanged[c] || hydrologyDirty[c]
                || (downstream != c && dirty[downstream]);
            dirty[c] = affected;
            if (affected)
            {
                count++;
            }
        }
        return count;
    }

    private static void IntegrateCell(
        int c, int[] rcv, CellLaw law, LawTable table, double[] zOutlet, double[] bottom, int nl, in LawParams p,
        double riseMin, double[] zNew)
    {
        int nr = nl + 1;
        int r = rcv[c];
        if (r == c)
        {
            zNew[c] = zOutlet[c];
            return;
        }
        double d = law.D[c];
        if (!double.IsFinite(d))
        {
            d = 0.0;
        }
        double z = zNew[r];
        double zFloor = z + riseMin;
        double e = law.E[c];
        if (e <= 0.0)
        {
            z += p.SMin * d;
            zNew[c] = z >= zFloor ? z : zFloor;
            return;
        }
        double qt = law.Qt[c];
        double hill = law.Hill[c];
        double ksRef = law.KsRef[c];
        double rem = d;
        while (true)
        {
            int i = UpperLayer(bottom, nl, c, z);
            int m = table.Slot((c * nr) + i);
            double s = LawSlope(ksRef * table.KPow[m], qt, hill, table.SCrit[m], p.SMin);
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

    private static void SurfaceLawKernel(
        int[] rcv, int[] nbr, double[] dist, double[] z, double[] q, double[] qs, double[] u, double[] bottom, int nl,
        double[] kMult, double[] sCrit, in LawParams p, double invN, double[] slope, double[] kS, double[] sCritOut)
    {
        int nr = nl + 1;
        LawParams pp = p;
        Parallelism.For(0, rcv.Length, c =>
        {
            int i = Model.LayerIndexAt(bottom, nl, c, z[c]);
            sCritOut[c] = sCrit[(c * nr) + i];
            int r = rcv[c];
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

    // 16번 넘게 뒤집힌 칸은 옛 방향에 묶고, 지난 반복과 수신 셀이 다른 칸을 changed 에 적습니다(changed 의 모든 칸을 새로 씀).
    // 칸마다 자기 값만 바꾸므로 조각으로 나눠 돌리고 조각 순서로 셉니다. 반환한 NChanged 는 changed 가 참인 칸 수와 같습니다.
    private static (int NChanged, int NFrozen) FreezeKernel(double[] zt, int[] prev, int[] rcv, int[] flips, int maxFlips,
        bool[] changed)
    {
        int n = rcv.Length;
        int[] changedCount = new int[(n + CountChunk - 1) / CountChunk];
        int[] frozenCount = new int[changedCount.Length];
        Parallelism.ForChunks(0, n, CountChunk, (lo, hi) =>
        {
            int nChanged = 0;
            int nFrozen = 0;
            for (int c = lo; c < hi; c++)
            {
                int p = prev[c];
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
                changed[c] = rcv[c] != p;
                if (flips[c] >= maxFlips)
                {
                    nFrozen++;
                }
            }
            changedCount[lo / CountChunk] = nChanged;
            frozenCount[lo / CountChunk] = nFrozen;
        });
        int totalChanged = 0;
        int totalFrozen = 0;
        for (int j = 0; j < changedCount.Length; j++)
        {
            totalChanged += changedCount[j];
            totalFrozen += frozenCount[j];
        }
        return (totalChanged, totalFrozen);
    }

    private static bool HasEpsDrop(double[] z, int[] rcv, bool[] isOutlet, double eps)
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
        Config cfg, double[]? zInit = null, double[]? extraInflow = null, int? maxIter = null, Action<string>? log = null,
        bool useIncremental = true, bool verifyIncremental = false)
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
        LawTable table = BuildLawTable(kMult, sCrit, invN);
        var law = new CellLaw(n);

        int maxDegree = Parallelism.MaxDegreeOfParallelism ?? Environment.ProcessorCount;
        Levels? levels = maxDegree > 1 && n >= LevelParallelMinCells ? new Levels(n) : null;
        // 수신 셀은 두 버퍼를 번갈아 씁니다: 이번 반복은 한쪽에 쓰고, 다른 쪽은 지난 반복의 수신 셀로 남습니다.
        // 기여 셀 CSR·위상 순서·스택도 반복마다 같은 버퍼를 다시 씁니다 (반복마다 새 배열을 만들지 않음).
        int[] rcvA = new int[n];
        int[] rcvB = new int[n];
        int[]? rcv = null;
        int[] donorStart = new int[n + 1];
        int[] donors = new int[n];
        int[] donorFill = new int[n];
        int[] topoStack = new int[n];
        int[] order = new int[n];
        int[] flips = new int[n];
        int nFrozen = 0;
        var history = new List<OrderedDictionary<string, object?>>();
        bool converged = false;
        int it = 0;
        double routingSeconds = 0.0;
        double accumulationSeconds = 0.0;
        double integrationSeconds = 0.0;
        double convergenceSeconds = 0.0;
        double[] zNew = new double[n];
        double[] q = new double[n];
        double[] aUp = new double[n];
        double[] qs = new double[n];
        double[]? fullGridVerificationZ = verifyIncremental ? new double[n] : null;
        bool[] changedMask = new bool[n];
        bool[] hydrologyDirty = new bool[n];
        bool[] integrationDirty = new bool[n];
        bool[] hydrologyValueChanged = new bool[n];
        bool[] oldPathDirty = new bool[n];
        bool[] newPathDirty = new bool[n];
        int[] topologicalPosition = new int[n];
        int[] donorScratch = new int[CellGraph.NSlots];
        int incrementalIterations = 0;
        int fallbackIterations = 0;
        while (it < mIter)
        {
            it++;
            long iterationStart = Stopwatch.GetTimestamp();
            long stageStart = Stopwatch.GetTimestamp();
            double[] zt = rcv is not null && HasEpsDrop(z, rcv, mask, eps) ? z : Depressions.FillEpsilon(z, nbr, mask, eps);
            int nChanged;
            int[]? oldReceiver = rcv;
            bool[] receiverChanged = changedMask;
            int changedReceiverCells;
            if (oldReceiver is null)
            {
                rcv = rcvA;
                Routing.D8IntoTrusted(zt, nbr, dist, mask, null, eta, rcv);
                nChanged = n;
                Array.Fill(receiverChanged, true);
                changedReceiverCells = n;
            }
            else
            {
                rcv = ReferenceEquals(oldReceiver, rcvA) ? rcvB : rcvA;
                Routing.D8IntoTrusted(zt, nbr, dist, mask, oldReceiver, eta, rcv);
                (nChanged, nFrozen) = FreezeKernel(zt, oldReceiver, rcv, flips, maxFlips, receiverChanged);
                changedReceiverCells = nChanged;
            }
            Routing.DonorCsrInto(rcv, donorStart, donors, donorFill);
            int nOrdered = Routing.TopoInto(rcv, donorStart, donors, topoStack, order, out _);
            if (nOrdered != n)
            {
                throw new ArgumentException($"수신 셀에 순환이 있어 출구에 닿지 않는 칸이 {n - nOrdered}개 있습니다");
            }
            double iterRoutingSeconds = Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
            routingSeconds += iterRoutingSeconds;

            bool incremental = useIncremental && oldReceiver is not null;
            bool integrationIncremental = incremental;
            int dirtyHydrologyCells = n;
            int dirtyIntegrationCells = n;
            if (incremental)
            {
                dirtyHydrologyCells = BuildHydrologyDirty(oldReceiver!, rcv, receiverChanged, hydrologyDirty,
                    oldPathDirty, newPathDirty);
                incremental = dirtyHydrologyCells <= (int)(n * 0.75);
                integrationIncremental = incremental;
                if (integrationIncremental)
                {
                    int dependencyUpperBound = BuildIntegrationDependencyUpperBound(rcv, order,
                        receiverChanged, hydrologyDirty, integrationDirty);
                    integrationIncremental = dependencyUpperBound <= (int)(n * 0.75);
                }
            }

            stageStart = Stopwatch.GetTimestamp();
            if (incremental)
            {
                for (int k = 0; k < n; k++)
                {
                    topologicalPosition[order[k]] = k;
                }
                Array.Clear(hydrologyValueChanged);
                AccumulateDirty3(donorStart, donors, order, topologicalPosition, donorScratch,
                    hydrologyDirty, hydrologyValueChanged, wWater, q, area, aUp, wSed, qs);
                if (verifyIncremental)
                {
                    VerifyAccumulation(it, "유량", rcv, order, wWater, q);
                    VerifyAccumulation(it, "집수 면적", rcv, order, area, aUp);
                    VerifyAccumulation(it, "퇴적물 유량", rcv, order, wSed, qs);
                }
            }
            else
            {
                AccumulateModule.Accumulate3IntoTrusted(rcv, order, wWater, q, area, aUp, wSed, qs);
            }
            for (int c = 0; c < n; c++)
            {
                qs[c] = Math.Max(qs[c], 0.0);
            }
            double iterAccumulationSeconds = Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
            accumulationSeconds += iterAccumulationSeconds;

            if (integrationIncremental)
            {
                dirtyIntegrationCells = BuildIntegrationDirty(receiverChanged, hydrologyValueChanged,
                    integrationDirty);
                integrationIncremental = dirtyIntegrationCells <= (int)(n * 0.75);
            }

            stageStart = Stopwatch.GetTimestamp();
            // 적분용 칸 값은 수신 셀이나 Q·A·Qs 가 바뀐 칸만 다시 계산합니다. 누적을 처음부터 했으면 모든 칸입니다.
            // 수신 셀이 이웃이 아닌 칸(내부 오류)도 여기서 셉니다. 다시 계산하지 않은 칸은 지난 반복에 이미 확인했습니다.
            int[] rcvNow = rcv;
            bool[]? lawDirty = incremental ? hydrologyValueChanged : null;
            LawParams pLaw = p;
            int[] badCount = new int[(n + CountChunk - 1) / CountChunk];
            Parallelism.ForChunks(0, n, CountChunk, (start, end) =>
            {
                int bad = 0;
                for (int c = start; c < end; c++)
                {
                    if (lawDirty is null || lawDirty[c] || receiverChanged[c])
                    {
                        law.Update(c, rcvNow, nbr, dist, q, aUp, qs, u, width, pLaw, invN);
                        if (rcvNow[c] != c && !double.IsFinite(law.D[c]))
                        {
                            bad++;
                        }
                    }
                }
                badCount[start / CountChunk] = bad;
            });
            int nBad = 0;
            foreach (int b in badCount)
            {
                nBad += b;
            }
            // 층별 병렬은 다시 쌓는 칸이 많을 때만 이득입니다. 증분 경로에서 다시 쌓을 칸이 적으면(행성처럼 출구가 많고
            // 바뀌는 칸이 몇 %일 때) 반복마다 층 표를 만드는 비용이 더 커서 한 줄로 쌓습니다. 어느 쪽이든 결과는 같습니다.
            Levels? levelsNow = integrationIncremental && dirtyIntegrationCells < n / 4 ? null : levels;
            IntegrateKernel(order, levelsNow, rcv, law, table, zo, bottom, nl, p, eps, zNew,
                integrationIncremental ? integrationDirty : null, z);
            if (integrationIncremental)
            {
                dirtyIntegrationCells = 0;
                for (int c = 0; c < n; c++)
                {
                    dirtyIntegrationCells += integrationDirty[c] ? 1 : 0;
                }
            }
            if (incremental && integrationIncremental)
            {
                incrementalIterations++;
            }
            else
            {
                fallbackIterations++;
            }
            if (!integrationIncremental)
            {
                dirtyIntegrationCells = n;
            }
            double iterIntegrationSeconds = Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
            integrationSeconds += iterIntegrationSeconds;
            if (verifyIncremental)
            {
                IntegrateKernel(order, levels, rcv, law, table, zo, bottom, nl, p, eps, fullGridVerificationZ!,
                    dirty: null, zOld: z);
                for (int c = 0; c < n; c++)
                {
                    if (zNew[c] != fullGridVerificationZ![c])
                    {
                        throw new InvalidOperationException($"증분 고도 검증 실패: iteration={it}, cell={c}, expected={fullGridVerificationZ![c]:G17}, actual={zNew[c]:G17}");
                    }
                }
            }
            if (nBad != 0)
            {
                throw new InvalidOperationException($"수신 셀이 이웃이 아닌 칸이 {nBad}개 있습니다 (내부 오류)");
            }
            double maxDz = 0.0;
            double sumDz = 0.0;
            int elevationChangedCells = 0;
            stageStart = Stopwatch.GetTimestamp();
            for (int c = 0; c < n; c++)
            {
                double dz = Math.Abs(zNew[c] - z[c]);
                sumDz += dz;
                if (dz != 0.0)
                {
                    elevationChangedCells++;
                }
                if (dz > maxDz || double.IsNaN(dz))
                {
                    maxDz = dz;
                }
            }
            (z, zNew) = (zNew, z);
            double iterConvergenceSeconds = Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
            convergenceSeconds += iterConvergenceSeconds;
            history.Add(new OrderedDictionary<string, object?>
            {
                ["iteration"] = (long)it,
                ["n_changed"] = (long)nChanged,
                ["receiver_changed_cells"] = (long)changedReceiverCells,
                ["receiver_changed_fraction"] = (double)changedReceiverCells / n,
                ["max_dz"] = maxDz,
                ["mean_dz"] = sumDz / n,
                ["elevation_changed_cells"] = (long)elevationChangedCells,
                ["dirty_hydrology_cells"] = (long)dirtyHydrologyCells,
                ["dirty_integration_cells"] = (long)dirtyIntegrationCells,
                ["routing_seconds"] = iterRoutingSeconds,
                ["accumulation_seconds"] = iterAccumulationSeconds,
                ["integration_seconds"] = iterIntegrationSeconds,
                ["convergence_seconds"] = iterConvergenceSeconds,
                ["other_seconds"] = Math.Max(0.0, Stopwatch.GetElapsedTime(iterationStart).TotalSeconds
                    - iterRoutingSeconds - iterAccumulationSeconds - iterIntegrationSeconds
                    - iterConvergenceSeconds),
                ["incremental"] = incremental && integrationIncremental,
                ["n_frozen"] = (long)nFrozen,
            });
            log?.Invoke($"솔버 반복 {it}: 방향 변화 {nChanged}, 최대 고도 변화 {maxDz:G4} m, 고정 칸 {nFrozen}");
            if (nChanged == 0 && maxDz < stopDz)
            {
                converged = true;
                break;
            }
        }

        long surfaceStart = Stopwatch.GetTimestamp();
        double[] slope = new double[n];
        double[] kS = new double[n];
        double[] sCritSurface = new double[n];
        SurfaceLawKernel(rcv!, nbr, dist, z, q, qs, u, bottom, nl, kMult, sCrit, p, invN, slope, kS, sCritSurface);
        bool[] frozen = new bool[n];
        long[] receiverOut = new long[n];
        long[] orderOut = new long[n];
        for (int c = 0; c < n; c++)
        {
            frozen[c] = flips[c] >= maxFlips;
            receiverOut[c] = rcv![c];
            orderOut[c] = order[c];
        }
        return new SolverResult
        {
            Z = z,
            Receiver = receiverOut,
            Order = orderOut,
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
            RoutingSeconds = routingSeconds,
            AccumulationSeconds = accumulationSeconds,
            IntegrationSeconds = integrationSeconds,
            ConvergenceSeconds = convergenceSeconds,
            SurfaceLawSeconds = Stopwatch.GetElapsedTime(surfaceStart).TotalSeconds,
            IncrementalIterations = incrementalIterations,
            FullFallbackIterations = fallbackIterations,
        };
    }

    private static void VerifyAccumulation(int iteration, string field, int[] receiver, int[] order,
        double[] weight, double[] actual)
    {
        double[] expected = (double[])weight.Clone();
        AccumulateModule.AccumulateKernel(receiver, order, expected);
        for (int c = 0; c < expected.Length; c++)
        {
            if (expected[c] != actual[c])
            {
                throw new InvalidOperationException($"증분 {field} 검증 실패: iteration={iteration}, cell={c}, expected={expected[c]:G17}, actual={actual[c]:G17}");
            }
        }
    }
}
