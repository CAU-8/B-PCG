using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.IO;

namespace Bpcg.NoiseWorld;

/// <summary>노이즈 비교본의 히어로 유역: 한 변 n 칸 정사각형의 칸 가운데 값 (0번 줄이 북쪽).</summary>
public sealed class NoiseHero
{
    public required int N { get; init; }

    public required double SpacingM { get; init; }

    public double SizeM => N * SpacingM;

    /// <summary>칸 가운데 해발 높이 [m] (n × n, 행 우선).</summary>
    public required double[] Z { get; init; }

    /// <summary>강 골짜기 안쪽 정도 (0~1).</summary>
    public required double[] Valley { get; init; }

    /// <summary>지표에 동굴이 열린 칸인지.</summary>
    public required bool[] Open { get; init; }

    /// <summary>칸 (행 j, 열 i) 가운데의 평면 좌표 (동, 북) [m]. 히어로 가운데가 (0, 0).</summary>
    public (double E, double N) Center(int j, int i) => ((-0.5 * SizeM) + ((i + 0.5) * SpacingM), (0.5 * SizeM) - ((j + 0.5) * SpacingM));
}

/// <summary>
/// 노이즈 비교본의 히어로 유역과 회랑을 B-PCG 회랑과 같은 파일 꼴로 굽습니다 (<see cref="Corridor.BakeCorridor"/> 와 같은 파일 이름·형식).
/// Godot 엔진은 두 회랑을 같은 코드로 읽습니다.
/// </summary>
public static class NoiseCorridor
{
    /// <summary>목차의 generator 값. 엔진이 이 값을 보고 '노이즈 비교본' 표시를 띄웁니다.</summary>
    public const string Generator = "noise";

    /// <summary>입구 목록에 넣는 입구 수의 위 한계 (큰 것부터).</summary>
    public const int MaxEntrances = 64;

    /// <summary>회랑 가장자리 띠에서 막은 구멍 자리에 쓰는 동굴 입구 지도 값 [m] (0 보다 크면 지형이 덮음).</summary>
    public const double EdgeMouthM = 0.5;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>히어로 유역 칸마다 지표 높이·강 골짜기·열린 동굴을 셉니다.</summary>
    public static NoiseHero BuildHero(NoiseTerrain terrain, NoiseSite site, Config cfg)
    {
        double size = cfg.F("profile.hero.size_m");
        double dx = cfg.F("profile.hero.spacing_m");
        int n = (int)Math.Round(size / dx);
        if (!(dx > 0) || n < 8)
        {
            throw new ArgumentException(
                $"히어로 한 변(profile.hero.size_m {LogText.Num(size)} m)이 칸 간격(spacing_m {LogText.Num(dx)} m)의 8배보다 작습니다. "
                + "size_m 을 늘리거나 spacing_m 을 줄이세요");
        }
        double[] z = new double[n * n];
        double[] valley = new double[n * n];
        bool[] open = new bool[n * n];
        var hero = new NoiseHero { N = n, SpacingM = dx, Z = z, Valley = valley, Open = open };
        double r = terrain.RadiusM;
        Parallelism.For(0, n * n, q =>
        {
            (double e, double nn) = hero.Center(q / n, q % n);
            (double ux, double uy, double uz) = site.ToUnit(e, nn);
            SurfacePoint sp = terrain.Surface(ux, uy, uz, dx);
            z[q] = sp.HeightM;
            valley[q] = sp.Valley;
            double rr = r + sp.HeightM;
            open[q] = sp.HeightM > 0.0 && terrain.CaveDistance(ux * rr, uy * rr, uz * rr, 0.0, dx) < 0.0;
        });
        return hero;
    }

    /// <summary>회랑 사각형 하나와 그 점수.</summary>
    public sealed record Choice((double XMin, double XMax, double YMin, double YMax) Rect, string Axis, double Score, int NCandidates,
        double LandFraction, double ReliefM, double RiverFraction, int NOpenCells);

    /// <summary>
    /// 걸어 볼 회랑을 고릅니다. 히어로 안에 두 방향(동서로 긴 띠, 남북으로 긴 띠) × 5 × 5 자리를 놓고, 육지 몫·기복·강 골짜기·열린
    /// 동굴로 점수를 매겨 가장 높은 것을 고릅니다. B-PCG 처럼 강 본류를 따라 고르지 않습니다(노이즈 강에는 흐름이 없음).
    /// </summary>
    public static Choice ChooseCorridor(NoiseHero hero, Config cfg)
    {
        (double askedL, double askedW, double voxel) = Corridor.SizeSettings(cfg);
        double span = hero.SizeM - (2.0 * hero.SpacingM);
        double length = Corridor.FloorToMultiple(Math.Min(askedL, span), voxel);
        double width = Corridor.FloorToMultiple(Math.Min(askedW, span), voxel);
        int n = hero.N;
        Choice? best = null;
        int count = 0;
        foreach (string axis in new[] { "east", "north" })
        {
            double lx = axis == "east" ? length : width;
            double ly = axis == "east" ? width : length;
            for (int a = 0; a < 5; a++)
            {
                for (int b = 0; b < 5; b++)
                {
                    double cx = (-0.5 * span) + (0.5 * lx) + (a * (span - lx) / 4.0);
                    double cy = (-0.5 * span) + (0.5 * ly) + (b * (span - ly) / 4.0);
                    cx = Math.Round(cx / voxel) * voxel;
                    cy = Math.Round(cy / voxel) * voxel;
                    (double, double, double, double) rect = (cx - (0.5 * lx), cx + (0.5 * lx), cy - (0.5 * ly), cy + (0.5 * ly));
                    var zs = new List<double>();
                    int land = 0;
                    int cells = 0;
                    int river = 0;
                    int openCells = 0;
                    for (int j = 0; j < n; j++)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            (double e, double nn) = hero.Center(j, i);
                            if (e < rect.Item1 || e > rect.Item2 || nn < rect.Item3 || nn > rect.Item4)
                            {
                                continue;
                            }
                            int q = (j * n) + i;
                            cells++;
                            if (hero.Z[q] > 0.0)
                            {
                                land++;
                                zs.Add(hero.Z[q]);
                            }
                            river += hero.Valley[q] > 0.9 ? 1 : 0;
                            openCells += hero.Open[q] ? 1 : 0;
                        }
                    }
                    count++;
                    if (cells == 0)
                    {
                        continue;
                    }
                    double landFrac = (double)land / cells;
                    zs.Sort();
                    double relief = zs.Count > 1 ? zs[(int)(0.9 * (zs.Count - 1))] - zs[(int)(0.1 * (zs.Count - 1))] : 0.0;
                    double riverFrac = (double)river / cells;
                    double score = landFrac < 0.6
                        ? landFrac - 1.0
                        : (2.0 * landFrac) + Math.Min(relief / 600.0, 1.0) + (0.8 * Math.Min(riverFrac / 0.02, 1.0))
                            + (0.5 * Math.Min(openCells / 3.0, 1.0));
                    if (best is null || score > best.Score)
                    {
                        best = new Choice(rect, axis, score, 0, landFrac, relief, riverFrac, openCells);
                    }
                }
            }
        }
        return best! with { NCandidates = count };
    }

    /// <summary>
    /// 회랑 파일을 굽습니다. outDir 에 B-PCG 회랑과 같은 파일(heightmap, surround25, water, water_table, water_table_lower,
    /// cave_mouth, caves.glb, strata.u8·.json, strata_top, entrances.json, manifest.json)을 쓰고, engineDir 이 있으면 그곳에도 복사합니다.
    /// </summary>
    public static OrderedDictionary<string, object?> Bake(
        NoiseTerrain terrain, NoiseSite site, NoiseHero hero, Config cfg, string outDir, string? engineDir, Action<string>? log,
        OrderedDictionary<string, object?>? parent)
    {
        long tAll = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(outDir);
        var sec = new OrderedDictionary<string, object?>();
        double r0 = terrain.RadiusM;
        long t = Stopwatch.GetTimestamp();
        Choice cor = ChooseCorridor(hero, cfg);
        (double XMin, double XMax, double YMin, double YMax) rect = cor.Rect;
        double voxel = cfg.F("profile.corridor.voxel_m");
        double lengthM = cor.Axis == "east" ? rect.XMax - rect.XMin : rect.YMax - rect.YMin;
        double widthM = cor.Axis == "east" ? rect.YMax - rect.YMin : rect.XMax - rect.XMin;
        sec["choose"] = Seconds(t);
        log?.Invoke($"[노이즈] 회랑: {LogText.Axis(cor.Axis)} 방향으로 긴 {lengthM:F0} × {widthM:F0} m, 후보 {cor.NCandidates}곳 가운데 점수 {cor.Score:F2} "
            + $"(육지 {LogText.Pct(cor.LandFraction)}, 기복 {cor.ReliefM:F0} m, 강 골짜기 칸 {LogText.Pct(cor.RiverFraction)}, 지표에 열린 동굴 칸 {cor.NOpenCells}개)");

        // --- 높이맵·물·지하수면·동굴 입구 구멍 (회랑 표본마다 한 번에)
        t = Stopwatch.GetTimestamp();
        (double[] xs, double[] ys) = Corridor.Grid(rect, voxel);
        int nrow = ys.Length;
        int ncol = xs.Length;
        (double[] gx, double[] gy) = Corridor.Mesh2(xs, ys);
        int np = gx.Length;
        double[] surf = new double[np];
        double[] riverWater = new double[np];
        double[] zgw = new double[np];
        double[] mouth = new double[np];
        Parallelism.For(0, np, q =>
        {
            (double ux, double uy, double uz) = site.ToUnit(gx[q], gy[q]);
            SurfacePoint sp = terrain.Surface(ux, uy, uz, voxel);
            surf[q] = sp.HeightM;
            riverWater[q] = sp.RiverWaterM;
            zgw[q] = terrain.WaterTable(ux, uy, uz, sp.HeightM, voxel);
            double rr = r0 + sp.HeightM;
            mouth[q] = Math.Clamp(terrain.CaveDistance(ux * rr, uy * rr, uz * rr, 0.0, Corridor.CaveMouthClipM),
                -Corridor.CaveMouthClipM, Corridor.CaveMouthClipM);
            // 엔진은 회랑 가장자리 안쪽 한 칸(히어로 칸 간격) 띠에 주변 지형 충돌면을 남겨 둡니다(engine/README.md '동굴 걸어 들어가기').
            // 그 띠 안의 구멍으로는 떨어지지 못하고 주변 지형 위에 서므로, 이 띠에서는 구멍을 막습니다. 동굴은 지표 아래에서 끝납니다.
            double edge = Math.Min(Math.Min(gx[q] - rect.XMin, rect.XMax - gx[q]), Math.Min(gy[q] - rect.YMin, rect.YMax - gy[q]));
            if (edge < hero.SpacingM)
            {
                mouth[q] = Math.Max(mouth[q], EdgeMouthM);
            }
        });
        double cx = 0.5 * (rect.XMin + rect.XMax);
        double cy = 0.5 * (rect.YMin + rect.YMax);
        var frame = new EngineFrame(cx, cy, Math.Floor(surf.Min()));
        double[] origin = [xs[0] - cx, -frame.YOffset, -(ys[0] - cy)];
        var files = new OrderedDictionary<string, object?>();
        Bundle.InvalidateIndex(outDir, "manifest.json");
        RemoveDetail(outDir);
        files["heightmap"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "heightmap"), surf, nrow, ncol, voxel, origin);
        sec["heightmap"] = Seconds(t);
        log?.Invoke($"[노이즈] 높이맵 {ncol} × {nrow} 표본 (간격 {PyFormat.General(voxel)} m), 높이 {surf.Min():F0}~{surf.Max():F0} m, {(double)sec["heightmap"]!:F2} s");

        t = Stopwatch.GetTimestamp();
        double[] water = new double[np];
        long nSea = 0;
        long nRiver = 0;
        for (int q = 0; q < np; q++)
        {
            double w = Corridor.WaterNoneM;
            int kind = 0;
            if (surf[q] < 0.0)
            {
                w = 0.0;
                kind = 1;
            }
            if (double.IsFinite(riverWater[q]) && riverWater[q] >= surf[q] - Corridor.WaterSurfaceTolM && riverWater[q] > w)
            {
                w = riverWater[q];
                kind = 2;
            }
            water[q] = w;
            nSea += kind == 1 ? 1 : 0;
            nRiver += kind == 2 ? 1 : 0;
        }
        OrderedDictionary<string, object?> wmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "water"), water, nrow, ncol, voxel, origin);
        wmeta["n_wet"] = nSea + nRiver;
        wmeta["n_sea"] = nSea;
        wmeta["n_river"] = nRiver;
        files["water"] = wmeta;
        files["water_table"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "water_table"), zgw, nrow, ncol, voxel, origin);
        // 노이즈 비교본에는 막는 층 아래의 두 번째 물 높이가 없어, 아래 지하수면도 같은 높이로 씁니다.
        files["water_table_lower"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "water_table_lower"), zgw, nrow, ncol, voxel, origin);
        OrderedDictionary<string, object?> mmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "cave_mouth"), mouth, nrow, ncol, voxel, origin);
        long nOpen = mouth.Count(v => v < 0.0);
        mmeta["n_open"] = nOpen;
        files["cave_mouth"] = mmeta;
        sec["water"] = Seconds(t);
        log?.Invoke($"[노이즈] 물: 젖은 표본 {LogText.N(nSea + nRiver)}개 (바다 {LogText.N(nSea)}, 강 물길 {LogText.N(nRiver)}), "
            + $"동굴 입구 구멍 표본 {LogText.N(nOpen)}개, {(double)sec["water"]!:F2} s");

        // --- 히어로 전체 지표 (둘레 지형)
        int hn = hero.N;
        double dxh = hero.SpacingM;
        double[] oS = [(-0.5 * hero.SizeM) + (0.5 * dxh) - cx, -frame.YOffset, -(((0.5 * hero.SizeM) - (0.5 * dxh)) - cy)];
        files["surround25"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "surround25"), hero.Z, hn, hn, dxh, oS);

        // --- 동굴 메시
        t = Stopwatch.GetTimestamp();
        (CaveMesh? mesh, OrderedDictionary<string, object?> cdiag) = CaveMeshFor(terrain, site, rect, voxel, frame);
        string? cavesNote = null;
        if (mesh is not null && mesh.FaceCount > 0)
        {
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
        log?.Invoke($"[노이즈] 동굴 메시: 삼각형 {LogText.N(mesh?.FaceCount ?? 0)}개 (동굴이 든 조각 {cdiag["tiles_with_caves"]}/{cdiag["tiles"]}, "
            + $"지표 위라서 버린 삼각형 {LogText.N(Convert.ToInt64(cdiag["faces_above_ground"], System.Globalization.CultureInfo.InvariantCulture))}개, "
            + $"어디로도 이어지지 않아 버린 삼각형 {LogText.N(Convert.ToInt64(cdiag["faces_unreachable"], System.Globalization.CultureInfo.InvariantCulture))}개), "
            + $"{(double)sec["caves"]!:F2} s");

        // --- 동굴 입구
        t = Stopwatch.GetTimestamp();
        (List<object?> entIn, List<object?> entOut, int nMouths) = Entrances(terrain, site, gx, gy, surf, mouth, nrow, ncol, voxel, frame);
        Bundle.WriteJson(Path.Combine(outDir, "entrances.json"), new OrderedDictionary<string, object?>
        {
            ["format"] = "bpcg-entrances",
            ["axes"] = "x_east_y_up_z_south",
            ["count"] = (long)entIn.Count,
            ["inside"] = entIn,
            ["outside"] = entOut,
            ["rule"] = "inside = 지표 구멍 아래 동굴 빈 곳의 점, outside = 구멍에서 내리막 쪽으로 12 m 떨어진 지표 4 m 위 (노이즈 비교본)",
        }, maxArray: null);
        sec["entrances"] = Seconds(t);

        // --- 재질 부피
        t = Stopwatch.GetTimestamp();
        OrderedDictionary<string, object?> st = Strata(terrain, site, rect, voxel, frame, cfg, outDir);
        foreach (KeyValuePair<string, object?> kv in st)
        {
            files[kv.Key] = kv.Value;
        }
        sec["strata"] = Seconds(t);
        var shp = (List<object?>)((OrderedDictionary<string, object?>)st["strata"]!)["shape"]!;
        log?.Invoke($"[노이즈] 재질 부피 {shp[2]} × {shp[0]} × {shp[1]} 상자 (열 × 행 × 층), {(double)sec["strata"]!:F2} s");

        var fileNames = new List<string>
        {
            "heightmap.bin", "heightmap.json", "surround25.bin", "surround25.json",
            "water.bin", "water.json", "water_table.bin", "water_table.json", "water_table_lower.bin", "water_table_lower.json",
            "strata.u8", "strata.json", "strata_top.bin", "strata_top.json", "entrances.json",
            "cave_mouth.bin", "cave_mouth.json",
        };
        if (files.ContainsKey("caves"))
        {
            fileNames.Add("caves.glb");
        }
        sec["total"] = Seconds(tAll);
        var caves = new OrderedDictionary<string, object?>(cdiag)
        {
            ["note"] = cavesNote,
            ["n_entrances"] = (long)entIn.Count,
            ["n_surface_openings"] = (long)nMouths,
            ["entrances_file"] = "entrances.json",
            ["how"] = "굴 = 서로 다른 3D 노이즈 둘이 모두 0 에 가까운 줄, 큰 방 = 세 번째 3D 노이즈가 문턱을 넘는 덩어리. 물과 암석에 상관없이 정함",
        };
        var corDict = new OrderedDictionary<string, object?>
        {
            ["rect"] = new List<object?> { rect.XMin, rect.XMax, rect.YMin, rect.YMax },
            ["axis"] = cor.Axis,
            ["sign"] = 1L,
            ["length_m"] = lengthM,
            ["width_m"] = widthM,
            ["n_candidates"] = (long)cor.NCandidates,
            ["score"] = cor.Score,
            ["land_fraction"] = cor.LandFraction,
            ["relief_m"] = cor.ReliefM,
            ["river_valley_fraction"] = cor.RiverFraction,
            ["n_open_cave_cells"] = (long)cor.NOpenCells,
            ["rect_local_m"] = new OrderedDictionary<string, object?>
            {
                ["x_min"] = rect.XMin,
                ["x_max"] = rect.XMax,
                ["y_min"] = rect.YMin,
                ["y_max"] = rect.YMax,
            },
            ["rect_engine_m"] = new OrderedDictionary<string, object?>
            {
                ["x_min"] = rect.XMin - cx,
                ["x_max"] = rect.XMax - cx,
                ["z_min"] = -(rect.YMax - cy),
                ["z_max"] = -(rect.YMin - cy),
            },
            ["voxel_m"] = voxel,
        };
        (double ox, double oy, double oz) = site.ToUnit(cx, cy);
        (double lat, double lon) = Globe.LatLonDeg([ox, oy, oz], Globe.AxisRotation(terrain.Axis));
        var manifest = new OrderedDictionary<string, object?>
        {
            ["format"] = RunFolder.CorridorFormat,
            ["generator"] = Generator,
            ["generator_note"] = "노이즈 비교본: 판·침식·물의 흐름을 계산하지 않고 오늘날 게임처럼 노이즈만으로 만든 땅입니다 (docs/noise_world.md)",
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = Bundle.GitCommit(),
            ["git_dirty"] = Bundle.GitDirty(),
            ["config_digest"] = cfg.Digest(),
            ["seed"] = cfg.I("planet.seed"),
            ["profile"] = cfg.Sec("profile").Get("name"),
            ["parent"] = parent,
            ["frame"] = frame.AsDict(),
            ["site"] = new OrderedDictionary<string, object?>
            {
                ["kind"] = "planet",
                ["origin_unit"] = new List<object?> { ox, oy, oz },
                ["east"] = site.East.Cast<object?>().ToList(),
                ["north"] = site.North.Cast<object?>().ToList(),
                ["lat_deg"] = lat,
                ["lon_deg"] = lon,
                ["hero_center_unit"] = site.Center.Cast<object?>().ToList(),
                ["hero_lat_deg"] = site.LatDeg,
                ["hero_lon_deg"] = site.LonDeg,
                ["radius_m"] = r0,
            },
            ["corridor"] = corDict,
            ["hero"] = new OrderedDictionary<string, object?>
            {
                ["shape"] = new List<object?> { (long)hn, (long)hn },
                ["spacing_m"] = dxh,
                ["origin_local_m"] = new List<object?> { -0.5 * hero.SizeM, 0.5 * hero.SizeM },
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
            RemoveDetail(engineDir);
            log?.Invoke($"[노이즈] 엔진 폴더에도 복사했습니다: {engineDir}");
        }
        return manifest;
    }

    // 노이즈 회랑은 높이맵에 짧은 물결까지 다 넣었으므로 프랙탈 디테일 파일을 쓰지 않습니다. 같은 폴더에 B-PCG 굽기가 남긴
    // 디테일 파일이 있으면 엔진이 그것을 회랑 지형으로 고르므로 지웁니다.
    private static void RemoveDetail(string folder)
    {
        foreach (string stem in Corridor.DetailStems)
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

    private sealed class TileMesh
    {
        public List<double> Verts = [];
        public List<int> Faces = [];
        public List<bool> Anchor = [];
        public long Voxels;
        public bool HasCaves;
        public long FacesRaw;
        public long FacesAbove;
        public ExceptionDispatchInfo? Error;
    }

    // 회랑 안 동굴 벽 메시. B-PCG 동굴 메시(Mesh.CaveSurface)와 같은 64 × 64 조각, 같은 등위면 함수(Mesh.IsoSurface),
    // 같은 지표 자르기(무게중심이 지표 위인 삼각형을 버림), 같은 막힌 조각 지우기(Mesh.BuildMesh 의 anchor)를 씁니다.
    private static (CaveMesh? Mesh, OrderedDictionary<string, object?> Diag) CaveMeshFor(
        NoiseTerrain terrain, NoiseSite site, (double XMin, double XMax, double YMin, double YMax) rect, double v, EngineFrame frame)
    {
        int nxv = (int)Math.Floor(((rect.XMax - rect.XMin) / v) + 1e-9) + 1;
        int nyv = (int)Math.Floor(((rect.YMax - rect.YMin) / v) + 1e-9) + 1;
        double[] xs = Enumerable.Range(0, nxv).Select(i => rect.XMin + (i * v)).ToArray();
        double[] ys = Enumerable.Range(0, nyv).Select(j => rect.YMin + (j * v)).ToArray();
        var tiles = new List<(int Ia, int Ib, int Ja, int Jb)>();
        for (int ia = 0; ia < Math.Max(nxv - 1, 1); ia += Mesh.TileVoxels)
        {
            int ib = Math.Min(ia + Mesh.TileVoxels, nxv - 1);
            for (int ja = 0; ja < Math.Max(nyv - 1, 1); ja += Mesh.TileVoxels)
            {
                tiles.Add((ia, ib, ja, Math.Min(ja + Mesh.TileVoxels, nyv - 1)));
            }
        }
        var results = new TileMesh[tiles.Count];
        Parallelism.ForTasks(tiles.Count, k =>
        {
            try
            {
                results[k] = CaveTile(terrain, site, xs, ys, tiles[k], v);
            }
            catch (Exception e)
            {
                results[k] = new TileMesh { Error = ExceptionDispatchInfo.Capture(e) };
            }
        });
        long nV = 0;
        long nF = 0;
        foreach (TileMesh tm in results)
        {
            tm.Error?.Throw();
            nV += tm.Verts.Count / 3;
            nF += tm.Faces.Count / 3;
        }
        double[] verts = new double[nV * 3];
        int[] faces = new int[nF * 3];
        bool[] anchor = new bool[nV];
        long vAt = 0;
        long fAt = 0;
        int base0 = 0;
        long voxels = 0;
        long withCaves = 0;
        long raw = 0;
        long above = 0;
        foreach (TileMesh tm in results)
        {
            voxels += tm.Voxels;
            withCaves += tm.HasCaves ? 1 : 0;
            raw += tm.FacesRaw;
            above += tm.FacesAbove;
            for (int q = 0; q < tm.Anchor.Count; q++)
            {
                anchor[base0 + q] = tm.Anchor[q];
            }
            foreach (double x in tm.Verts)
            {
                verts[vAt++] = x;
            }
            for (int q = 0; q < tm.Faces.Count; q += 3)
            {
                // 값이 커지는 쪽(동굴 밖)을 앞면으로 둔 삼각형을 뒤집어 빈 곳 쪽으로.
                faces[fAt++] = tm.Faces[q] + base0;
                faces[fAt++] = tm.Faces[q + 2] + base0;
                faces[fAt++] = tm.Faces[q + 1] + base0;
            }
            base0 += tm.Verts.Count / 3;
        }
        // 회랑 가장자리에 놓인 꼭짓점도 바깥과 닿는 점입니다 (회랑 밖으로 이어지는 굴).
        double eps = 1e-6 * v;
        for (int q = 0; q < nV; q++)
        {
            double x = verts[q * 3];
            double y = verts[(q * 3) + 1];
            if (x <= xs[0] + eps || x >= xs[^1] - eps || y <= ys[0] + eps || y >= ys[^1] - eps)
            {
                anchor[q] = true;
            }
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["tiles"] = (long)tiles.Count,
            ["tiles_with_caves"] = withCaves,
            ["voxels"] = voxels,
            ["faces_raw"] = raw,
            ["faces_above_ground"] = above,
        };
        if (faces.Length == 0)
        {
            diag["faces_unreachable"] = 0L;
            diag["faces_kept"] = 0L;
            return (null, diag);
        }
        double r0 = terrain.RadiusM;
        CaveMesh mesh = Mesh.BuildMesh(verts, faces, pts =>
        {
            int m = pts.Length / 3;
            byte[] mat = new byte[m];
            Parallelism.For(0, m, q =>
            {
                (double ux, double uy, double uz) = site.ToUnit(pts[q * 3], pts[(q * 3) + 1]);
                mat[q] = terrain.Bedrock(terrain.Column(ux, uy, uz), pts[(q * 3) + 2]).Rock;
            });
            return mat;
        }, frame, v, anchor);
        diag["faces_unreachable"] = (faces.Length / 3) - (long)mesh.FaceCount;
        diag["faces_kept"] = (long)mesh.FaceCount;
        return (mesh, diag);
    }

    private static TileMesh CaveTile(NoiseTerrain terrain, NoiseSite site, double[] xs, double[] ys, (int Ia, int Ib, int Ja, int Jb) tile, double v)
    {
        (int ia, int ib, int ja, int jb) = tile;
        var output = new TileMesh();
        int ntx = ib - ia + 1;
        int nty = jb - ja + 1;
        int nc = ntx * nty;
        double[] ux = new double[nc];
        double[] uy = new double[nc];
        double[] uz = new double[nc];
        double[] s = new double[nc];
        for (int i = 0; i < ntx; i++)
        {
            for (int j = 0; j < nty; j++)
            {
                int q = (i * nty) + j;
                (ux[q], uy[q], uz[q]) = site.ToUnit(xs[ia + i], ys[ja + j]);
                s[q] = terrain.Surface(ux[q], uy[q], uz[q], v).HeightM;
            }
        }
        double clip = Mesh.SdfClipVoxels * v;
        double maxDepth = terrain.CaveMaxDepthM;
        int k0 = (int)Math.Floor((s.Min() - maxDepth - (2.0 * v)) / v);
        int k1 = (int)Math.Ceiling((s.Max() + (3.0 * v)) / v);
        int nz = k1 - k0 + 1;
        double[] f = new double[nc * nz];
        double fmin = double.PositiveInfinity;
        double r0 = terrain.RadiusM;
        for (int q = 0; q < nc; q++)
        {
            for (int k = 0; k < nz; k++)
            {
                double z = (k0 + k) * v;
                double depth = s[q] - z;
                double d = clip;
                if (depth >= -3.0 * v && depth <= maxDepth + (2.0 * v))
                {
                    double rr = r0 + z;
                    d = Math.Clamp(terrain.CaveDistance(ux[q] * rr, uy[q] * rr, uz[q] * rr, depth, clip), -clip, clip);
                }
                f[(q * nz) + k] = d;
                fmin = Math.Min(fmin, d);
            }
        }
        output.Voxels = f.Length;
        if (fmin >= 0.0)
        {
            return output;
        }
        (List<double> vt, List<int> fc) = Mesh.IsoSurface(f, ntx, nty, nz, v);
        if (fc.Count == 0)
        {
            return output;
        }
        output.HasCaves = true;
        output.FacesRaw = fc.Count / 3;
        int nv = vt.Count / 3;
        double oz = k0 * v;
        bool[] usedKept = new bool[nv];
        bool[] usedDropped = new bool[nv];
        var kept = new List<int>(fc.Count);
        for (int q = 0; q < fc.Count; q += 3)
        {
            int a = fc[q];
            int b = fc[q + 1];
            int c = fc[q + 2];
            double fx = (vt[a * 3] + vt[b * 3] + vt[c * 3]) / (3.0 * v);
            double fy = (vt[(a * 3) + 1] + vt[(b * 3) + 1] + vt[(c * 3) + 1]) / (3.0 * v);
            double fz = oz + ((vt[(a * 3) + 2] + vt[(b * 3) + 2] + vt[(c * 3) + 2]) / 3.0);
            // 조각 칸 지표 높이를 두 방향으로 곧게 이어 무게중심 아래 지표 높이를 얻습니다.
            int i0 = Math.Clamp((int)Math.Floor(fx), 0, Math.Max(ntx - 2, 0));
            int j0 = Math.Clamp((int)Math.Floor(fy), 0, Math.Max(nty - 2, 0));
            int i1 = Math.Min(i0 + 1, ntx - 1);
            int j1 = Math.Min(j0 + 1, nty - 1);
            double tx = Math.Clamp(fx - i0, 0.0, 1.0);
            double ty = Math.Clamp(fy - j0, 0.0, 1.0);
            double s0 = s[(i0 * nty) + j0] + (tx * (s[(i1 * nty) + j0] - s[(i0 * nty) + j0]));
            double s1 = s[(i0 * nty) + j1] + (tx * (s[(i1 * nty) + j1] - s[(i0 * nty) + j1]));
            double sAt = s0 + (ty * (s1 - s0));
            if (fz < sAt)
            {
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
                usedKept[a] = usedKept[b] = usedKept[c] = true;
            }
            else
            {
                usedDropped[a] = usedDropped[b] = usedDropped[c] = true;
                output.FacesAbove++;
            }
        }
        double ox = xs[ia];
        double oy = ys[ja];
        for (int q = 0; q < nv; q++)
        {
            output.Verts.Add(vt[q * 3] + ox);
            output.Verts.Add(vt[(q * 3) + 1] + oy);
            output.Verts.Add(vt[(q * 3) + 2] + oz);
            // 지표 위라서 버린 삼각형과 꼭짓점을 나눠 쓰는 곳: 동굴이 지표로 열린 가장자리.
            output.Anchor.Add(usedKept[q] && usedDropped[q]);
        }
        output.Faces = kept;
        return output;
    }

    // 지표에 열린 동굴 구멍(cave_mouth 가 음수인 표본이 이어진 덩어리)마다 입구 하나. 큰 구멍부터 MaxEntrances 개.
    private static (List<object?> Inside, List<object?> Outside, int NMouths) Entrances(
        NoiseTerrain terrain, NoiseSite site, double[] gx, double[] gy, double[] surf, double[] mouth, int nrow, int ncol, double v,
        EngineFrame frame)
    {
        int np = mouth.Length;
        int[] comp = new int[np];
        Array.Fill(comp, -1);
        var groups = new List<(int Size, int Deepest)>();
        var stack = new Stack<int>();
        for (int q0 = 0; q0 < np; q0++)
        {
            if (mouth[q0] >= 0.0 || comp[q0] >= 0)
            {
                continue;
            }
            int id = groups.Count;
            int size = 0;
            int deepest = q0;
            comp[q0] = id;
            stack.Push(q0);
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                size++;
                if (mouth[q] < mouth[deepest] || (mouth[q] == mouth[deepest] && q < deepest))
                {
                    deepest = q;
                }
                int r = q / ncol;
                int c = q % ncol;
                foreach ((int rr, int cc) in new[] { (r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1) })
                {
                    if (rr < 0 || rr >= nrow || cc < 0 || cc >= ncol)
                    {
                        continue;
                    }
                    int p = (rr * ncol) + cc;
                    if (mouth[p] < 0.0 && comp[p] < 0)
                    {
                        comp[p] = id;
                        stack.Push(p);
                    }
                }
            }
            groups.Add((size, deepest));
        }
        // 플레이어(반지름 0.4 m)가 지나갈 만큼 넓은 구멍만: 가장 깊은 표본이 동굴 벽에서 0.6 m 넘게 안쪽.
        var chosen = groups.Where(g => mouth[g.Deepest] < -0.6)
            .OrderByDescending(g => g.Size).ThenBy(g => g.Deepest).Take(MaxEntrances).ToList();
        var inside = new List<object?>();
        var outside = new List<object?>();
        double r0 = terrain.RadiusM;
        foreach ((int _, int q) in chosen)
        {
            double x = gx[q];
            double y = gy[q];
            (double ux, double uy, double uz) = site.ToUnit(x, y);
            double zIn = surf[q] - 2.0;
            for (int k = 1; k <= 12; k++)
            {
                double z = surf[q] - k;
                double rr = r0 + z;
                if (terrain.CaveDistance(ux * rr, uy * rr, uz * rr, k, 5.0) < -0.4)
                {
                    zIn = z;
                    break;
                }
            }
            int r = q / ncol;
            int c = q % ncol;
            // 높이맵 행은 북 → 남, 열은 서 → 동. 지표 기울기의 반대쪽(내리막)으로 나갑니다.
            double dzE = (surf[(r * ncol) + Math.Min(c + 1, ncol - 1)] - surf[(r * ncol) + Math.Max(c - 1, 0)]) / (2.0 * v);
            double dzN = (surf[(Math.Max(r - 1, 0) * ncol) + c] - surf[(Math.Min(r + 1, nrow - 1) * ncol) + c]) / (2.0 * v);
            double gl = Math.Sqrt((dzE * dzE) + (dzN * dzN));
            (double de, double dn) = gl > 1e-9 ? (-dzE / gl, -dzN / gl) : (0.0, -1.0);
            double xo = x + (12.0 * de);
            double yo = y + (12.0 * dn);
            (double oux, double ouy, double ouz) = site.ToUnit(xo, yo);
            double zo = terrain.Surface(oux, ouy, ouz, v).HeightM + 4.0;
            double[] ai = frame.ToEngine([x, y, zIn]);
            double[] bo = frame.ToEngine([xo, yo, zo]);
            inside.Add(ai.Select(w => (object?)Math.Round(w, 3)).ToList());
            outside.Add(bo.Select(w => (object?)Math.Round(w, 3)).ToList());
        }
        return (inside, outside, groups.Count);
    }

    // 재질 부피: B-PCG 굽기와 같은 4 m × 4 m × 2 m 상자, 같은 깊이(지하수 최대 깊이 + 통로 반지름 두 배)와 같은 번호.
    private static OrderedDictionary<string, object?> Strata(
        NoiseTerrain terrain, NoiseSite site, (double XMin, double XMax, double YMin, double YMax) rect, double voxel, EngineFrame frame,
        Config cfg, string outDir)
    {
        (double[] xs, double[] ys) = Corridor.Grid(rect, Corridor.StrataDxM);
        (double[] px, double[] py) = Corridor.Mesh2(xs, ys);
        int ncol = xs.Length;
        int nrow = ys.Length;
        int np = px.Length;
        double depthAll = cfg.F("groundwater.max_depth_m") + (2.0 * cfg.F("caves.passage_radius_m"));
        int nz = (int)Math.Ceiling(depthAll / Corridor.StrataDzM);
        double[] top = new double[np];
        double[] valley = new double[np];
        double[] zgw = new double[np];
        double[] ux = new double[np];
        double[] uy = new double[np];
        double[] uz = new double[np];
        var cols = new RockColumn[np];
        Parallelism.For(0, np, q =>
        {
            (ux[q], uy[q], uz[q]) = site.ToUnit(px[q], py[q]);
            // 윗면은 회랑 높이맵과 같은 간격(voxel)으로 읽어, 지형 색을 고르는 층 깊이가 높이맵 지표와 맞게 합니다.
            SurfacePoint sp = terrain.Surface(ux[q], uy[q], uz[q], voxel);
            top[q] = sp.HeightM;
            valley[q] = sp.Valley;
            zgw[q] = terrain.WaterTable(ux[q], uy[q], uz[q], sp.HeightM, voxel);
            cols[q] = terrain.Column(ux[q], uy[q], uz[q]);
        });
        byte[] mat = new byte[np * nz];
        double r0 = terrain.RadiusM;
        double dz = Corridor.StrataDzM;
        double dxs = Corridor.StrataDxM;
        Parallelism.For(0, np, q =>
        {
            int r = q / ncol;
            int c = q % ncol;
            double dzE = (top[(r * ncol) + Math.Min(c + 1, ncol - 1)] - top[(r * ncol) + Math.Max(c - 1, 0)]) / (2.0 * dxs);
            double dzN = (top[(Math.Max(r - 1, 0) * ncol) + c] - top[(Math.Min(r + 1, nrow - 1) * ncol) + c]) / (2.0 * dxs);
            double slope = Math.Sqrt((dzE * dzE) + (dzN * dzN));
            for (int k = 0; k < nz; k++)
            {
                double depth = (k + 0.5) * dz;
                double z = top[q] - depth;
                double rr = r0 + z;
                byte id;
                // 재질 상자(4 m × 2 m)는 가는 굴보다 크므로, 상자 가운데가 동굴 벽에서 1 m 안쪽이면 빈 곳으로 칠해 굴이 토막 나지 않게 합니다.
                if (terrain.CaveDistance(ux[q] * rr, uy[q] * rr, uz[q] * rr, depth, 1.0) < 1.0)
                {
                    id = z < zgw[q] ? Corridor.StrataWater : Corridor.StrataAir;
                }
                else
                {
                    id = terrain.Material(cols[q], z, top[q], slope, valley[q]);
                    if (Corridor.HasBeds(id) && k >= Corridor.BedMinLayer
                        && terrain.DarkBed(cols[q], z, Corridor.BedThicknessM, Corridor.BedDarkFraction))
                    {
                        id += Corridor.BedIdOffset;
                    }
                }
                mat[(q * nz) + k] = id;
            }
        });
        return Corridor.WriteStrata(mat, top, xs, ys, nz, frame, outDir);
    }
}
