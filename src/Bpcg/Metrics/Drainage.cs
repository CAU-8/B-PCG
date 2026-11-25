using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 격자 정렬 지수로 합격·불합격을 가리는 데 필요한 가장 적은 하천 칸 수. 방향이 무작위여도 지수는 칸이 적을수록 크게
    /// 흔들립니다: 표준편차 = sqrt((1 − p) ÷ (p × 칸 수)). p 는 방향이 무작위일 때 칸 차이가 가로·세로·대각선이 될 확률로,
    /// 6칸 따라간 변위가 길수록 작아집니다(laptop 프로필에서 약 0.15~0.19, AlignmentNull). p = 0.15 일 때 250칸에서
    /// 표준편차가 약 0.15 라, 합격선 1.3 과 무작위 값 1 의 차이 0.3 이 그 두 배입니다. 칸이 이보다 적으면 우연히 합격선을
    /// 넘을 수 있습니다.
    /// </summary>
    public const long AlignmentMinCells = 250;
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
        Parallelism.For(0, receiver.Length, c =>
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
        Parallelism.For(0, n, c =>
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

    /// <summary>LawConsistency 와 같은 칸에서 |S − 법칙|/법칙 이 tol 을 넘는 칸 수.</summary>
    public static long LawOverCount(double[] slope, double[] lawSlope, double[] sCrit, bool[]? mask, double tol)
    {
        long over = 0;
        for (int c = 0; c < slope.Length; c++)
        {
            bool use = (mask is null || mask[c]) && double.IsFinite(slope[c]) && double.IsFinite(lawSlope[c])
                && lawSlope[c] > 0.0 && slope[c] < sCrit[c];
            if (use && Math.Abs(slope[c] - lawSlope[c]) / lawSlope[c] > tol)
            {
                over++;
            }
        }
        return over;
    }

    /// <summary>
    /// 흐름 수지의 상대 오차 (budget_error). 두 값 가운데 큰 값입니다.
    /// <list type="bullet">
    /// <item>칸마다: |flux − 상류부터 더한 source| ÷ (상류부터 더한 |source|). 칸마다 그 칸까지 모인 양에 견주므로, 작은 개울에서
    /// 물이 새도 큰 강의 양에 묻히지 않습니다. 예: 1년에 1,000 m³ 가 모이는 칸에서 10 m³ 가 새면 0.01 입니다.</item>
    /// <item>출구 합: |출구 칸 flux 의 합 − 들어온 합| ÷ (출구 칸에서 잰 |source| 누적의 합).</item>
    /// </list>
    /// 모인 |source| 가 0 인 칸은 flux 와 기대값이 같아야 하고, 다르면 무한대입니다. clipNegative 면 기대값을 0 아래로 내리지
    /// 않습니다(가라앉는 땅의 흙 수지).
    /// </summary>
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
        double[] gross = AccumulateModule.Accumulate(receiver, o, Array.ConvertAll(source, Math.Abs));
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
        double cell = 0.0;
        for (int c = 0; c < n; c++)
        {
            double diff = Math.Abs(flux[c] - expect[c]);
            if (diff > 0.0)
            {
                cell = Math.Max(cell, gross[c] > 0.0 ? diff / gross[c] : double.PositiveInfinity);
            }
        }
        double totalIn = clipNegative ? NpStats.SumWhere(expect, root) : NpReduce.Sum(source);
        double scale = NpStats.SumWhere(gross, root);
        double gap = Math.Abs(NpStats.SumWhere(flux, root) - totalIn);
        double outlet = scale > 0.0 ? gap / scale : gap > 0.0 ? double.PositiveInfinity : 0.0;
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

    /// <summary>
    /// 방향이 고르게 무작위인 직선 변위(길이 r 칸)를 칸 번호로 반올림했을 때, 칸 차이가 가로·세로·대각선이 될 확률
    /// (alignment_null). 격자 정렬 지수의 '무작위 = 1' 기준입니다.
    /// </summary>
    /// <remarks>
    /// 가로·세로·대각선 여덟 방향과 거울 대칭 덕분에 0°~45° 만 봅니다. 끝점 (r·cos φ, r·sin φ) 의 세로 성분이 0 으로
    /// 반올림되면 가로이고(φ &lt; asin(0.5/r)), 두 성분이 같은 정수 k 로 반올림되면 대각선입니다(k 마다 두 각도 구간의 겹침).
    /// 예: r = 1 이면 1(이웃 여덟 칸은 모두 여덟 방향), r = 2 이면 약 0.40, r = 6 이면 약 0.19, r = 8 이면 약 0.11 입니다.
    /// 변위가 길수록 작아지므로, 하나의 상수(예: 5° 허용 각의 2 × 5° ÷ 45° ≈ 0.22)로 나누면 긴 강일수록 지수가 작게 나옵니다.
    /// </remarks>
    public static double AlignmentNull(double r)
    {
        if (!(r >= 1.0) || double.IsInfinity(r))
        {
            throw new ArgumentException($"r 은 1 이상의 유한한 칸 수여야 합니다: {r}");
        }
        const double octant = Math.PI / 4.0;
        double axis = Math.Asin(0.5 / r);
        double diag = 0.0;
        for (int k = 1; k - 0.5 <= r; k++)
        {
            double lo = Math.Max(AcosClip((k + 0.5) / r), AsinClip((k - 0.5) / r));
            double hi = Math.Min(Math.Min(AcosClip((k - 0.5) / r), AsinClip((k + 0.5) / r)), octant);
            diag += Math.Max(0.0, hi - lo);
        }
        return (Math.Min(axis, octant) + diag) / octant;
    }

    private static double AcosClip(double x) => x >= 1.0 ? 0.0 : Math.Acos(x);

    private static double AsinClip(double x) => x >= 1.0 ? Math.PI / 2.0 : Math.Asin(x);

    /// <summary>
    /// 격자 정렬 지수를 이루는 수 (alignment_counts): 정렬된 칸 수(칸 차이가 정확히 가로·세로·대각선), 쓴 칸 수, 방향이
    /// 무작위일 때 정렬될 칸 수의 기대값(쓴 칸마다 변위 길이의 AlignmentNull 을 더한 값).
    /// </summary>
    public static (long Hit, long Used, double Expected) AlignmentCounts(
        CellGraph graph, long[] receiver, bool[] isRiver, string band = "all", int steps = AlignmentSteps)
    {
        int n = graph.NCells;
        CheckRcv(receiver, n);
        int bandCode = BandCode(band);
        if (steps < 1)
        {
            throw new ArgumentException($"steps 는 1 이상이어야 합니다: {steps}");
        }
        (int faceCells, int ny, int nx) = FaceLayout(graph);
        // 칸 차이 (di, dj) 는 |di|, |dj| ≤ steps 인 정수라, 길이의 제곱 di² + dj² 마다 기대 확률을 미리 구해 둡니다.
        double[] pNull = new double[(2 * steps * steps) + 1];
        for (int r2 = 1; r2 < pNull.Length; r2++)
        {
            pNull[r2] = AlignmentNull(Math.Sqrt(r2));
        }
        sbyte[] flag = new sbyte[n];
        int[] lenSq = new int[n];
        Parallelism.For(0, n, c0 =>
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
            lenSq[c0] = (int)((di * di) + (dj * dj));
            flag[c0] = di == 0 || dj == 0 || di == dj ? (sbyte)1 : (sbyte)0;
        });
        long hit = 0;
        long used = 0;
        // 기대값은 칸 번호 순서로 더해 실행마다 같은 값이 나오게 합니다.
        double expected = 0.0;
        for (int c = 0; c < n; c++)
        {
            if (flag[c] >= 0)
            {
                hit += flag[c];
                used++;
                // 수신 셀이 이웃이 아니면(그런 그래프는 없지만) 표 밖의 길이라 바로 계산합니다.
                expected += lenSq[c] < pNull.Length ? pNull[lenSq[c]] : AlignmentNull(Math.Sqrt(lenSq[c]));
            }
        }
        return (hit, used, expected);
    }

    /// <summary>
    /// 격자 정렬 지수 (grid_alignment): 정렬된 칸 수 ÷ 방향이 무작위일 때의 기대값 (AlignmentCounts). 1 이면 격자 방향을
    /// 편들지 않고, 1.3 이면 무작위보다 30% 많이 따릅니다. 쓴 칸이 없으면 NaN.
    /// </summary>
    public static double GridAlignment(
        CellGraph graph, long[] receiver, bool[] isRiver, string band = "all", int steps = AlignmentSteps)
    {
        (long hit, long used, double expected) = AlignmentCounts(graph, receiver, isRiver, band, steps);
        return used == 0 ? double.NaN : hit / expected;
    }
}
