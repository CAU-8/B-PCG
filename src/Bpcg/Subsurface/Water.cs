using System;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 물: 호수·강·수면과 골짜기 깊이 (src/bpcg/subsurface/water.py, docs/pipeline.md 8.1).
/// </summary>
public static class Water
{
    public const double LakeMinDepthM = 1e-3;
    public const double RiverSurfaceDepthFraction = 0.2;
    public const double WidthExponent = 0.5;
    public const double DepthExponent = 0.4;
    public const double SeaLevelM = 0.0;

    public const byte KindNone = 0;
    public const byte KindOcean = 1;
    public const byte KindLake = 2;
    public const byte KindRiver = 3;

    internal static double[] AsVector(double[] x, int n, string name, bool allowScalar = false)
    {
        double[] a = x;
        if (allowScalar && x.Length == 1 && n != 1)
        {
            a = new double[n];
            Array.Fill(a, x[0]);
        }
        if (a.Length != n)
        {
            throw new ArgumentException($"{name} 는 ({n},) 배열이어야 합니다 (받은 길이: {a.Length})");
        }
        foreach (double v in a)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException($"{name} 에 NaN 이나 inf 가 있습니다");
            }
        }
        return a;
    }

    internal static bool[] AsMask(bool[] x, int n, string name) =>
        x.Length == n ? x : throw new ArgumentException($"{name} 는 ({n},) bool 배열이어야 합니다");

    /// <summary>물길 결과 (receiver, order, discharge) 를 확인하고 order 가 없으면 다시 구합니다 (routing_from_result).</summary>
    public static (long[] Rcv, long[] Order, double[] Q) RoutingFromResult(long[] receiver, long[]? order, double[] discharge, int n)
    {
        if (receiver.Length != n || discharge.Length != n)
        {
            throw new ArgumentException($"result 의 receiver·discharge 모양이 칸 수 ({n},) 와 다릅니다");
        }
        foreach (long r in receiver)
        {
            if (r < 0 || r >= n)
            {
                throw new ArgumentException("result 의 receiver 에 범위를 벗어난 셀 번호가 있습니다");
            }
        }
        foreach (double q in discharge)
        {
            if (!double.IsFinite(q) || q < 0.0)
            {
                throw new ArgumentException("result 의 discharge 는 0 이상의 유한한 값이어야 합니다");
            }
        }
        return (receiver, order ?? Routing.TopoOrder(receiver), discharge);
    }

    private static void WaterLevelKernel(long[] order, long[] rcv, byte[] kind, double[] h)
    {
        int n = order.Length;
        for (int k = n - 1; k >= 0; k--)
        {
            long c = order[k];
            long r = rcv[c];
            if (r == c)
            {
                continue;
            }
            if ((kind[c] == KindRiver || kind[c] == KindLake) && kind[r] == KindRiver && h[c] < h[r])
            {
                h[r] = h[c];
            }
        }
        for (int k = 0; k < n; k++)
        {
            long c = order[k];
            long r = rcv[c];
            if (r == c || kind[c] != KindRiver || kind[r] == KindNone)
            {
                continue;
            }
            if (h[r] > h[c])
            {
                h[c] = h[r];
            }
        }
    }

    private static double WindowChordSq(CellGraph graph, double windowM)
    {
        if (graph.Kind == "sphere")
        {
            double r = graph.R!.Value;
            if (windowM >= Math.PI * r)
            {
                return double.PositiveInfinity;
            }
            double chord = 2.0 * r * Math.Sin(0.5 * windowM / r);
            return chord * chord;
        }
        return windowM * windowM;
    }

    /// <summary>칸 cells 마다 반경 windowM 안 칸들의 최고 고도 (window_max, 그래프 이웃을 따라 퍼뜨림).</summary>
    public static double[] WindowMax(CellGraph graph, double[] z, long[] cells, double windowM)
    {
        int n = graph.NCells;
        double[] zz = AsVector(z, n, "z");
        if (!(double.IsFinite(windowM) && windowM >= 0.0))
        {
            throw new ArgumentException($"window_m 은 0 이상의 유한한 값이어야 합니다: {windowM}");
        }
        foreach (long c in cells)
        {
            if (c < 0 || c >= n)
            {
                throw new ArgumentException($"cells 는 0..{n - 1} 이어야 합니다");
            }
        }
        int m = cells.Length;
        double[] output = new double[m];
        if (m == 0)
        {
            return output;
        }
        double r2 = WindowChordSq(graph, windowM);
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        const int nSlots = CellGraph.NSlots;
        int nChunks = Math.Max(1, Math.Min(Environment.ProcessorCount, m));
        Parallelism.For(0, nChunks, ch =>
        {
            int lo = (int)((long)ch * m / nChunks);
            int hi = (int)((long)(ch + 1) * m / nChunks);
            long[] stamp = new long[n];
            Array.Fill(stamp, -1L);
            long[] queue = new long[1024];
            for (int k = lo; k < hi; k++)
            {
                long c0 = cells[k];
                double x0 = pos[c0 * 3];
                double y0 = pos[(c0 * 3) + 1];
                double w0 = pos[(c0 * 3) + 2];
                stamp[c0] = k;
                queue[0] = c0;
                int head = 0;
                int tail = 1;
                double best = zz[c0];
                while (head < tail)
                {
                    long u = queue[head];
                    head++;
                    for (int s = 0; s < nSlots; s++)
                    {
                        int v = nbr[(u * nSlots) + s];
                        if (v < 0 || stamp[v] == k)
                        {
                            continue;
                        }
                        stamp[v] = k;
                        double dx = pos[v * 3] - x0;
                        double dy = pos[(v * 3) + 1] - y0;
                        double dw = pos[(v * 3) + 2] - w0;
                        if ((dx * dx) + (dy * dy) + (dw * dw) > r2)
                        {
                            continue;
                        }
                        if (zz[v] > best)
                        {
                            best = zz[v];
                        }
                        if (tail == queue.Length)
                        {
                            Array.Resize(ref queue, queue.Length * 2);
                        }
                        queue[tail] = v;
                        tail++;
                    }
                }
                output[k] = best;
            }
        });
        return output;
    }

    /// <summary>골짜기 깊이 = (강 칸 반경 안 최고 z) − h_w, 다른 칸은 가장 가까운 강의 값 (valley_depth). 강이 없으면 NaN.</summary>
    public static double[] ValleyDepth(CellGraph graph, double[] z, double[] waterLevel, bool[] isRiver, double windowM)
    {
        int n = graph.NCells;
        double[] zz = AsVector(z, n, "z");
        AsMask(isRiver, n, "is_river");
        if (waterLevel.Length != n)
        {
            throw new ArgumentException($"water_level 은 ({n},) 배열이어야 합니다");
        }
        var cellList = new System.Collections.Generic.List<long>();
        for (int c = 0; c < n; c++)
        {
            if (isRiver[c])
            {
                if (!double.IsFinite(waterLevel[c]))
                {
                    throw new ArgumentException("강 칸의 water_level 에 NaN 이나 inf 가 있습니다");
                }
                cellList.Add(c);
            }
        }
        double[] vd = new double[n];
        Array.Fill(vd, NpMath.NaN);
        if (cellList.Count == 0)
        {
            return vd;
        }
        long[] cells = cellList.ToArray();
        double[] rim = WindowMax(graph, zz, cells, windowM);
        for (int k = 0; k < cells.Length; k++)
        {
            vd[cells[k]] = Math.Max(rim[k] - waterLevel[cells[k]], 0.0);
        }
        (_, _, double[] output) = Distance.NearestSourceValues(graph, isRiver, vd);
        return output;
    }

    /// <summary>
    /// 호수·강·수면·골짜기 깊이 (water_bodies). 반환: water_level_m, is_lake, is_river, river_width_m, river_depth_m, valley_depth_m.
    /// receiver·order·discharge 는 솔버 결과 또는 reroute 결과(order 없으면 null).
    /// </summary>
    public static FieldSet WaterBodies(
        CellGraph graph, double[] z, long[] receiver, long[]? order, double[] discharge, bool[] isOcean, Config cfg)
    {
        int n = graph.NCells;
        double[] zz = AsVector(z, n, "z");
        bool[] ocean = AsMask(isOcean, n, "is_ocean");
        (long[] rcv, long[] ord, double[] q) = RoutingFromResult(receiver, order, discharge, n);
        double qMin = cfg.F("rivers.min_discharge_m3_per_s");
        double kW = cfg.F("rivers.width_coefficient");
        double kD = cfg.F("rivers.depth_coefficient");
        if (!(qMin >= 0.0 && kW > 0.0 && kD > 0.0))
        {
            throw new ArgumentException(
                $"rivers.min_discharge_m3_per_s ≥ 0, width_coefficient > 0, depth_coefficient > 0 이어야 합니다: {qMin}, {kW}, {kD}");
        }
        double window = cfg.F("caves.valley_window_m");
        bool[] isOutlet = new bool[n];
        for (int c = 0; c < n; c++)
        {
            isOutlet[c] = ocean[c] || rcv[c] == c;
        }
        double[] zHat = Depressions.FillDepressions(zz, graph.Nbr, isOutlet);
        bool[] isLake = new bool[n];
        bool[] isRiver = new bool[n];
        double[] width = new double[n];
        double[] depth = new double[n];
        byte[] kind = new byte[n];
        double[] h = new double[n];
        for (int c = 0; c < n; c++)
        {
            bool land = !ocean[c];
            isLake[c] = land && zHat[c] - zz[c] > LakeMinDepthM;
            double qS = q[c] / Constants.SecondsPerYear;
            isRiver[c] = land && !isLake[c] && qS >= qMin;
            width[c] = isRiver[c] ? kW * NpMath.Power(qS, WidthExponent) : 0.0;
            depth[c] = isRiver[c] ? kD * NpMath.Power(qS, DepthExponent) : 0.0;
            kind[c] = ocean[c] ? KindOcean : KindNone;
            if (isLake[c])
            {
                kind[c] = KindLake;
            }
            if (isRiver[c])
            {
                kind[c] = KindRiver;
            }
            h[c] = NpMath.NaN;
            if (ocean[c])
            {
                h[c] = SeaLevelM;
            }
            if (isLake[c])
            {
                h[c] = zHat[c];
            }
            if (isRiver[c])
            {
                h[c] = zz[c] - (RiverSurfaceDepthFraction * depth[c]);
            }
        }
        WaterLevelKernel(ord, rcv, kind, h);
        double[] vd = ValleyDepth(graph, zz, h, isRiver, window);
        return new FieldSet
        {
            ["water_level_m"] = h,
            ["is_lake"] = isLake,
            ["is_river"] = isRiver,
            ["river_width_m"] = width,
            ["river_depth_m"] = depth,
            ["valley_depth_m"] = vd,
        };
    }
}
