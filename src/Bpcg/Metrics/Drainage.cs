using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Bpcg.Numerics;

namespace Bpcg.Metrics;

/// <summary>
/// 물길 지표: Hack 법칙, 하천 연결·역류, 법칙 자기일관성, 수지, 격자 정렬 지수 (src/bpcg/metrics/drainage.py).
/// </summary>
public static class Drainage
{
    public const int AlignmentSteps = 6;
    public const double AlignmentTolDeg = 5.0;
    public const double EdgeBand = 0.9;

    private const int NSlots = CellGraph.NSlots;

    private static int BandCode(string band) => band switch
    {
        "all" => 0,
        "edge" => 1,
        "center" => 2,
        _ => throw new ArgumentException($"band 는 'all', 'edge', 'center' 가운데 하나여야 합니다: '{band}'"),
    };

    private static long[] Order(long[]? order, long[] rcv)
    {
        if (order is null)
        {
            return Routing.TopoOrder(rcv);
        }
        if (order.Length != rcv.Length)
        {
            throw new ArgumentException($"order 는 ({rcv.Length},) 정수 배열이어야 합니다");
        }
        return order;
    }

    private static void CheckRcv(long[] rcv, int? n = null)
    {
        if (n is int nn && rcv.Length != nn)
        {
            throw new ArgumentException($"receiver 길이 {rcv.Length} 가 칸 수 {nn} 와 다릅니다");
        }
        foreach (long r in rcv)
        {
            if (r < 0 || r >= rcv.Length)
            {
                throw new ArgumentException("receiver 에 범위를 벗어난 셀 번호가 있습니다");
            }
        }
    }

    /// <summary>칸마다 수신 셀까지 거리 [m] (receiver_distance, 출구 0).</summary>
    public static double[] ReceiverDistance(CellGraph graph, long[] receiver)
    {
        CheckRcv(receiver, graph.NCells);
        double[] o = new double[receiver.Length];
        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;
        double[] pos = graph.Pos;
        Parallel.For(0, receiver.Length, c =>
        {
            long r = receiver[c];
            if (r == c)
            {
                o[c] = 0.0;
                return;
            }
            double d = -1.0;
            for (int s = 0; s < NSlots; s++)
            {
                if (nbr[(c * NSlots) + s] == r)
                {
                    d = dist[(c * NSlots) + s];
                    break;
                }
            }
            if (d < 0.0)
            {
                double dx = pos[c * 3] - pos[r * 3];
                double dy = pos[(c * 3) + 1] - pos[(r * 3) + 1];
                double dz = pos[(c * 3) + 2] - pos[(r * 3) + 2];
                d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }
            o[c] = d;
        });
        return o;
    }

    private static void MainStemKernel(long[] order, long[] rcv, long[] main, bool[] output)
    {
        foreach (long c in order)
        {
            long r = rcv[c];
            output[c] = r == c || rcv[r] == r || (output[r] && main[r] == c);
        }
    }

    /// <summary>칸마다 가장 긴 흐름 경로 길이와 본류 기여 셀 (longest_flow_path).</summary>
    public static (double[] Length, long[] MainDonor) LongestFlowPath(
        CellGraph graph, long[] receiver, long[]? order = null, double[]? drainageArea = null)
    {
        int n = graph.NCells;
        CheckRcv(receiver, n);
        long[] o = Order(order, receiver);
        double[] aUp = drainageArea ?? AccumulateModule.Accumulate(receiver, o, graph.Area);
        double[] rdist = ReceiverDistance(graph, receiver);
        double[] length = new double[n];
        long[] main = new long[n];
        Array.Fill(main, -1L);
        for (int q = n - 1; q >= 0; q--)
        {
            long c = o[q];
            long r = receiver[c];
            if (r == c)
            {
                continue;
            }
            double cand = length[c] + rdist[c];
            long m = main[r];
            if (m < 0 || cand > length[r]
                || (cand == length[r] && (aUp[c] > aUp[m] || (aUp[c] == aUp[m] && c < m))))
            {
                length[r] = cand;
                main[r] = c;
            }
        }
        return (length, main);
    }

    /// <summary>본류 칸 (main_stem_mask).</summary>
    public static bool[] MainStemMask(long[] receiver, long[]? order, long[] mainDonor)
    {
        CheckRcv(receiver);
        long[] o = Order(order, receiver);
        bool[] output = new bool[receiver.Length];
        MainStemKernel(o, receiver, mainDonor, output);
        return output;
    }

    /// <summary>본류를 따라 Hack 법칙 L = c·A^h 를 맞춥니다 (hack_fit). 점이 모자라면 (NaN, NaN).</summary>
    public static (double Coefficient, double Exponent) HackFit(
        CellGraph graph, long[] receiver, long[]? order, double[] drainageArea, bool[]? mask = null, double minAreaCells = 10.0)
    {
        int n = graph.NCells;
        CheckRcv(receiver, n);
        long[] o = Order(order, receiver);
        if (!(double.IsFinite(minAreaCells) && minAreaCells > 0.0))
        {
            throw new ArgumentException($"min_area_cells 는 0 보다 커야 합니다: {minAreaCells}");
        }
        (double[] length, long[] main) = LongestFlowPath(graph, receiver, o, drainageArea);
        bool[] stem = new bool[n];
        MainStemKernel(o, receiver, main, stem);
        double aMin = minAreaCells * NpReduce.Mean(graph.Area);
        var xs = new List<double>();
        var ys = new List<double>();
        for (int c = 0; c < n; c++)
        {
            bool use = mask is null || mask[c];
            if (stem[c] && use && receiver[c] != c && drainageArea[c] >= aMin && length[c] > 0.0)
            {
                xs.Add(Math.Log10(drainageArea[c] / 1.0e6));
                ys.Add(Math.Log10(length[c] / 1.0e3));
            }
        }
        if (xs.Count < 3)
        {
            return (double.NaN, double.NaN);
        }
        double xmin = double.PositiveInfinity;
        double xmax = double.NegativeInfinity;
        foreach (double v in xs)
        {
            xmin = Math.Min(xmin, v);
            xmax = Math.Max(xmax, v);
        }
        if (xmax - xmin <= 0.0)
        {
            return (double.NaN, double.NaN);
        }
        (double h, double b) = SciPy.Polyfit1(xs.ToArray(), ys.ToArray());
        return (Math.Pow(10.0, b), h);
    }

    /// <summary>하천 칸 가운데 강 칸만 지나 바다·호수·출구에 닿는 비율 (river_reach_fraction). 하천이 없으면 NaN.</summary>
    public static double RiverReachFraction(long[] receiver, long[]? order, bool[] isRiver, bool[] isSink)
    {
        CheckRcv(receiver);
        long[] o = Order(order, receiver);
        int n = receiver.Length;
        int nRiver = 0;
        foreach (bool b in isRiver)
        {
            nRiver += b ? 1 : 0;
        }
        if (nRiver == 0)
        {
            return double.NaN;
        }
        bool[] ok = new bool[n];
        foreach (long c in o)
        {
            long r = receiver[c];
            ok[c] = isSink[c] || (r != c && isRiver[c] && ok[r]);
        }
        int hit = 0;
        for (int c = 0; c < n; c++)
        {
            if (isRiver[c] && ok[c])
            {
                hit++;
            }
        }
        return (double)hit / nRiver;
    }

    /// <summary>수신 셀의 수면이 더 높은 하천 칸 수 (river_backflow_count).</summary>
    public static long RiverBackflowCount(long[] receiver, double[] waterLevel, bool[] isRiver, double tol = 0.0)
    {
        CheckRcv(receiver);
        long bad = 0;
        for (int c = 0; c < receiver.Length; c++)
        {
            if (!isRiver[c] || receiver[c] == c)
            {
                continue;
            }
            double h0 = waterLevel[c];
            double h1 = waterLevel[receiver[c]];
            if (double.IsFinite(h0) && double.IsFinite(h1) && h1 > h0 + tol)
            {
                bad++;
            }
        }
        return bad;
    }

    /// <summary>솔버 결과 필드로 경사 법칙을 다시 계산합니다 (law_slope_from_fields). 출구는 NaN.</summary>
    public static double[] LawSlopeFromFields(
        CellGraph graph, long[] receiver, double[] discharge, double[] drainageArea, double[] sedimentFlux,
        double[] uplift, double[] kS, double[] sCrit, Config cfg)
    {
        int n = graph.NCells;
        CheckRcv(receiver, n);
        double theta = cfg.F("landscape.theta");
        double sMin = cfg.F("landscape.s_min");
        double diff = cfg.F("landscape.hillslope_diffusivity_m2_per_yr");
        double gDep = cfg.F("landscape.deposition_g");
        double eps = cfg.F("landscape.fill_epsilon_m");
        double rRef = cfg.F("climate.runoff_ref_m_per_yr");
        double[] d = ReceiverDistance(graph, receiver);
        double[] s = new double[n];
        for (int c = 0; c < n; c++)
        {
            double width = Math.Sqrt(graph.Area[c]);
            double q = discharge[c];
            double e = uplift[c] + (gDep * rRef * sedimentFlux[c] / q);
            bool good = e > 0.0 && kS[c] > 0.0 && q > 0.0;
            double inv = (NpMath.Power(q, theta) / kS[c]) + (diff / (e * (drainageArea[c] / width)));
            double v = good ? 1.0 / inv : sMin;
            v = Math.Max(Math.Min(v, sCrit[c]), sMin);
            v = Math.Max(v, eps / d[c]);
            s[c] = receiver[c] == c ? NpMath.NaN : v;
        }
        return s;
    }

    /// <summary>칸과 수신 셀 사이에서 층 경계를 넘는 칸 (law_cross_mask). strataBottom 은 (N, L).</summary>
    public static bool[] LawCrossMask(long[] receiver, double[] z, double[] strataBottom, int nLayers)
    {
        CheckRcv(receiver);
        int n = receiver.Length;
        if (strataBottom.Length != n * nLayers)
        {
            throw new ArgumentException($"strata_bottom 은 ({n}, L) 이어야 합니다");
        }
        bool[] o = new bool[n];
        Parallel.For(0, n, c =>
        {
            long r = receiver[c];
            if (r == c)
            {
                o[c] = false;
                return;
            }
            int lo = nLayers;
            for (int i = 0; i < nLayers; i++)
            {
                if (strataBottom[(c * nLayers) + i] <= z[r])
                {
                    lo = i;
                    break;
                }
            }
            o[c] = lo != Model.LayerIndexAt(strataBottom, nLayers, c, z[c]);
        });
        return o;
    }

    /// <summary>실제 경사와 법칙 경사의 상대 차이 (law_consistency): median, max, n.</summary>
    public static OrderedDictionary<string, object?> LawConsistency(double[] slope, double[] lawSlope, double[] sCrit, bool[]? mask = null)
    {
        int n = slope.Length;
        var rel = new List<double>();
        for (int c = 0; c < n; c++)
        {
            bool use = (mask is null || mask[c]) && double.IsFinite(slope[c]) && double.IsFinite(lawSlope[c])
                && lawSlope[c] > 0.0 && slope[c] < sCrit[c];
            if (use)
            {
                rel.Add(Math.Abs(slope[c] - lawSlope[c]) / lawSlope[c]);
            }
        }
        if (rel.Count == 0)
        {
            return new OrderedDictionary<string, object?> { ["median"] = double.NaN, ["max"] = double.NaN, ["n"] = 0L };
        }
        double mx = double.NegativeInfinity;
        foreach (double v in rel)
        {
            mx = Math.Max(mx, v);
        }
        return new OrderedDictionary<string, object?>
        {
            ["median"] = NpStats.Median(rel),
            ["max"] = mx,
            ["n"] = (long)rel.Count,
        };
    }

    /// <summary>흐름 수지의 상대 오차 (budget_error).</summary>
    public static double BudgetError(long[] receiver, double[] flux, double[] source, long[]? order = null, bool clipNegative = false)
    {
        CheckRcv(receiver);
        int n = receiver.Length;
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(flux[c]) || !double.IsFinite(source[c]))
            {
                throw new ArgumentException("flux 와 source 는 유한한 값이어야 합니다");
            }
        }
        long[] o = Order(order, receiver);
        double[] expect = AccumulateModule.Accumulate(receiver, o, source);
        if (clipNegative)
        {
            for (int c = 0; c < n; c++)
            {
                expect[c] = Math.Max(expect[c], 0.0);
            }
        }
        bool[] root = new bool[n];
        for (int c = 0; c < n; c++)
        {
            root[c] = receiver[c] == c;
        }
        double totalIn = clipNegative ? NpStats.SumWhere(expect, root) : NpReduce.Sum(source);
        double scale = NpReduce.Sum(NpStats.Select(Array.ConvertAll(expect, Math.Abs), root));
        if (!(scale > 0.0))
        {
            double fmax = 0.0;
            foreach (double v in flux)
            {
                fmax = Math.Max(fmax, Math.Abs(v));
            }
            return fmax == 0.0 ? 0.0 : double.PositiveInfinity;
        }
        double outlet = Math.Abs(NpStats.SumWhere(flux, root) - totalIn) / scale;
        double cellMax = double.NegativeInfinity;
        for (int c = 0; c < n; c++)
        {
            cellMax = Math.Max(cellMax, Math.Abs(flux[c] - expect[c]));
        }
        double cell = cellMax / scale;
        return NpMath.PyMax(outlet, cell);
    }

    private static (int FaceCells, int Ny, int Nx) FaceLayout(CellGraph graph)
    {
        if (graph.Kind == "sphere")
        {
            int n = (int)graph.Shape[1];
            return (n * n, n, n);
        }
        if (graph.Kind == "flat")
        {
            int ny = (int)graph.Shape[0];
            int nx = (int)graph.Shape[1];
            return (ny * nx, ny, nx);
        }
        throw new ArgumentException($"그래프 종류는 sphere 또는 flat 이어야 합니다: {graph.Kind}");
    }

    /// <summary>격자 정렬 지수의 (정렬된 칸 수, 쓴 칸 수) (alignment_counts).</summary>
    public static (long Hit, long Used) AlignmentCounts(
        CellGraph graph, long[] receiver, bool[] isRiver, string band = "all", int steps = AlignmentSteps, double tolDeg = AlignmentTolDeg)
    {
        int n = graph.NCells;
        CheckRcv(receiver, n);
        int bandCode = BandCode(band);
        if (steps < 1)
        {
            throw new ArgumentException($"steps 는 1 이상이어야 합니다: {steps}");
        }
        if (!(tolDeg > 0.0 && tolDeg < 22.5))
        {
            throw new ArgumentException($"tol_deg 는 (0, 22.5) 도여야 합니다: {tolDeg}");
        }
        (int faceCells, int ny, int nx) = FaceLayout(graph);
        sbyte[] flag = new sbyte[n];
        Parallel.For(0, n, c0 =>
        {
            flag[c0] = -1;
            if (!isRiver[c0])
            {
                return;
            }
            long c = c0;
            long f = c / faceCells;
            long rem = c - (f * faceCells);
            long j0 = rem / nx;
            long i0 = rem - (j0 * nx);
            if (bandCode != 0)
            {
                double a = -1.0 + (((2.0 * i0) + 1.0) / nx);
                double b = -1.0 + (((2.0 * j0) + 1.0) / ny);
                bool isEdge = Math.Abs(a) > EdgeBand || Math.Abs(b) > EdgeBand;
                if ((bandCode == 1) != isEdge)
                {
                    return;
                }
            }
            long cur = c;
            for (int k = 0; k < steps; k++)
            {
                long r = receiver[cur];
                if (r == cur || r / faceCells != f)
                {
                    return;
                }
                cur = r;
            }
            rem = cur - (f * faceCells);
            long j1 = rem / nx;
            long i1 = rem - (j1 * nx);
            long di = Math.Abs(i1 - i0);
            long dj = Math.Abs(j1 - j0);
            if (di == 0 && dj == 0)
            {
                return;
            }
            double ang = Math.Atan2(dj, di) * (180.0 / Math.PI);
            double dev = ang % 45.0;
            if (45.0 - dev < dev)
            {
                dev = 45.0 - dev;
            }
            flag[c0] = dev <= tolDeg ? (sbyte)1 : (sbyte)0;
        });
        long hit = 0;
        long used = 0;
        foreach (sbyte v in flag)
        {
            hit += v == 1 ? 1 : 0;
            used += v >= 0 ? 1 : 0;
        }
        return (hit, used);
    }

    /// <summary>격자 정렬 지수 (grid_alignment): 정렬 비율 / (2·tol/45). 쓴 칸이 없으면 NaN.</summary>
    public static double GridAlignment(
        CellGraph graph, long[] receiver, bool[] isRiver, string band = "all", int steps = AlignmentSteps, double tolDeg = AlignmentTolDeg)
    {
        (long hit, long used) = AlignmentCounts(graph, receiver, isRiver, band, steps, tolDeg);
        if (used == 0)
        {
            return double.NaN;
        }
        return ((double)hit / used) / (2.0 * tolDeg / 45.0);
    }
}
