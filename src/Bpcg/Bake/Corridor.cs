using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hero;
using Bpcg.IO;
using Bpcg.Numerics;
using Bpcg.Volume;

namespace Bpcg.Bake;

/// <summary>
/// 회랑 굽기: 히어로에서 걸어 다닐 회랑을 골라 엔진 파일로 씁니다 (src/bpcg/bake/corridor.py, docs/pipeline.md 11장).
/// </summary>
public static class Corridor
{
    public const double StrataDxM = 4.0;
    public const double StrataDzM = 2.0;
    public const double WaterNoneM = -10_000.0;
    public const byte StrataWater = 254;
    public const byte StrataAir = HeroVolume.MaterialEmpty;
    public const int MaxCandidates = 256;
    public const int StrataChunkPoints = 4_000_000;
    public const double WaterSurfaceTolM = 0.01;
    public const double CaveMouthClipM = 20.0;
    public static readonly string[] DetailStems = ["heightmap_detail", "cave_mouth_detail"];
    public const int DetailMinSamples = 8;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    private static void RemoveStems(string folder, IEnumerable<string> stems)
    {
        foreach (string stem in stems)
        {
            foreach (string ext in new[] { ".bin", ".json" })
            {
                string p = Path.Combine(folder, stem + ext);
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    private static (double[] X, double[] Y) Centers(CellGraph graph)
    {
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        double[] x = new double[ny * nx];
        double[] y = new double[ny * nx];
        for (int c = 0; c < x.Length; c++)
        {
            x[c] = graph.Origin.East + (((c % nx) + 0.5) * dx);
            y[c] = graph.Origin.North - (((c / nx) + 0.5) * dx);
        }
        return (x, y);
    }

    /// <summary>칸마다 유량이 가장 큰 기여 셀 (없으면 −1) (best_donor). 같은 유량이면 칸 번호가 작은 쪽.</summary>
    public static long[] BestDonor(long[] receiver, double[] discharge)
    {
        int n = receiver.Length;
        long[] o = new long[n];
        Array.Fill(o, -1L);
        for (int d = 0; d < n; d++)
        {
            long r = receiver[d];
            if (r == d)
            {
                continue;
            }
            long cur = o[r];
            if (cur < 0 || discharge[d] > discharge[cur] || (discharge[d] == discharge[cur] && d < cur))
            {
                o[r] = d;
            }
        }
        return o;
    }

    /// <summary>start 를 지나는 본류 (하류 끝 출구 → 상류 끝) 칸 번호와 그 안의 start 위치 (main_stem).</summary>
    public static (long[] Path, int StartIndex) MainStem(long[] receiver, double[] discharge, long start)
    {
        int n = receiver.Length;
        var down = new List<long> { start };
        var seen = new HashSet<long> { start };
        while (receiver[down[^1]] != down[^1] && down.Count <= n)
        {
            long nxt = receiver[down[^1]];
            if (!seen.Add(nxt))
            {
                throw new ArgumentException("receiver 에 순환이 있습니다");
            }
            down.Add(nxt);
        }
        long[] donor = BestDonor(receiver, discharge);
        var up = new List<long>();
        long c = start;
        while (donor[c] >= 0 && up.Count <= n)
        {
            c = donor[c];
            up.Add(c);
        }
        down.Reverse();
        long[] path = down.Concat(up).ToArray();
        return (path, down.Count - 1);
    }

    /// <summary>회랑 직사각형 고르기의 결과.</summary>
    public sealed record Choice(
        (double XMin, double XMax, double YMin, double YMax) Rect, string Axis, int Sign, long StartCell, (double X, double Y) StartXy,
        long ApexCell, string ApexKind, long NEntranceCells, long NStemCells, double LengthM, double WidthM, long NCandidates)
    {
        public OrderedDictionary<string, object?> AsDict() => new()
        {
            ["rect"] = new List<object?> { Rect.XMin, Rect.XMax, Rect.YMin, Rect.YMax },
            ["axis"] = Axis,
            ["sign"] = (long)Sign,
            ["start_cell"] = StartCell,
            ["start_xy"] = new List<object?> { StartXy.X, StartXy.Y },
            ["apex_cell"] = ApexCell,
            ["apex_kind"] = ApexKind,
            ["n_entrance_cells"] = NEntranceCells,
            ["n_stem_cells"] = NStemCells,
            ["length_m"] = LengthM,
            ["width_m"] = WidthM,
            ["n_candidates"] = NCandidates,
        };
    }

    private static double Num(object? v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>회랑 직사각형을 고릅니다 (choose_corridor).</summary>
    public static Choice ChooseCorridor(HeroState heroState, Config cfg)
    {
        CellGraph graph = heroState.Graph;
        FieldSet f = heroState.Fields;
        if (graph.Kind != "flat")
        {
            throw new ArgumentException("회랑은 평면 히어로에서만 고릅니다");
        }
        double length = cfg.F("profile.corridor.length_m");
        double width = cfg.F("profile.corridor.width_m");
        if (!(length > 0 && width > 0))
        {
            throw new ArgumentException("profile.corridor 의 length_m, width_m 은 0 보다 커야 합니다");
        }
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        (double[] xc, double[] yc) = Centers(graph);
        double loX = graph.Origin.East + (0.5 * dx);
        double hiX = graph.Origin.East + ((nx - 0.5) * dx);
        double loY = graph.Origin.North - ((ny - 0.5) * dx);
        double hiY = graph.Origin.North - (0.5 * dx);
        double[] q = f.GetFloat64("discharge_m3_per_yr");
        long[] rcv = (long[])FieldSet.CastTo(f["receiver"], typeof(long));
        bool[] isRiver = (bool[])FieldSet.CastTo(f["is_river"], typeof(bool));
        bool[] ent = Array.ConvertAll((long[])FieldSet.CastTo(f["cave_entrance"], typeof(long)), v => v > 0);

        var apexes = heroState.FanApexes.OfType<OrderedDictionary<string, object?>>().Where(a => a.ContainsKey("cell")).ToList();
        long start;
        string kind;
        if (apexes.Count > 0)
        {
            OrderedDictionary<string, object?> best0 = apexes[0];
            foreach (OrderedDictionary<string, object?> a in apexes.Skip(1))
            {
                double qa = a.TryGetValue("discharge_m3_per_yr", out object? va) ? Num(va) : 0.0;
                double qb = best0.TryGetValue("discharge_m3_per_yr", out object? vb) ? Num(vb) : 0.0;
                long ca = Convert.ToInt64(a["cell"], System.Globalization.CultureInfo.InvariantCulture);
                long cb = Convert.ToInt64(best0["cell"], System.Globalization.CultureInfo.InvariantCulture);
                if (qa > qb || (qa == qb && -ca > -cb))
                {
                    best0 = a;
                }
            }
            start = Convert.ToInt64(best0["cell"], System.Globalization.CultureInfo.InvariantCulture);
            kind = "fan";
        }
        else if (isRiver.Any(b => b))
        {
            long bestC = -1;
            for (int c = 0; c < isRiver.Length; c++)
            {
                if (isRiver[c] && (bestC < 0 || q[c] > q[bestC]))
                {
                    bestC = c;
                }
            }
            start = bestC;
            kind = "river";
        }
        else
        {
            long bestC = 0;
            for (int c = 1; c < q.Length; c++)
            {
                if (q[c] > q[bestC])
                {
                    bestC = c;
                }
            }
            start = bestC;
            kind = "discharge";
        }
        (long[] path, int p0) = MainStem(rcv, q, start);
        int np = path.Length;
        double[] px = path.Select(c => xc[c]).ToArray();
        double[] py = path.Select(c => yc[c]).ToArray();
        double[] s = new double[np];
        for (int i = 1; i < np; i++)
        {
            double ddx = px[i] - px[i - 1];
            double ddy = py[i] - py[i - 1];
            s[i] = s[i - 1] + Math.Sqrt((ddx * ddx) + (ddy * ddy));
        }
        int[] cand = Enumerable.Range(0, np).Where(i => isRiver[path[i]]).ToArray();
        if (cand.Length == 0)
        {
            cand = Enumerable.Range(0, np).ToArray();
        }
        if (cand.Length > MaxCandidates)
        {
            double[] lin = NpGrid.Linspace(0, cand.Length - 1, MaxCandidates);
            cand = lin.Select(v => cand[(int)Math.Round(v)]).ToArray();
        }
        (long, double, long)? bestKey = null;
        Choice? best = null;
        foreach (int a in cand)
        {
            int b = Math.Min(SciPy.SearchSorted(s, s[a] + length, false), np - 1);
            double vx = px[b] - px[a];
            double vy = py[b] - py[a];
            if (vx == 0 && vy == 0)
            {
                double ex = px[^1] - px[a];
                double ey = py[^1] - py[a];
                (vx, vy) = ex != 0 || ey != 0 ? (ex, ey) : (0.0, 1.0);
            }
            string axis = Math.Abs(vx) >= Math.Abs(vy) ? "east" : "north";
            double sign = (axis == "east" ? vx : vy) >= 0 ? 1.0 : -1.0;
            double ax = px[a];
            double ay = py[a];
            double l;
            double wd;
            double xa;
            double xb;
            double ya;
            double yb;
            if (axis == "east")
            {
                l = NpMath.PyMin(length, hiX - loX);
                wd = NpMath.PyMin(width, hiY - loY);
                double e2 = ax + (sign * l);
                (xa, xb) = ax <= e2 ? (ax, e2) : (e2, ax);
                ya = ay - (0.5 * wd);
                yb = ay + (0.5 * wd);
            }
            else
            {
                l = NpMath.PyMin(length, hiY - loY);
                wd = NpMath.PyMin(width, hiX - loX);
                double e2 = ay + (sign * l);
                (ya, yb) = ay <= e2 ? (ay, e2) : (e2, ay);
                xa = ax - (0.5 * wd);
                xb = ax + (0.5 * wd);
            }
            double sx = NpMath.PyMax(loX - xa, 0.0) - NpMath.PyMax(xb - hiX, 0.0);
            double sy = NpMath.PyMax(loY - ya, 0.0) - NpMath.PyMax(yb - hiY, 0.0);
            (double, double, double, double) rect = (xa + sx, xb + sx, ya + sy, yb + sy);
            long nEnt = 0;
            bool[] inside = new bool[xc.Length];
            for (int c = 0; c < xc.Length; c++)
            {
                inside[c] = xc[c] >= rect.Item1 && xc[c] <= rect.Item2 && yc[c] >= rect.Item3 && yc[c] <= rect.Item4;
                nEnt += inside[c] && ent[c] ? 1 : 0;
            }
            long nStem = path.Count(c => inside[c]);
            (long, double, long) key = (nEnt, -Math.Abs(s[a] - s[p0]), nStem);
            if (bestKey is null || key.CompareTo(bestKey.Value) > 0)
            {
                bestKey = key;
                best = new Choice(rect, axis, (int)sign, path[a], (px[a], py[a]), start, kind, nEnt, nStem, l, wd, cand.Length);
            }
        }
        return best!;
    }

    private static (double[] Xs, double[] Ys) Grid((double XMin, double XMax, double YMin, double YMax) rect, double step)
    {
        int ncol = (int)Math.Floor(((rect.XMax - rect.XMin) / step) + 1e-9) + 1;
        int nrow = (int)Math.Floor(((rect.YMax - rect.YMin) / step) + 1e-9) + 1;
        return (Enumerable.Range(0, ncol).Select(i => rect.XMin + (i * step)).ToArray(),
            Enumerable.Range(0, nrow).Select(j => rect.YMax - (j * step)).ToArray());
    }

    private static (double[] Gx, double[] Gy) Mesh2(double[] xs, double[] ys)
    {
        double[] gx = new double[xs.Length * ys.Length];
        double[] gy = new double[gx.Length];
        for (int r = 0; r < ys.Length; r++)
        {
            for (int c = 0; c < xs.Length; c++)
            {
                gx[(r * xs.Length) + c] = xs[c];
                gy[(r * xs.Length) + c] = ys[r];
            }
        }
        return (gx, gy);
    }

    private static OrderedDictionary<string, object?> SiteFrame(HeroState heroState, Config cfg, double cx, double cy)
    {
        HeroSite? site = heroState.Site;
        if (site is null)
        {
            return new OrderedDictionary<string, object?> { ["kind"] = "flat_hero", ["note"] = "행성 없이 만든 평면 히어로라 행성 위 위치가 없습니다" };
        }
        double r = cfg.F("planet.radius_m");
        double[] p = Domain.LocalToUnit(site, [cx], [cy], r);
        double[] axis0 = cfg.FArray("planet.axis");
        double na = LinAlg.Norm3(axis0);
        double[] axis = [axis0[0] / na, axis0[1] / na, axis0[2] / na];
        double lat = Math.Asin(NpMath.Clip(LinAlg.Dot3Np(p, axis), -1.0, 1.0)) * (180.0 / Math.PI);
        double[] rf = new double[3];
        rf[Domain.ArgMinAbs(axis)] = 1.0;
        double ra = LinAlg.Dot3Np(rf, axis);
        rf = [rf[0] - (ra * axis[0]), rf[1] - (ra * axis[1]), rf[2] - (ra * axis[2])];
        double nr = LinAlg.Norm3(rf);
        rf = [rf[0] / nr, rf[1] / nr, rf[2] / nr];
        double[] cr = new double[3];
        LinAlg.Cross3(rf, p, cr);
        double lon = Math.Atan2(LinAlg.Dot3Np(cr, axis), LinAlg.Dot3Np(p, rf)) * (180.0 / Math.PI);
        static List<object?> L(double[] v) => v.Select(x => (object?)x).ToList();
        return new OrderedDictionary<string, object?>
        {
            ["kind"] = "planet",
            ["origin_unit"] = L(p),
            ["east"] = L(site.East),
            ["north"] = L(site.North),
            ["lat_deg"] = lat,
            ["lon_deg"] = lon,
            ["hero_center_unit"] = L(site.CenterUnit),
            ["hero_lat_deg"] = site.LatDeg,
            ["hero_lon_deg"] = site.LonDeg,
            ["radius_m"] = r,
        };
    }

    /// <summary>재질 부피 strata.u8/.json 과 윗면 strata_top.bin/.json 을 씁니다 (bake_strata).</summary>
    public static OrderedDictionary<string, object?> BakeStrata(
        HeroVolume volume, (double XMin, double XMax, double YMin, double YMax) rect, EngineFrame frame, Config cfg, string outDir)
    {
        (double[] xs, double[] ys) = Grid(rect, StrataDxM);
        (double[] cx, double[] cy) = Mesh2(xs, ys);
        double[] top = volume.SurfaceHeight(cx, cy);
        double depth = cfg.F("groundwater.max_depth_m") + (2.0 * cfg.F("caves.passage_radius_m"));
        int nz = (int)Math.Ceiling(depth / StrataDzM);
        double[] offs = Enumerable.Range(0, nz).Select(k => (k + 0.5) * StrataDzM).ToArray();
        byte[] mat = new byte[cx.Length * nz];
        int step = Math.Max(StrataChunkPoints / nz, 1);
        for (int s = 0; s < cx.Length; s += step)
        {
            int e = Math.Min(s + step, cx.Length);
            double[] up = new double[(e - s) * nz];
            for (int c = s; c < e; c++)
            {
                for (int k = 0; k < nz; k++)
                {
                    up[((c - s) * nz) + k] = top[c] - offs[k];
                }
            }
            Dictionary<string, Array> ev = volume.EvaluateGrid(cx[s..e], cy[s..e], up, nz, double.PositiveInfinity, "material", "water");
            byte[] m = (byte[])ev["material"];
            bool[] w = (bool[])ev["water"];
            for (int q = 0; q < m.Length; q++)
            {
                mat[(s * nz) + q] = w[q] ? StrataWater : m[q];
            }
        }
        int nrow = ys.Length;
        int ncol = xs.Length;
        byte[] vol = new byte[mat.Length];
        for (int r = 0; r < nrow; r++)
        {
            for (int c = 0; c < ncol; c++)
            {
                for (int k = 0; k < nz; k++)
                {
                    vol[(((r * nz) + k) * ncol) + c] = mat[(((r * ncol) + c) * nz) + k];
                }
            }
        }
        double[] originTop = [xs[0] - frame.Cx, -frame.YOffset, -(ys[0] - frame.Cy)];
        OrderedDictionary<string, object?> topMeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "strata_top"), top, nrow, ncol, StrataDxM, originTop);
        string tmp = Path.Combine(outDir, "strata.u8.part");
        File.WriteAllBytes(tmp, vol);
        File.Move(tmp, Path.Combine(outDir, "strata.u8"), true);
        long[] counts = new long[256];
        foreach (byte v in vol)
        {
            counts[v]++;
        }
        var legend = new List<object?>();
        for (int r = 0; r < Rocks.NRocks; r++)
        {
            legend.Add(new OrderedDictionary<string, object?>
            {
                ["id"] = (long)r,
                ["name"] = Rocks.RockNames[r],
                ["rgb"] = new List<object?> { (long)Rocks.ColorRgb[r * 3], (long)Rocks.ColorRgb[(r * 3) + 1], (long)Rocks.ColorRgb[(r * 3) + 2] },
            });
        }
        legend.Add(new OrderedDictionary<string, object?> { ["id"] = (long)StrataWater, ["name"] = "water", ["rgb"] = new List<object?> { 40L, 110L, 200L } });
        legend.Add(new OrderedDictionary<string, object?> { ["id"] = (long)StrataAir, ["name"] = "air", ["rgb"] = new List<object?> { 0L, 0L, 0L } });
        var countDict = new OrderedDictionary<string, object?>();
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] != 0)
            {
                countDict[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = counts[i];
            }
        }
        var meta = new OrderedDictionary<string, object?>
        {
            ["format"] = "uint8",
            ["layout"] = "row_layer_col",
            ["axes"] = "x_east_y_up_z_south",
            ["shape"] = new List<object?> { (long)nrow, (long)nz, (long)ncol },
            ["shape_names"] = new List<object?> { "rows (+Z, north to south)", "layers (down from top)", "cols (+X)" },
            ["spacing_m"] = new OrderedDictionary<string, object?> { ["x"] = StrataDxM, ["y"] = StrataDzM, ["z"] = StrataDxM },
            ["origin_xz"] = new List<object?> { originTop[0], originTop[2] },
            ["vertical"] = "surface_following",
            ["top_stem"] = "strata_top",
            ["layer_center_rule"] = "Y = (strata_top value + origin.y) - (layer + 0.5) * spacing_m.y",
            ["depth_m"] = nz * StrataDzM,
            ["legend"] = legend,
            ["counts"] = countDict,
            ["bpcg_version"] = Package.Version,
        };
        Bundle.WriteJson(Path.Combine(outDir, "strata.json"), meta);
        return new OrderedDictionary<string, object?> { ["strata"] = meta, ["strata_top"] = topMeta };
    }

    private static double Round3(double x) => Math.Round(x * 1000.0, MidpointRounding.ToEven) / 1000.0;

    /// <summary>회랑을 골라 엔진 파일을 굽습니다 (bake_corridor). 반환: manifest.</summary>
    public static OrderedDictionary<string, object?> BakeCorridor(HeroState heroState, Config cfg, string outDir, string? engineDir = null, Action<string>? log = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(outDir);
        var sec = new OrderedDictionary<string, object?>();
        double voxel = cfg.F("profile.corridor.voxel_m");
        if (!(double.IsFinite(voxel) && voxel > 0))
        {
            throw new ArgumentException($"profile.corridor.voxel_m 은 0 보다 커야 합니다: {voxel}");
        }

        long t = Stopwatch.GetTimestamp();
        var vol = new HeroVolume(heroState, cfg);
        sec["volume"] = Seconds(t);
        t = Stopwatch.GetTimestamp();
        Choice cor = ChooseCorridor(heroState, cfg);
        (double XMin, double XMax, double YMin, double YMax) rect = cor.Rect;
        sec["choose"] = Seconds(t);
        string apex = cor.ApexKind switch { "fan" => "선상지 꼭짓점", "river" => "출구 쪽 큰 강", _ => cor.ApexKind };
        log?.Invoke($"[굽기] 회랑: {LogText.Axis(cor.Axis)} 방향으로 긴 {cor.LengthM:F0} × {cor.WidthM:F0} m, 시작 칸 {cor.StartCell} ({apex}에서 출발), 동굴 입구 칸 {LogText.N(cor.NEntranceCells)}개");

        // --- 높이맵 (회랑 지표)
        t = Stopwatch.GetTimestamp();
        (double[] xs, double[] ys) = Grid(rect, voxel);
        int nrow = ys.Length;
        int ncol = xs.Length;
        (double[] gx, double[] gy) = Mesh2(xs, ys);
        double[] surf = vol.SurfaceHeight(gx, gy);
        double cx = 0.5 * (rect.XMin + rect.XMax);
        double cy = 0.5 * (rect.YMin + rect.YMax);
        var frame = new EngineFrame(cx, cy, Math.Floor(surf.Min()));
        double[] origin = [xs[0] - cx, -frame.YOffset, -(ys[0] - cy)];
        var files = new OrderedDictionary<string, object?>();
        files["heightmap"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "heightmap"), surf, nrow, ncol, voxel, origin);
        sec["heightmap"] = Seconds(t);
        log?.Invoke($"[굽기] 높이맵 {ncol} × {nrow} 표본 (간격 {PyFormat.General(voxel)} m), {(double)sec["heightmap"]!:F2} s");

        // --- 수면·지하수면
        t = Stopwatch.GetTimestamp();
        double[] hw = vol.WaterSurface(gx, gy);
        bool[] wet = new bool[hw.Length];
        double[] waterMap = new double[hw.Length];
        for (int q = 0; q < hw.Length; q++)
        {
            wet[q] = double.IsFinite(hw[q]) && hw[q] >= surf[q] - WaterSurfaceTolM;
            waterMap[q] = wet[q] ? hw[q] : WaterNoneM;
        }
        OrderedDictionary<string, object?> wmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "water"), waterMap, nrow, ncol, voxel, origin);
        wmeta["n_wet"] = (long)wet.Count(b => b);
        files["water"] = wmeta;
        double[] zgw = vol.WaterTable(gx, gy);
        files["water_table"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "water_table"), zgw, nrow, ncol, voxel, origin);
        sec["water"] = Seconds(t);

        // --- 동굴 입구 구멍
        t = Stopwatch.GetTimestamp();
        double[] dMouth = (double[])vol.EvaluateGrid(gx, gy, surf, 1, double.PositiveInfinity, "d_cave")["d_cave"];
        double[] mouth = Array.ConvertAll(dMouth, v => NpMath.Clip(v, -CaveMouthClipM, CaveMouthClipM));
        OrderedDictionary<string, object?> mmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "cave_mouth"), mouth, nrow, ncol, voxel, origin);
        mmeta["n_open"] = (long)mouth.Count(v => v < 0.0);
        files["cave_mouth"] = mmeta;
        sec["cave_mouth"] = Seconds(t);

        // --- 프랙탈 디테일 (보기용)
        OrderedDictionary<string, double>? det = Detail.DetailSettings(cfg);
        if (det is not null)
        {
            (double kLo, double kHi) = Detail.BandLimits(voxel, det["min_wavelength_m"], det["max_wavelength_m"]);
            if (kHi <= kLo || Math.Min(nrow, ncol) < DetailMinSamples)
            {
                log?.Invoke($"[굽기] 프랙탈 디테일: 간격 {PyFormat.General(voxel)} m 회랑에는 더할 파장이 없습니다");
                det = null;
            }
        }
        double[]? surfD = null;
        OrderedDictionary<string, object?>? dmeta = null;
        if (det is not null)
        {
            t = Stopwatch.GetTimestamp();
            try
            {
                (surfD, dmeta) = Detail.AddFractalDetail(surf, gx, gy, nrow, ncol, voxel, wet, vol, cfg, mouth);
            }
            catch (ArgumentException e)
            {
                log?.Invoke($"[굽기] 프랙탈 디테일을 건너뜁니다: {e.Message}");
                det = null;
            }
        }
        if (det is not null && surfD is not null && dmeta is not null)
        {
            OrderedDictionary<string, object?> hd = Heightmap.WriteHeightmap(Path.Combine(outDir, "heightmap_detail"), surfD, nrow, ncol, voxel, origin);
            hd["detail"] = dmeta;
            files["heightmap_detail"] = hd;
            double[] dcd = (double[])vol.EvaluateGrid(gx, gy, surfD, 1, double.PositiveInfinity, "d_cave")["d_cave"];
            double[] mouthD = Array.ConvertAll(dcd, v => NpMath.Clip(v, -CaveMouthClipM, CaveMouthClipM));
            OrderedDictionary<string, object?> md = Heightmap.WriteHeightmap(Path.Combine(outDir, "cave_mouth_detail"), mouthD, nrow, ncol, voxel, origin);
            md["n_open"] = (long)mouthD.Count(v => v < 0.0);
            files["cave_mouth_detail"] = md;
            sec["detail"] = Seconds(t);
            var bw = (List<object?>)dmeta["beta_wavelength_m"]!;
            log?.Invoke($"[굽기] 프랙탈 디테일: 더한 거칠기 평균 크기(RMS) {(double)dmeta["rms_m"]!:F2} m (가장 큰 곳 {(double)dmeta["max_abs_m"]!:F1} m), "
                + $"스펙트럼 기울기 β {(double)dmeta["beta_before"]!:F2} → {(double)dmeta["beta_after"]!:F2} "
                + $"(파장 {PyFormat.General((double)bw[0]!)}–{PyFormat.General((double)bw[1]!)} m), 새로 생긴 웅덩이를 메운 칸 {LogText.N(Convert.ToInt64(dmeta["n_filled"], CultureInfo.InvariantCulture))}개, {(double)sec["detail"]!:F2} s");
        }
        else
        {
            RemoveStems(outDir, DetailStems);
        }

        // --- 히어로 전체 25 m 지표
        CellGraph g = heroState.Graph;
        int hny = (int)g.Shape[0];
        int hnx = (int)g.Shape[1];
        double dxh = g.Spacing;
        double[] zHero = heroState.Fields.GetFloat64("z_m");
        double[] oS = [g.Origin.East + (0.5 * dxh) - cx, -frame.YOffset, -((g.Origin.North - (0.5 * dxh)) - cy)];
        files["surround25"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "surround25"), zHero, hny, hnx, dxh, oS);

        // --- 동굴 메시
        t = Stopwatch.GetTimestamp();
        (double[] verts, long[] faces, OrderedDictionary<string, object?> cdiag) = Mesh.CaveSurface(vol, rect, voxel);
        string? cavesNote = null;
        if (faces.Length > 0)
        {
            CaveMesh mesh = Mesh.BuildMesh(verts, faces, vol, frame, voxel);
            long size = Mesh.ExportGlb(mesh, Path.Combine(outDir, "caves.glb"));
            files["caves"] = new OrderedDictionary<string, object?>
            {
                ["file"] = "caves.glb",
                ["bytes"] = size,
                ["vertices"] = (long)mesh.VertexCount,
                ["faces"] = (long)mesh.FaceCount,
                ["color_0"] = "rgb = rock color, alpha = material id (0..11) / 255",
                ["normals"] = "point into the cave void (front faces seen from inside)",
            };
        }
        else
        {
            cavesNote = "회랑 안에 동굴이 없어 caves.glb 를 쓰지 않았습니다";
            string stale = Path.Combine(outDir, "caves.glb");
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }
        sec["caves"] = Seconds(t);
        log?.Invoke($"[굽기] 동굴 메시: 삼각형 {LogText.N(Convert.ToInt64(cdiag.GetValueOrDefault("faces_kept") ?? 0L, CultureInfo.InvariantCulture))}개 (동굴이 든 조각 {cdiag["tiles_with_caves"]}/{cdiag["tiles"]}), {(double)sec["caves"]!:F2} s");

        // --- 재질 부피
        t = Stopwatch.GetTimestamp();
        OrderedDictionary<string, object?> st = BakeStrata(vol, rect, frame, cfg, outDir);
        foreach (KeyValuePair<string, object?> kv in st)
        {
            files[kv.Key] = kv.Value;
        }
        sec["strata"] = Seconds(t);
        var shp = (List<object?>)((OrderedDictionary<string, object?>)st["strata"]!)["shape"]!;
        log?.Invoke($"[굽기] 재질 부피 {shp[2]} × {shp[0]} × {shp[1]} 상자 (열 × 행 × 층), {(double)sec["strata"]!:F2} s");

        // --- 입구 위치 (엔진 좌표)
        var entIn = new List<object?>();
        var entOut = new List<object?>();
        for (int e = 0; e < vol.NCapsules; e++)
        {
            double mx = 0.5 * (vol.CapA[e * 3] + vol.CapB[e * 3]);
            double my = 0.5 * (vol.CapA[(e * 3) + 1] + vol.CapB[(e * 3) + 1]);
            if (mx >= rect.XMin && mx <= rect.XMax && my >= rect.YMin && my <= rect.YMax)
            {
                double[] ai = frame.ToEngine(vol.CapA[(e * 3)..((e * 3) + 3)]);
                double[] bo = frame.ToEngine(vol.CapB[(e * 3)..((e * 3) + 3)]);
                entIn.Add(ai.Select(v => (object?)Round3(v)).ToList());
                entOut.Add(bo.Select(v => (object?)Round3(v)).ToList());
            }
        }
        Bundle.WriteJson(Path.Combine(outDir, "entrances.json"), new OrderedDictionary<string, object?>
        {
            ["format"] = "bpcg-entrances",
            ["axes"] = "x_east_y_up_z_south",
            ["count"] = (long)entIn.Count,
            ["inside"] = entIn,
            ["outside"] = entOut,
            ["rule"] = "inside = 통로 한가운데 끝, outside = 산비탈 밖 공중 끝 (같은 동굴 층 높이)",
        }, maxArray: null);

        var fileNames = new List<string>
        {
            "heightmap.bin", "heightmap.json", "surround25.bin", "surround25.json",
            "water.bin", "water.json", "water_table.bin", "water_table.json",
            "strata.u8", "strata.json", "strata_top.bin", "strata_top.json", "entrances.json",
            "cave_mouth.bin", "cave_mouth.json",
        };
        if (files.ContainsKey("caves"))
        {
            fileNames.Add("caves.glb");
        }
        foreach (string stem in DetailStems)
        {
            if (files.ContainsKey(stem))
            {
                fileNames.Add($"{stem}.bin");
                fileNames.Add($"{stem}.json");
            }
        }
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> corDict = cor.AsDict();
        corDict["rect_local_m"] = new OrderedDictionary<string, object?>
        {
            ["x_min"] = rect.XMin,
            ["x_max"] = rect.XMax,
            ["y_min"] = rect.YMin,
            ["y_max"] = rect.YMax,
        };
        corDict["rect_engine_m"] = new OrderedDictionary<string, object?>
        {
            ["x_min"] = rect.XMin - cx,
            ["x_max"] = rect.XMax - cx,
            ["z_min"] = -(rect.YMax - cy),
            ["z_max"] = -(rect.YMin - cy),
        };
        corDict["voxel_m"] = voxel;
        var caves = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, object?> kv in cdiag)
        {
            if (kv.Key != "seconds")
            {
                caves[kv.Key] = kv.Value;
            }
        }
        caves["note"] = cavesNote;
        caves["n_capsules"] = (long)vol.NCapsules;
        caves["n_unopened_entrances"] = vol.NUnopened;
        caves["n_entrances"] = (long)entIn.Count;
        caves["entrances_file"] = "entrances.json";
        var manifest = new OrderedDictionary<string, object?>
        {
            ["format"] = "bpcg-corridor",
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = Bundle.GitCommit(),
            ["config_digest"] = cfg.Digest(),
            ["seed"] = cfg.I("planet.seed"),
            ["profile"] = cfg.Sec("profile").Get("name"),
            ["frame"] = frame.AsDict(),
            ["site"] = SiteFrame(heroState, cfg, cx, cy),
            ["corridor"] = corDict,
            ["hero"] = new OrderedDictionary<string, object?>
            {
                ["shape"] = new List<object?> { (long)hny, (long)hnx },
                ["spacing_m"] = dxh,
                ["origin_local_m"] = new List<object?> { g.Origin.East, g.Origin.North },
            },
            ["files"] = fileNames.Cast<object?>().ToList(),
            ["file_meta"] = files,
            ["caves"] = caves,
            ["seconds"] = sec,
        };
        Bundle.WriteJson(Path.Combine(outDir, "manifest.json"), manifest);
        fileNames.Add("manifest.json");
        if (engineDir is not null)
        {
            Directory.CreateDirectory(engineDir);
            foreach (string name in fileNames)
            {
                File.Copy(Path.Combine(outDir, name), Path.Combine(engineDir, name), true);
            }
            if (!files.ContainsKey("caves") && File.Exists(Path.Combine(engineDir, "caves.glb")))
            {
                File.Delete(Path.Combine(engineDir, "caves.glb"));
            }
            RemoveStems(engineDir, DetailStems.Where(sx => !files.ContainsKey(sx)));
            log?.Invoke($"[굽기] 엔진 폴더에도 복사했습니다: {engineDir}");
        }
        log?.Invoke($"[굽기] 끝: {(double)sec["total"]!:F2} s → {outDir}");
        return manifest;
    }
}
