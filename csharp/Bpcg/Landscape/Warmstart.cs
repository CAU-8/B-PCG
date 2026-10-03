using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Landscape;

/// <summary>
/// 거친 격자 먼저 풀기 (평면 히어로용 시작 지형, src/bpcg/landscape/warmstart.py).
/// </summary>
public static class Warmstart
{
    public const int MinCoarseCells = 64;
    public const int MinWarmStartCells = 16;

    private static (int[] Blk, int Nyc, int Nxc) BlockIndex(CellGraph graph, int factor)
    {
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        int nyc = Math.Max(ny / factor, 1);
        int nxc = Math.Max(nx / factor, 1);
        int[] blk = new int[graph.NCells];
        for (int c = 0; c < blk.Length; c++)
        {
            int j = Math.Min(c / nx / factor, nyc - 1);
            int i = Math.Min(c % nx / factor, nxc - 1);
            blk[c] = (j * nxc) + i;
        }
        return (blk, nyc, nxc);
    }

    /// <summary>
    /// 평면 그래프의 시작 지형 [m] (N,) 을 거친 격자 풀이로 만듭니다 (coarse_warm_start).
    /// 거친 격자 한 변이 MinWarmStartCells 보다 작으면 (null, {skipped: true, …}).
    /// </summary>
    public static (double[]? ZInit, OrderedDictionary<string, object?> Diag) CoarseWarmStart(
        CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, LayerColumns? columns,
        Config cfg, double[]? extraInflow = null, int factor = 4, int? maxIter = null, Action<string>? log = null)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException("coarse_warm_start 는 평면 그래프에서만 씁니다");
        }
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        if (Math.Min(ny, nx) / factor < MinWarmStartCells)
        {
            return (null, new OrderedDictionary<string, object?>
            {
                ["levels"] = new List<object?>(),
                ["seconds"] = 0.0,
                ["skipped"] = true,
            });
        }
        long t0 = Stopwatch.GetTimestamp();
        int n = graph.NCells;
        double[] zo = zOutlet.Length == 1 ? Enumerable.Repeat(zOutlet[0], n).ToArray() : zOutlet;
        (int[] blk, int nyc, int nxc) = BlockIndex(graph, factor);
        int nc = nyc * nxc;
        double[] counts = new double[nc];
        double[] uC = new double[nc];
        double[] rC = new double[nc];
        for (int c = 0; c < n; c++)
        {
            counts[blk[c]] += 1.0;
        }
        for (int c = 0; c < n; c++)
        {
            uC[blk[c]] += uplift[c];
        }
        for (int c = 0; c < n; c++)
        {
            rC[blk[c]] += runoffEff[c];
        }
        for (int k = 0; k < nc; k++)
        {
            uC[k] /= counts[k];
            rC[k] /= counts[k];
        }
        bool[] outC = new bool[nc];
        double[] zoC = new double[nc];
        Array.Fill(zoC, double.PositiveInfinity);
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c])
            {
                outC[blk[c]] = true;
                zoC[blk[c]] = Math.Min(zoC[blk[c]], zo[c]);
            }
        }
        for (int k = 0; k < nc; k++)
        {
            if (!outC[k])
            {
                zoC[k] = 0.0;
            }
        }
        double[]? inflowC = null;
        if (extraInflow is not null)
        {
            inflowC = new double[nc];
            for (int c = 0; c < n; c++)
            {
                inflowC[blk[c]] += extraInflow[c];
            }
        }
        double dxc = graph.Spacing * factor;
        CellGraph coarse = Graph.FlatGraph(
            nyc, nxc, dxc, cfg.F("landscape.jitter"), cfg.I("planet.seed") + 7919, graph.Origin);
        LayerColumns? colsC = null;
        if (columns is not null)
        {
            int nl = columns.NLayers;
            int nr = nl + 1;
            double[] b = new double[nc * nl];
            byte[] r = new byte[nc * nr];
            double[] km = new double[nc * nr];
            double[] sc = new double[nc * nr];
            for (int k = 0; k < nc; k++)
            {
                int jc = k / nxc;
                int ic = k % nxc;
                int jf = Math.Min((jc * factor) + (factor / 2), ny - 1);
                int iff = Math.Min((ic * factor) + (factor / 2), nx - 1);
                int pick = (jf * nx) + iff;
                Array.Copy(columns.Bottom, pick * nl, b, k * nl, nl);
                Array.Copy(columns.Rock, pick * nr, r, k * nr, nr);
                Array.Copy(columns.KMult, pick * nr, km, k * nr, nr);
                Array.Copy(columns.SCrit, pick * nr, sc, k * nr, nr);
            }
            colsC = new LayerColumns(b, r, km, sc, nl);
        }
        var levels = new List<object?>();
        double[]? zInitC = null;
        if (Math.Min(nyc, nxc) / factor >= MinCoarseCells)
        {
            (zInitC, OrderedDictionary<string, object?> sub) = CoarseWarmStart(
                coarse, outC, zoC, uC, rC, colsC, cfg, inflowC, factor, maxIter);
            levels.AddRange((List<object?>)sub["levels"]!);
        }
        SolverResult res = Solver.SolveSteadyState(coarse, outC, zoC, uC, rC, colsC, cfg, zInitC, inflowC, maxIter);
        levels.Add(new OrderedDictionary<string, object?>
        {
            ["shape"] = new List<object?> { (long)nyc, (long)nxc },
            ["spacing_m"] = dxc,
            ["iterations"] = (long)res.Iterations,
            ["converged"] = res.Converged,
            ["n_frozen"] = (long)res.NFrozen,
        });
        // 거친 칸 중심(흔들기 전) 격자에서 쌍선형 보간. 가장자리 반 칸은 선형 외삽합니다.
        (double ox, double oy) = graph.Origin;
        double[] xs = new double[nxc];
        for (int i = 0; i < nxc; i++)
        {
            xs[i] = ox + ((i + 0.5) * dxc);
        }
        double[] ysAsc = new double[nyc];
        for (int j = 0; j < nyc; j++)
        {
            // ys[::-1]: 남 → 북 오름차순
            ysAsc[j] = oy - ((nyc - 1 - j + 0.5) * dxc);
        }
        double[] img = new double[nc];
        for (int j = 0; j < nyc; j++)
        {
            Array.Copy(res.Z, (nyc - 1 - j) * nxc, img, j * nxc, nxc);
        }
        double[] qy = new double[n];
        double[] qx = new double[n];
        for (int c = 0; c < n; c++)
        {
            qy[c] = graph.Pos[(c * 3) + 1];
            qx[c] = graph.Pos[c * 3];
        }
        double[] zInit = Interp.RegularGridLinear2D(ysAsc, xs, img, qy, qx);
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c])
            {
                zInit[c] = zo[c];
            }
        }
        double seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        var diag = new OrderedDictionary<string, object?> { ["levels"] = levels, ["seconds"] = seconds };
        if (log is not null)
        {
            IEnumerable<string> parts = levels.Cast<OrderedDictionary<string, object?>>()
                .Select(lv => $"{((List<object?>)lv["shape"]!)[0]}² 반복 {lv["iterations"]}");
            log($"[2단계] 거친 격자 먼저: {string.Join(", ", parts)}, {seconds:F1} s");
        }
        return (zInit, diag);
    }
}
