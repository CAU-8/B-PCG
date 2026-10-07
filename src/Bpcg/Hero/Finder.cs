using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;
using Bpcg.Planet;

namespace Bpcg.Hero;

/// <summary>고른 히어로 자리 (HeroSite).</summary>
public sealed record HeroSite(
    double[] CenterUnit, double[] East, double[] North, long L0Cell, double Score,
    OrderedDictionary<string, double> Parts, double LatDeg, double LonDeg);

/// <summary>
/// 히어로 후보 찾기 (src/bpcg/hero/finder.py, docs/pipeline.md 9장).
/// </summary>
public static class Finder
{
    public static readonly OrderedDictionary<string, double> ScoreWeights = new()
    {
        ["uplift_gradient"] = 0.3,
        ["carbonate"] = 0.4,
        ["relief"] = 0.15,
        ["dry_fraction"] = 0.15,
    };

    public const double MaxLatDeg = 55.0;
    public const double DryRadiusM = 200_000.0;
    public const double DryAridityIndex = 1.0;
    public const double NormalizePercentile = 99.0;
    public const int CarbonateSamples = 16;
    public const double OceanMarginCells = 2.0;

    private static readonly string[] Required =
        ["is_ocean", "uplift_m_per_yr", "z_m", "relief_m", "precip_m_per_yr", "pet_m_per_yr", "strata_bottom_m", "strata_rock"];

    private static FieldSet CheckFields(PlanetState planet)
    {
        FieldSet f = planet.Fields;
        CellGraph g = planet.Graph;
        if (g.Kind != "sphere")
        {
            throw new ArgumentException("planet.graph 는 구면 그래프여야 합니다");
        }
        var missing = Required.Where(k => !f.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"planet.fields 에 히어로 점수에 필요한 필드가 없습니다: {Pipeline.PyListRepr(missing)}");
        }
        foreach (string k in Required)
        {
            int cols = f.Columns.TryGetValue(k, out int cc) ? cc : 1;
            if (f[k].Length / cols != g.NCells)
            {
                throw new ArgumentException($"planet.fields['{k}'] 의 길이가 칸 수 {g.NCells} 와 다릅니다");
            }
        }
        return f;
    }

    /// <summary>융기 기울기 |∇U| ≈ sqrt(2·평균_s ((U_n − U_c)/d)²) [1/yr] (uplift_gradient).</summary>
    /// <remarks>TODO(port): numpy 의 축 1 합(길이 8)의 덧셈 순서를 1차원 pairwise 합으로 두었습니다(대조 안 함).</remarks>
    public static double[] UpliftGradient(CellGraph graph, double[] uplift)
    {
        const int ns = CellGraph.NSlots;
        int n = graph.NCells;
        double[] o = new double[n];
        double[] row = new double[ns];
        for (int c = 0; c < n; c++)
        {
            int cnt = 0;
            for (int s = 0; s < ns; s++)
            {
                int v = graph.Nbr[(c * ns) + s];
                double g = 0.0;
                if (v >= 0)
                {
                    g = (uplift[v] - uplift[c]) / graph.Dist[(c * ns) + s];
                    cnt++;
                }
                row[s] = g * g;
            }
            o[c] = Math.Sqrt(2.0 * NpReduce.Sum(row) / Math.Max(cnt, 1));
        }
        return o;
    }

    /// <summary>z ~ z + relief 높이 범위에서 녹는 암석 비율 (carbonate_fraction).</summary>
    public static double[] CarbonateFraction(double[] strataBottom, byte[] strataRock, int nLayers, double[] z, double[] relief)
    {
        int n = z.Length;
        int m = CarbonateSamples;
        double[] zz = new double[n * m];
        for (int c = 0; c < n; c++)
        {
            double r = Math.Max(NpMath.NanToNum(relief[c]), 0.0);
            for (int k = 0; k < m; k++)
            {
                zz[(c * m) + k] = z[c] + (r * ((k + 0.5) / m));
            }
        }
        byte[] rock = Model.RockAt(strataBottom, strataRock, nLayers, zz);
        double[] o = new double[n];
        for (int c = 0; c < n; c++)
        {
            int cnt = 0;
            for (int k = 0; k < m; k++)
            {
                cnt += Rocks.Soluble[rock[(c * m) + k]] ? 1 : 0;
            }
            o[c] = (double)cnt / m;
        }
        return o;
    }

    /// <summary>칸 cells 마다 반경 안 칸 가운데 is_dry 인 칸의 비율 (dry_fraction).</summary>
    public static double[] DryFraction(CellGraph graph, bool[] isDry, long[] cells, double radiusM)
    {
        double chord = graph.Kind == "sphere"
            ? 2.0 * graph.R!.Value * Math.Sin(NpMath.PyMin(0.5 * radiusM / graph.R!.Value, 0.5 * Math.PI))
            : radiusM;
        if (cells.Length == 0)
        {
            return [];
        }
        var all = new KdTree(graph.Pos);
        var dryPts = new List<double>();
        for (int c = 0; c < isDry.Length; c++)
        {
            if (isDry[c])
            {
                dryPts.AddRange(graph.Pos.AsSpan(c * 3, 3).ToArray());
            }
        }
        double[] o = new double[cells.Length];
        if (dryPts.Count == 0)
        {
            return o;
        }
        var dry = new KdTree(dryPts.ToArray());
        Parallelism.For(0, cells.Length, k =>
        {
            ReadOnlySpan<double> q = graph.Pos.AsSpan((int)cells[k] * 3, 3);
            double total = all.CountBallPoint(q, chord);
            double d = dry.CountBallPoint(q, chord);
            o[k] = d / Math.Max(total, 1.0);
        });
        return o;
    }

    private static double[] Normalize(double[] x, bool[] mask)
    {
        double[] sel = NpStats.Select(x, mask);
        if (sel.Length == 0)
        {
            return new double[x.Length];
        }
        double r = NpStats.Percentile(sel, NormalizePercentile);
        if (!(r > 0.0))
        {
            r = sel.Max();
        }
        if (!(r > 0.0))
        {
            return new double[x.Length];
        }
        return Array.ConvertAll(x, v => NpMath.Clip(v / r, 0.0, 1.0));
    }

    /// <summary>히어로 중심이 될 수 있는 L0 칸 (candidate_mask).</summary>
    public static bool[] CandidateMask(PlanetState planet, Config cfg)
    {
        FieldSet f = CheckFields(planet);
        CellGraph g = planet.Graph;
        bool[] ocean = f.Get<bool>("is_ocean");
        double[] lat = Climate.LatitudeRad(g, cfg);
        (int nSide, double dx) = Domain.HeroGridSize(cfg);
        double halfDiag = 0.5 * Math.Sqrt(2.0) * nSide * dx;
        double margin = halfDiag + (OceanMarginCells * g.Spacing);
        double[] dOcean;
        if (Array.IndexOf(ocean, true) >= 0)
        {
            (dOcean, _) = Distance.NearestSource(g, ocean);
        }
        else
        {
            dOcean = Enumerable.Repeat(double.PositiveInfinity, g.NCells).ToArray();
        }
        double latMax = NpMath.Radians(MaxLatDeg);
        bool[] o = new bool[g.NCells];
        for (int c = 0; c < o.Length; c++)
        {
            o[c] = !ocean[c] && Math.Abs(lat[c]) <= latMax && dOcean[c] > margin;
        }
        return o;
    }

    /// <summary>L0 칸마다 히어로 점수 (score_cells): (score, parts, candidate).</summary>
    public static (double[] Score, OrderedDictionary<string, double[]> Parts, bool[] Candidate) ScoreCells(PlanetState planet, Config cfg)
    {
        FieldSet f = CheckFields(planet);
        CellGraph g = planet.Graph;
        int n = g.NCells;
        bool[] cand = CandidateMask(planet, cfg);
        bool[] ocean = f.Get<bool>("is_ocean");
        double[] grad = UpliftGradient(g, f.GetFloat64("uplift_m_per_yr"));
        double[] relief = Array.ConvertAll(f.GetFloat64("relief_m"), NpMath.NanToNum);
        var parts = new OrderedDictionary<string, double[]>
        {
            ["uplift_gradient"] = Normalize(grad, cand),
            ["carbonate"] = new double[n],
            ["relief"] = Normalize(relief, cand),
            ["dry_fraction"] = new double[n],
        };
        long[] idx = Enumerable.Range(0, n).Where(c => cand[c]).Select(c => (long)c).ToArray();
        if (idx.Length > 0)
        {
            int nl = f.Columns["strata_bottom_m"];
            double[] bottomAll = f.GetFloat64("strata_bottom_m");
            byte[] rockAll = (byte[])FieldSet.CastTo(f["strata_rock"], typeof(byte));
            double[] zAll = f.GetFloat64("z_m");
            double[] b = new double[idx.Length * nl];
            byte[] r = new byte[idx.Length * (nl + 1)];
            double[] zs = new double[idx.Length];
            double[] rs = new double[idx.Length];
            for (int k = 0; k < idx.Length; k++)
            {
                int c = (int)idx[k];
                Array.Copy(bottomAll, c * nl, b, k * nl, nl);
                Array.Copy(rockAll, c * (nl + 1), r, k * (nl + 1), nl + 1);
                zs[k] = zAll[c];
                rs[k] = relief[c];
            }
            double[] carb = CarbonateFraction(b, r, nl, zs, rs);
            double[] p = f.GetFloat64("precip_m_per_yr");
            double[] pet = f.GetFloat64("pet_m_per_yr");
            bool[] isDry = new bool[n];
            for (int c = 0; c < n; c++)
            {
                isDry[c] = !ocean[c] && p[c] < DryAridityIndex * pet[c];
            }
            double[] dry = DryFraction(g, isDry, idx, DryRadiusM);
            for (int k = 0; k < idx.Length; k++)
            {
                parts["carbonate"][idx[k]] = carb[k];
                parts["dry_fraction"][idx[k]] = dry[k];
            }
        }
        double[] total = new double[n];
        foreach (KeyValuePair<string, double> kv in ScoreWeights)
        {
            double[] part = parts[kv.Key];
            for (int c = 0; c < n; c++)
            {
                total[c] += kv.Value * part[c];
            }
        }
        for (int c = 0; c < n; c++)
        {
            if (!cand[c])
            {
                total[c] = 0.0;
            }
        }
        return (total, parts, cand);
    }

    private static double Degrees(double x) => x * (180.0 / Math.PI);

    /// <summary>L0 칸 하나를 중심으로 하는 HeroSite (site_from_cell).</summary>
    public static HeroSite SiteFromCell(PlanetState planet, long cell, Config cfg, double score = double.NaN, OrderedDictionary<string, double>? parts = null)
    {
        CellGraph g = planet.Graph;
        if (cell < 0 || cell >= g.NCells)
        {
            throw new ArgumentException($"cell 은 0..{g.NCells - 1} 이어야 합니다: {cell}");
        }
        double[] p = g.Pos.AsSpan((int)cell * 3, 3).ToArray();
        double np = LinAlg.Norm3(p);
        double[] center = [p[0] / np, p[1] / np, p[2] / np];
        double[] axis0 = cfg.FArray("planet.axis");
        double na = LinAlg.Norm3(axis0);
        double[] axis = [axis0[0] / na, axis0[1] / na, axis0[2] / na];
        (double[] east, double[] north) = Domain.TangentFrame(center, axis);
        double lat = Degrees(Math.Asin(NpMath.Clip(LinAlg.Dot3Np(center, axis), -1.0, 1.0)));
        double[] rf = new double[3];
        rf[Domain.ArgMinAbs(axis)] = 1.0;
        double ra = LinAlg.Dot3Np(rf, axis);
        rf = [rf[0] - (ra * axis[0]), rf[1] - (ra * axis[1]), rf[2] - (ra * axis[2])];
        double nr = LinAlg.Norm3(rf);
        rf = [rf[0] / nr, rf[1] / nr, rf[2] / nr];
        double[] cr = new double[3];
        LinAlg.Cross3(rf, center, cr);
        double lon = Degrees(Math.Atan2(LinAlg.Dot3Np(cr, axis), LinAlg.Dot3Np(center, rf)));
        return new HeroSite(center, east, north, cell, score, parts ?? [], lat, lon);
    }

    /// <summary>점수가 가장 높은 L0 칸을 히어로 중심으로 고릅니다 (find_hero, 같으면 번호가 작은 쪽).</summary>
    public static HeroSite FindHero(PlanetState planet, Config cfg)
    {
        (double[] total, OrderedDictionary<string, double[]> parts, bool[] cand) = ScoreCells(planet, cfg);
        if (Array.IndexOf(cand, true) < 0)
        {
            throw new ArgumentException(
                "히어로 후보 칸이 없습니다 (육지, |위도| ≤ 55°, 바다에서 히어로 반대각선 + L0 칸 2개 넘게 떨어진 칸이 하나도 없음)");
        }
        int best = 0;
        double bestV = cand[0] ? total[0] : -1.0;
        for (int c = 1; c < total.Length; c++)
        {
            double v = cand[c] ? total[c] : -1.0;
            if (v > bestV)
            {
                bestV = v;
                best = c;
            }
        }
        var bestParts = new OrderedDictionary<string, double>();
        foreach (KeyValuePair<string, double[]> kv in parts)
        {
            bestParts[kv.Key] = kv.Value[best];
        }
        return SiteFromCell(planet, best, cfg, total[best], bestParts);
    }
}
