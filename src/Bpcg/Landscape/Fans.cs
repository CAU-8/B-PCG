using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Numerics;

namespace Bpcg.Landscape;

/// <summary>
/// 선상지 후처리와 물길 다시 계산 (src/bpcg/landscape/fans.py, docs/pipeline.md 7.2).
/// </summary>
public static class Fans
{
    private const int NSlots = CellGraph.NSlots;

    private static double ArcToChord(CellGraph graph, double s)
    {
        if (graph.Kind == "sphere" && double.IsFinite(s))
        {
            double r = graph.R!.Value;
            return 2.0 * r * Math.Sin(NpMath.PyMin(0.5 * s / r, 0.5 * Math.PI));
        }
        return s;
    }

    private static double ChordToArc(CellGraph graph, double chord)
    {
        if (graph.Kind == "sphere")
        {
            double r = graph.R!.Value;
            return 2.0 * r * Math.Asin(NpMath.Clip(chord / (2.0 * r), 0.0, 1.0));
        }
        return chord;
    }

    // 칸 c 에서 수신 셀까지 거리 (출구는 inf)
    private static double ReceiverDistance(CellGraph graph, long[] rcv, int c)
    {
        double d = double.PositiveInfinity;
        for (int s = 0; s < NSlots; s++)
        {
            if (graph.Nbr[(c * NSlots) + s] == rcv[c])
            {
                d = Math.Min(d, graph.Dist[(c * NSlots) + s]);
            }
        }
        return d;
    }

    /// <summary>선상지 꼭짓점 칸 번호, Q 가 큰 것부터 (find_fan_apexes).</summary>
    public static long[] FindFanApexes(CellGraph graph, SolverResult result, Config cfg)
    {
        long[] rcv = result.Receiver;
        int n = rcv.Length;
        double[] s = result.Slope;
        double[] q = result.Discharge;
        double ratio = cfg.F("fans.slope_drop_ratio");
        double qMin = cfg.F("fans.min_discharge_m3_per_yr");
        double qMax = cfg.F("fans.max_discharge_m3_per_yr");
        double radius = cfg.F("fans.radius_m");
        var cells = new List<int>();
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            bool isOut = r == c;
            bool rOut = rcv[r] == r;
            double sr = s[r];
            if (!isOut && !rOut && sr > 0.0 && s[c] >= ratio * sr && q[c] >= qMin && q[c] <= qMax
                && ReceiverDistance(graph, rcv, c) < radius)
            {
                cells.Add(c);
            }
        }
        if (cells.Count == 0)
        {
            return [];
        }
        // Q 내림차순, 같으면 번호 오름차순 (np.lexsort((cells, -Q[cells])))
        cells.Sort((a, b) =>
        {
            int cmp = (-q[a]).CompareTo(-q[b]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        double[] pts = new double[cells.Count * 3];
        for (int k = 0; k < cells.Count; k++)
        {
            Array.Copy(graph.Pos, cells[k] * 3, pts, k * 3, 3);
        }
        var tree = new KdTree(pts);
        double r2 = ArcToChord(graph, 2.0 * radius);
        bool[] suppressed = new bool[cells.Count];
        var keep = new List<long>();
        for (int k = 0; k < cells.Count; k++)
        {
            if (suppressed[k])
            {
                continue;
            }
            keep.Add(cells[k]);
            foreach (int j in tree.QueryBallPoint(graph.Pos.AsSpan(cells[k] * 3, 3), r2))
            {
                suppressed[j] = true;
            }
        }
        return keep.ToArray();
    }

    /// <summary>
    /// 산지 앞 꼭짓점에 원뿔 선상지를 얹습니다 (add_fans). 반환: (z_new, fan_mask, not_steady_mask, apexes).
    /// </summary>
    public static (double[] ZNew, bool[] Fan, bool[] NotSteady, List<object?> Apexes) AddFans(
        CellGraph graph, double[] z, SolverResult result, Config cfg)
    {
        int n = graph.NCells;
        if (z.Length != n)
        {
            throw new ArgumentException($"z 는 유한한 ({n},) 배열이어야 합니다");
        }
        foreach (double v in z)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException($"z 는 유한한 ({n},) 배열이어야 합니다");
            }
        }
        if (result.Receiver.Length != n)
        {
            throw new ArgumentException("result 의 칸 수가 그래프와 다릅니다");
        }
        double[] zNew = (double[])z.Clone();
        bool[] fan = new bool[n];
        var apexes = new List<object?>();
        if (!cfg.B("fans.enabled"))
        {
            return (zNew, fan, (bool[])fan.Clone(), apexes);
        }
        double radius = cfg.F("fans.radius_m");
        double coneSlope = cfg.F("fans.slope");
        if (!(radius > 0.0 && coneSlope >= 0.0))
        {
            throw new ArgumentException($"fans.radius_m > 0, fans.slope ≥ 0 이어야 합니다: {radius}, {coneSlope}");
        }
        long[] apexCells = FindFanApexes(graph, result, cfg);
        long[] rcv = result.Receiver;
        if (apexCells.Length == 0)
        {
            return (zNew, fan, (bool[])fan.Clone(), apexes);
        }
        var tree = new KdTree(graph.Pos);
        double rChord = ArcToChord(graph, radius);
        double[] pos = graph.Pos;
        foreach (long a in apexCells)
        {
            ReadOnlySpan<double> pa = pos.AsSpan((int)a * 3, 3);
            long ra = rcv[a];
            double vx = pos[ra * 3] - pa[0];
            double vy = pos[(ra * 3) + 1] - pa[1];
            double vz = pos[(ra * 3) + 2] - pa[2];
            int nRaised = 0;
            foreach (int i in tree.QueryBallPoint(pa, rChord))
            {
                double rx = pos[i * 3] - pa[0];
                double ry = pos[(i * 3) + 1] - pa[1];
                double rz = pos[(i * 3) + 2] - pa[2];
                double ell = ChordToArc(graph, Math.Sqrt((rx * rx) + (ry * ry) + (rz * rz)));
                bool down = (rx * vx) + (ry * vy) + (rz * vz) > 0.0;
                if (!(down && ell < radius && rcv[i] != i))
                {
                    continue;
                }
                double zCone = z[a] - (coneSlope * ell);
                if (zCone > zNew[i])
                {
                    zNew[i] = zCone;
                    fan[i] = true;
                    nRaised++;
                }
            }
            if (nRaised == 0)
            {
                continue;
            }
            apexes.Add(new OrderedDictionary<string, object?>
            {
                ["cell"] = a,
                ["receiver"] = ra,
                ["discharge_m3_per_yr"] = result.Discharge[a],
                ["z_m"] = z[a],
                ["slope_ratio"] = result.Slope[a] / result.Slope[ra],
                ["n_raised"] = (long)nRaised,
                ["pos"] = new List<object?> { pa[0], pa[1], pa[2] },
            });
        }
        return (zNew, fan, (bool[])fan.Clone(), apexes);
    }

    /// <summary>
    /// 후처리 뒤 물길만 다시 계산합니다: ε 채움 → D8 → Q (reroute). 반환: receiver, discharge_m3_per_yr,
    /// drainage_area_m2, slope.
    /// </summary>
    public static FieldSet Reroute(
        CellGraph graph, double[] z, bool[] isOutlet, double[] runoffEff, Config cfg, double[]? extraInflow = null)
    {
        int n = graph.NCells;
        if (z.Length != n || runoffEff.Length != n)
        {
            throw new ArgumentException($"z, runoff_eff 는 ({n},) 이어야 합니다");
        }
        double[] weight = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(runoffEff[c]) || runoffEff[c] < 0.0)
            {
                throw new ArgumentException("runoff_eff 는 0 이상의 유한한 값이어야 합니다");
            }
            weight[c] = graph.Area[c] * runoffEff[c];
        }
        if (extraInflow is not null)
        {
            if (extraInflow.Length != n)
            {
                throw new ArgumentException($"extra_inflow 는 0 이상의 유한한 ({n},) 배열이어야 합니다");
            }
            for (int c = 0; c < n; c++)
            {
                if (!double.IsFinite(extraInflow[c]) || extraInflow[c] < 0.0)
                {
                    throw new ArgumentException($"extra_inflow 는 0 이상의 유한한 ({n},) 배열이어야 합니다");
                }
                weight[c] += extraInflow[c];
            }
        }
        double[] zt = Depressions.FillEpsilon(z, graph.Nbr, isOutlet, cfg.F("landscape.fill_epsilon_m"));
        (long[] rcv, _, _) = Routing.D8Receivers(zt, graph.Nbr, graph.Dist, isOutlet);
        long[] order = Routing.TopoOrder(rcv);
        double[] q = AccumulateModule.Accumulate(rcv, order, weight);
        double[] aUp = AccumulateModule.Accumulate(rcv, order, graph.Area);
        double[] slope = new double[n];
        for (int c = 0; c < n; c++)
        {
            double d = ReceiverDistance(graph, rcv, c);
            slope[c] = double.IsFinite(d) ? Math.Max(z[c] - z[rcv[c]], 0.0) / d : 0.0;
        }
        return new FieldSet
        {
            ["receiver"] = rcv,
            ["discharge_m3_per_yr"] = q,
            ["drainage_area_m2"] = aUp,
            ["slope"] = slope,
        };
    }
}
