using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.IO;
using Bpcg.Numerics;

namespace Bpcg.Bake;

/// <summary>
/// 행성 전체를 지구본처럼 돌려 보기 위한 굽기 (src/bpcg/bake/globe.py). 면 기저는 (6, 3, 3) [면, (n, u, v), xyz] 행 우선.
/// </summary>
public static class Globe
{
    public const string FormatName = "bpcg-globe";
    public const int FormatVersion = 1;
    public const string GlobeJson = "globe.json";
    public const string CornersFile = "corners_elevation_m.bin";
    public const int DefaultFaceRes = 256;
    public const int MaxFaceRes = 1024;
    public const int KNearest = 4;
    public const double SeaLevelM = 0.0;
    public static readonly int[] NanRgb = [128, 128, 128];
    public const double MarkerMaxFraction = 0.01;

    private static readonly double[] PlanetToGodot = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -1.0, 0.0];
    public const string FrameRule = "godot = (planet_x, planet_z, -planet_y); planet axis z = north = Godot +Y";
    public const string FrameRuleRotated = "godot = (q_x, q_z, -q_y), q = axis_rotation · planet; planet.axis = north = Godot +Y";

    public const string Mapping =
        "equiangular: dir ∝ n + tan(a·π/4)·u + tan(b·π/4)·v; cell (row r, col c) center "
        + "a = -1 + 2(c+0.5)/N, b = -1 + 2(r+0.5)/N; corner (r, c) a = -1 + 2c/N, b = -1 + 2r/N";

    public const string MappingInverse =
        "f = argmax_f dir·n_f; a = (4/π)·atan((dir·u)/(dir·n)), b = (4/π)·atan((dir·v)/(dir·n)); "
        + "c = clamp(floor((a+1)·N/2), 0, N-1), r = clamp(floor((b+1)·N/2), 0, N-1)";

    public const string Layout = "little-endian, C order [face][row][col]; row r follows v (b), col c follows u (a)";

    public static readonly OrderedDictionary<string, object?> Sampling = new()
    {
        ["mean_k4"] = "칸 중심에서 가장 가까운 L0 칸 4개의 평균 (값 없는 칸은 빼고, 값 없음 여부는 가장 가까운 칸을 따름)",
        ["nearest"] = "칸 중심에서 가장 가까운 L0 칸의 값",
        ["nearest_fill_in_cell"] = "가장 가까운 L0 칸의 값. 그 값이 0(없음)이면 칸 안에 든 L0 칸 값 가운데 가장 큰 것 (가는 선이 끊기지 않게)",
        ["max_in_cell"] = "가장 가까운 L0 칸과 칸 안에 든 L0 칸 값 가운데 가장 큰 것 (강은 가장 큰 등급)",
        ["bits_or_in_cell"] = "가장 가까운 L0 칸과 칸 안에 든 L0 칸 값의 비트 OR (드문 칸을 남김)",
    };

    public const double MappingTolSlack = 1e-6;
    public static readonly double[] RiverClassEdgesM3PerS = [10.0, 100.0, 1000.0];
    public const double WaterTableGapMaxFraction = 0.1;
    public static readonly (double Lo, double Hi) ValleyRatioRange = (0.8, 1.25);

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>JSON 에 쓰기 좋게 유효숫자 6자리로 줄입니다 (-0.0 은 0.0) (_num).</summary>
    public static double Num(double x)
    {
        if (!double.IsFinite(x))
        {
            return x;
        }
        return double.Parse(PyFormat.General(x, 6), System.Globalization.CultureInfo.InvariantCulture) + 0.0;
    }

    private static List<object?> Rgb(int[] c) => c.Select(v => (object?)(long)v).ToList();

    private static List<object?> Rgb(byte r, byte g, byte b) => [(long)r, (long)g, (long)b];

    private static double[] MatMul3(double[] a, double[] b)
    {
        double[] o = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                o[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }
        }
        return o;
    }

    /// <summary>행성 좌표 (3,) → Godot 좌표 (to_godot). rotation 은 axis_rotation (null 이면 단위 행렬).</summary>
    public static double[] ToGodot(ReadOnlySpan<double> v, double[]? rotation = null)
    {
        double[] m = rotation is null ? PlanetToGodot : MatMul3(PlanetToGodot, rotation);
        double[] o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            o[i] = (v[0] * m[i * 3]) + (v[1] * m[(i * 3) + 1]) + (v[2] * m[(i * 3) + 2]) + 0.0;
        }
        return o;
    }

    /// <summary>자전축을 z 로, 경도 0 의 기준을 x 로 돌리는 회전 (3, 3) (axis_rotation).</summary>
    public static double[] AxisRotation(double[] axis)
    {
        double norm = axis.Length == 3 ? LinAlg.Norm3(axis) : 0.0;
        if (!(norm > 0) || axis.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("planet.axis 는 길이가 0 이 아닌 3차원 벡터여야 합니다");
        }
        double[] a = [axis[0] / norm, axis[1] / norm, axis[2] / norm];
        double[] rf = new double[3];
        rf[Hero.Domain.ArgMinAbs(a)] = 1.0;
        double ra = LinAlg.Dot3Np(rf, a);
        rf = [rf[0] - (ra * a[0]), rf[1] - (ra * a[1]), rf[2] - (ra * a[2])];
        double nr = LinAlg.Norm3(rf);
        rf = [rf[0] / nr, rf[1] / nr, rf[2] / nr];
        double[] c = new double[3];
        LinAlg.Cross3(a, rf, c);
        double[] rot = [rf[0], rf[1], rf[2], c[0], c[1], c[2], a[0], a[1], a[2]];
        return Array.ConvertAll(rot, v => (Math.Abs(v) < 1e-15 ? 0.0 : v) + 0.0);
    }

    private static bool AllCloseIdentity(double[] m, double atol = 1e-12)
    {
        for (int i = 0; i < 9; i++)
        {
            double e = i % 4 == 0 ? 1.0 : 0.0;
            if (!(Math.Abs(m[i] - e) <= atol + (1e-5 * Math.Abs(e))))
            {
                return false;
            }
        }
        return true;
    }

    private static List<object?> Matrix(double[] m) =>
        [new List<object?> { m[0], m[1], m[2] }, new List<object?> { m[3], m[4], m[5] }, new List<object?> { m[6], m[7], m[8] }];

    /// <summary>globe.json 의 frame (frame_entry).</summary>
    public static OrderedDictionary<string, object?> FrameEntry(double[] rotation) => new()
    {
        ["rule"] = AllCloseIdentity(rotation) ? FrameRule : FrameRuleRotated,
        ["units"] = "unit sphere directions",
        ["axis_rotation"] = Matrix(rotation),
        ["matrix_planet_to_godot"] = Matrix(MatMul3(PlanetToGodot, rotation)),
    };

    /// <summary>면 기저 (6, 3, 3) [면, (n, u, v), xyz] 를 만들고 검사합니다 (face_bases).</summary>
    public static double[] FaceBases(OrderedDictionary<string, object?>? faceBasis = null)
    {
        double[] b = new double[54];
        double[] Flat(string k)
        {
            if (faceBasis is null)
            {
                return k switch { "n" => Cubesphere.FaceN, "u" => Cubesphere.FaceU, _ => Cubesphere.FaceV };
            }
            if (!faceBasis.TryGetValue(k, out object? v) || v is not List<object?> rows)
            {
                throw new ArgumentException("face_basis 는 n, u, v 를 6×3 으로 가져야 합니다");
            }
            return rows.SelectMany(r => ((List<object?>)r!).Select(x => Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        }
        double[][] parts = [Flat("n"), Flat("u"), Flat("v")];
        if (parts.Any(p => p.Length != 18))
        {
            throw new ArgumentException("face_basis 모양이 (6, 3, 3) 이 아닙니다");
        }
        for (int f = 0; f < 6; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                Array.Copy(parts[k], f * 3, b, (f * 9) + (k * 3), 3);
            }
        }
        CheckBases(b);
        return b;
    }

    /// <summary>면 기저가 정규직교이고 u × v = n 이며 n 이 큐브의 여섯 방향인지 검사합니다 (check_bases).</summary>
    public static void CheckBases(double[] bases, double tol = 1e-9)
    {
        for (int f = 0; f < 6; f++)
        {
            ReadOnlySpan<double> bf = bases.AsSpan(f * 9, 9);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double d = (bf[i * 3] * bf[j * 3]) + (bf[(i * 3) + 1] * bf[(j * 3) + 1]) + (bf[(i * 3) + 2] * bf[(j * 3) + 2]);
                    if (Math.Abs(d - (i == j ? 1.0 : 0.0)) > tol)
                    {
                        throw new ArgumentException($"면 {f} 의 기저 n, u, v 가 정규직교가 아닙니다");
                    }
                }
            }
            double[] c = new double[3];
            LinAlg.Cross3(bf.Slice(3, 3), bf.Slice(6, 3), c);
            for (int k = 0; k < 3; k++)
            {
                if (Math.Abs(c[k] - bf[k]) > tol)
                {
                    throw new ArgumentException($"면 {f} 의 기저가 오른손 규칙 u × v = n 을 따르지 않습니다");
                }
            }
        }
        for (int f = 0; f < 6; f++)
        {
            int opp = 0;
            int side = 0;
            for (int g = 0; g < 6; g++)
            {
                if (g == f)
                {
                    continue;
                }
                double d = (bases[f * 9] * bases[g * 9]) + (bases[(f * 9) + 1] * bases[(g * 9) + 1]) + (bases[(f * 9) + 2] * bases[(g * 9) + 2]);
                opp += Math.Abs(d + 1.0) < tol ? 1 : 0;
                side += Math.Abs(d) < tol ? 1 : 0;
            }
            if (opp != 1 || side != 4)
            {
                throw new ArgumentException($"면 {f} 의 법선 n 이 큐브의 여섯 방향을 이루지 않습니다");
            }
        }
    }

    /// <summary>등각 사상으로 칸 중심(또는 꼭짓점) 방향 (6, R, R, 3) 단위 벡터 (face_directions).</summary>
    public static double[] FaceDirections(double[] bases, int res, bool corners = false)
    {
        int r = corners ? res + 1 : res;
        double[] t = new double[r];
        for (int i = 0; i < r; i++)
        {
            t[i] = corners ? -1.0 + (2.0 * i / res) : -1.0 + (2.0 * (i + 0.5) / res);
        }
        double[] tanT = Array.ConvertAll(t, v => Math.Tan(v * Math.PI / 4.0));
        double[] o = new double[6 * r * r * 3];
        Parallelism.For(0, 6, f =>
        {
            for (int row = 0; row < r; row++)
            {
                for (int col = 0; col < r; col++)
                {
                    double x = tanT[col];
                    double y = tanT[row];
                    int b0 = f * 9;
                    double dx = bases[b0] + (x * bases[b0 + 3]) + (y * bases[b0 + 6]);
                    double dy = bases[b0 + 1] + (x * bases[b0 + 4]) + (y * bases[b0 + 7]);
                    double dz = bases[b0 + 2] + (x * bases[b0 + 5]) + (y * bases[b0 + 8]);
                    double nrm = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                    int q = ((((f * r) + row) * r) + col) * 3;
                    o[q] = dx / nrm;
                    o[q + 1] = dy / nrm;
                    o[q + 2] = dz / nrm;
                }
            }
        });
        return o;
    }

    /// <summary>방향 (M, 3) → 그 방향을 담은 칸 (면, 행, 열) (direction_to_cell).</summary>
    public static (int[] F, int[] R, int[] C) DirectionToCell(double[] dirs, double[] bases, int res)
    {
        int m = dirs.Length / 3;
        int[] fo = new int[m];
        int[] ro = new int[m];
        int[] co = new int[m];
        Parallelism.For(0, m, i =>
        {
            ReadOnlySpan<double> d = dirs.AsSpan(i * 3, 3);
            int best = 0;
            double bestV = double.NegativeInfinity;
            for (int f = 0; f < 6; f++)
            {
                double v = (d[0] * bases[f * 9]) + (d[1] * bases[(f * 9) + 1]) + (d[2] * bases[(f * 9) + 2]);
                if (v > bestV)
                {
                    bestV = v;
                    best = f;
                }
            }
            int b0 = best * 9;
            double dn = (d[0] * bases[b0]) + (d[1] * bases[b0 + 1]) + (d[2] * bases[b0 + 2]);
            double du = (d[0] * bases[b0 + 3]) + (d[1] * bases[b0 + 4]) + (d[2] * bases[b0 + 5]);
            double dv = (d[0] * bases[b0 + 6]) + (d[1] * bases[b0 + 7]) + (d[2] * bases[b0 + 8]);
            double a = 4.0 / Math.PI * Math.Atan(du / dn);
            double b = 4.0 / Math.PI * Math.Atan(dv / dn);
            fo[i] = best;
            co[i] = (int)Math.Clamp(Math.Floor((a + 1.0) * res / 2.0), 0, res - 1);
            ro[i] = (int)Math.Clamp(Math.Floor((b + 1.0) * res / 2.0), 0, res - 1);
        });
        return (fo, ro, co);
    }

    private static double[] UnitOf(CellGraph graph)
    {
        double[] u = new double[graph.Pos.Length];
        for (int i = 0; i < graph.NCells; i++)
        {
            double nrm = LinAlg.Norm3(graph.Pos.AsSpan(i * 3, 3));
            u[i * 3] = graph.Pos[i * 3] / nrm;
            u[(i * 3) + 1] = graph.Pos[(i * 3) + 1] / nrm;
            u[(i * 3) + 2] = graph.Pos[(i * 3) + 2] / nrm;
        }
        return u;
    }

    /// <summary>문서의 사상으로 만든 칸 중심이 묶음 칸 위치와 맞는지 봅니다 (check_bundle_mapping).</summary>
    public static OrderedDictionary<string, object?> CheckBundleMapping(CellGraph graph, double[] bases, double jitter)
    {
        int n = (int)graph.Shape[1];
        double[] unit = UnitOf(graph);
        double[] centers = FaceDirections(bases, n);
        double r = graph.R!.Value;
        double ratio = double.NegativeInfinity;
        double err = double.NegativeInfinity;
        double jj = NpMath.PyMax(jitter, 0.0);
        double[] c = new double[3];
        for (int i = 0; i < graph.NCells; i++)
        {
            LinAlg.Cross3(unit.AsSpan(i * 3, 3), centers.AsSpan(i * 3, 3), c);
            double cn = Math.Sqrt((c[0] * c[0]) + (c[1] * c[1]) + (c[2] * c[2]));
            double dot = (unit[i * 3] * centers[i * 3]) + (unit[(i * 3) + 1] * centers[(i * 3) + 1]) + (unit[(i * 3) + 2] * centers[(i * 3) + 2]);
            double ang = Math.Atan2(cn, dot);
            double h = Math.Sqrt(graph.Area[i]) / r;
            double m = jj * h / Math.Sqrt(2.0);
            double tol = Math.Atan(m / Math.Max(1.0 - m, 1e-3)) + (MappingTolSlack * h);
            ratio = Math.Max(ratio, ang / tol);
            err = Math.Max(err, ang / h);
        }
        if (!(ratio <= 1.0))
        {
            throw new ArgumentException(
                $"묶음 칸 위치가 지구본 사상(행 = v, 열 = u)과 맞지 않습니다: 최대 {err:F3} 칸 어긋남 (노드 흔들기 {PyFormat.General(jitter)} 의 허용치의 {ratio:F2} 배). "
                + "묶음의 face_basis 나 셀 번호 규칙을 확인하세요");
        }
        return new OrderedDictionary<string, object?>
        {
            ["n_per_face"] = (long)n,
            ["jitter"] = jitter,
            ["max_error_cells"] = Num(err),
            ["max_error_ratio"] = Num(ratio),
        };
    }

    private static string FaceName(ReadOnlySpan<double> nPlanet)
    {
        for (int f = 0; f < 6; f++)
        {
            bool close = true;
            for (int k = 0; k < 3; k++)
            {
                double b = Cubesphere.FaceN[(f * 3) + k];
                close &= Math.Abs(nPlanet[k] - b) <= 1e-8 + (1e-5 * Math.Abs(b));
            }
            if (close)
            {
                return $"planet {Cubesphere.FaceNames[f]}";
            }
        }
        return "planet ?";
    }

    private static double Degrees(double x) => x * (180.0 / Math.PI);

    /// <summary>행성 좌표 단위 벡터 → (위도, 경도) [°] (lat_lon_deg).</summary>
    public static (double Lat, double Lon) LatLonDeg(double[] unit, double[]? rotation = null)
    {
        double nrm = LinAlg.Norm3(unit);
        double[] p = [unit[0] / nrm, unit[1] / nrm, unit[2] / nrm];
        if (rotation is not null)
        {
            double[] q = new double[3];
            for (int i = 0; i < 3; i++)
            {
                q[i] = (rotation[i * 3] * p[0]) + (rotation[(i * 3) + 1] * p[1]) + (rotation[(i * 3) + 2] * p[2]);
            }
            p = q;
        }
        return (Degrees(Math.Asin(NpMath.Clip(p[2], -1.0, 1.0))), Degrees(Math.Atan2(p[1], p[0])));
    }

    private static double ToD(object? v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>히어로 dict (행성 좌표 unit, size_m) → globe.json 의 hero (hero_entry).</summary>
    public static OrderedDictionary<string, object?>? HeroEntry(OrderedDictionary<string, object?>? hero, double[]? rotation = null)
    {
        if (hero is null || hero.Count == 0)
        {
            return null;
        }
        double[] p = hero.GetValueOrDefault("unit") is List<object?> ul ? ul.Select(ToD).ToArray() : [];
        if (p.Length != 3 || p.Any(v => !double.IsFinite(v)) || LinAlg.Norm3(p) == 0)
        {
            throw new ArgumentException("히어로 unit 은 유한한 3차원 벡터여야 합니다");
        }
        double nrm = LinAlg.Norm3(p);
        p = [p[0] / nrm, p[1] / nrm, p[2] / nrm];
        (double lat, double lon) = LatLonDeg(p, rotation);
        double? size = hero.GetValueOrDefault("size_m") is null ? null : ToD(hero["size_m"]);
        string sizeTxt = size is double s ? $"(한 변 {PyFormat.Fixed(s / 1000, 0)} km 정사각형)" : "";
        return new OrderedDictionary<string, object?>
        {
            ["unit"] = ToGodot(p, rotation).Select(v => (object?)v).ToList(),
            ["unit_planet"] = p.Select(v => (object?)v).ToList(),
            ["lat_deg"] = lat,
            ["lon_deg"] = lon,
            ["size_m"] = size,
            ["label"] = "히어로 유역",
            ["description"] = $"자세히 만든 히어로 영역{sizeTxt}의 가운데입니다. 엔진의 지형 장면(회랑)이 이 안에 있습니다. "
                + $"위도 {PyFormat.Fixed(lat, 2)}°, 경도 {PyFormat.Fixed(lon, 2)}° 입니다.",
        };
    }

    /// <summary>실행 폴더의 corridor/manifest.json 또는 hero/manifest.json 에서 히어로 자리를 찾습니다 (hero_from_run).</summary>
    public static OrderedDictionary<string, object?>? HeroFromRun(string runDir)
    {
        string cor = Path.Combine(runDir, "corridor", "manifest.json");
        if (File.Exists(cor))
        {
            var m = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(cor))!;
            var site = m.GetValueOrDefault("site") as OrderedDictionary<string, object?> ?? [];
            if (site.GetValueOrDefault("hero_center_unit") is not null)
            {
                var h = m.GetValueOrDefault("hero") as OrderedDictionary<string, object?> ?? [];
                object? size = null;
                if (h.GetValueOrDefault("shape") is List<object?> sh && sh.Count > 0 && h.GetValueOrDefault("spacing_m") is not null)
                {
                    size = sh.Max(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)) * ToD(h["spacing_m"]);
                }
                return new OrderedDictionary<string, object?>
                {
                    ["unit"] = site["hero_center_unit"],
                    ["lat_deg"] = site.GetValueOrDefault("hero_lat_deg"),
                    ["lon_deg"] = site.GetValueOrDefault("hero_lon_deg"),
                    ["size_m"] = size,
                    ["seed"] = m.GetValueOrDefault("seed"),
                    ["config_digest"] = m.GetValueOrDefault("config_digest"),
                    ["source"] = cor,
                };
            }
        }
        string heroPath = Path.Combine(runDir, "hero", "manifest.json");
        if (File.Exists(heroPath))
        {
            var m = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(heroPath))!;
            var meta = m.GetValueOrDefault("meta") as OrderedDictionary<string, object?> ?? [];
            var site = meta.GetValueOrDefault("site") as OrderedDictionary<string, object?> ?? [];
            if (site.GetValueOrDefault("center_unit") is not null)
            {
                var g = m.GetValueOrDefault("graph") as OrderedDictionary<string, object?> ?? [];
                object? size = null;
                if (g.GetValueOrDefault("shape") is List<object?> sh && sh.Count > 0 && g.GetValueOrDefault("spacing_m") is not null)
                {
                    size = sh.Max(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)) * ToD(g["spacing_m"]);
                }
                return new OrderedDictionary<string, object?>
                {
                    ["unit"] = site["center_unit"],
                    ["lat_deg"] = site.GetValueOrDefault("lat_deg"),
                    ["lon_deg"] = site.GetValueOrDefault("lon_deg"),
                    ["size_m"] = size,
                    ["seed"] = m.GetValueOrDefault("seed"),
                    ["config_digest"] = m.GetValueOrDefault("config_digest"),
                    ["source"] = heroPath,
                };
            }
        }
        return null;
    }

    /// <summary>L0 칸 값 → 지구본 칸 값 (_Resampler).</summary>
    private sealed class Resampler
    {
        private readonly KdTree _tree;
        private readonly int[] _knn;
        private readonly int _k;

        public Resampler(double[] unit, double[] bases, int res)
        {
            Res = res;
            _tree = new KdTree(unit);
            double[] centers = FaceDirections(bases, res);
            int nc = centers.Length / 3;
            _k = Math.Min(KNearest, unit.Length / 3);
            _knn = new int[nc * _k];
            Parallelism.For(0, nc, i =>
            {
                (_, int[] idx) = _tree.Query(centers.AsSpan(i * 3, 3), _k);
                Array.Copy(idx, 0, _knn, i * _k, _k);
            });
            Nearest = new int[nc];
            for (int i = 0; i < nc; i++)
            {
                Nearest[i] = _knn[i * _k];
            }
            (int[] f, int[] r, int[] c) = DirectionToCell(unit, bases, res);
            CellOfL0 = new int[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                CellOfL0[i] = (((f[i] * res) + r[i]) * res) + c[i];
            }
        }

        public int Res { get; }

        public int[] Nearest { get; }

        public int[] CellOfL0 { get; }

        public double[] Mean(double[] values)
        {
            int nc = Nearest.Length;
            double[] o = new double[nc];
            double[] row = new double[_k];
            for (int i = 0; i < nc; i++)
            {
                int cnt = 0;
                for (int q = 0; q < _k; q++)
                {
                    double v = values[_knn[(i * _k) + q]];
                    bool ok = double.IsFinite(v);
                    row[q] = ok ? v : 0.0;
                    cnt += ok ? 1 : 0;
                }
                o[i] = NpReduce.Sum(row) / Math.Max(cnt, 1);
                if (!double.IsFinite(values[_knn[i * _k]]))
                {
                    o[i] = NpMath.NaN;
                }
            }
            return o;
        }

        public T[] Pick<T>(T[] values) => Nearest.Select(i => values[i]).ToArray();

        public byte[] InCell(byte[] v, string mode)
        {
            byte[] near = Pick(v);
            byte[] acc = new byte[near.Length];
            if (mode == "bits_or_in_cell")
            {
                for (int i = 0; i < v.Length; i++)
                {
                    acc[CellOfL0[i]] |= v[i];
                }
                return near.Select((x, i) => (byte)(x | acc[i])).ToArray();
            }
            for (int i = 0; i < v.Length; i++)
            {
                acc[CellOfL0[i]] = Math.Max(acc[CellOfL0[i]], v[i]);
            }
            return mode switch
            {
                "max_in_cell" => near.Select((x, i) => Math.Max(x, acc[i])).ToArray(),
                "nearest_fill_in_cell" => near.Select((x, i) => x != 0 ? x : acc[i]).ToArray(),
                _ => throw new ArgumentException($"모르는 sampling: {mode}"),
            };
        }

        public float[] Corners(double[] z, double[] bases)
        {
            double[] d = FaceDirections(bases, Res, corners: true);
            int nd = d.Length / 3;
            int[] rep = Enumerable.Range(0, nd).ToArray();
            var dt = new KdTree(d);
            foreach ((int i, int j) in dt.QueryPairs(1e-9))
            {
                rep[j] = Math.Min(rep[j], i);
            }
            int[] uniq = rep.Distinct().OrderBy(x => x).ToArray();
            var pos = new Dictionary<int, int>();
            for (int q = 0; q < uniq.Length; q++)
            {
                pos[uniq[q]] = q;
            }
            int k = Math.Min(KNearest, z.Length);
            double[] val = new double[uniq.Length];
            Parallelism.For(0, uniq.Length, q =>
            {
                (_, int[] idx) = _tree.Query(d.AsSpan(uniq[q] * 3, 3), k);
                double[] zz = idx.Select(i => z[i]).ToArray();
                val[q] = NpReduce.Mean(zz);
            });
            float[] o = new float[nd];
            for (int i = 0; i < nd; i++)
            {
                o[i] = (float)val[pos[rep[i]]];
            }
            return o;
        }
    }

    private static List<object?> Strict(List<object?> stops)
    {
        for (int i = 1; i < stops.Count; i++)
        {
            double a = (double)((List<object?>)stops[i - 1]!)[0]!;
            double b = (double)((List<object?>)stops[i]!)[0]!;
            if (!(b > a))
            {
                throw new ArgumentException($"색표 값이 늘어나지 않습니다: {a} → {b}");
            }
        }
        return stops;
    }

    private static List<object?> Stop(double v, int[] rgb) => [v, Rgb(rgb)];

    private static List<object?> Sequential(double lo, double hi, int[][] colors)
    {
        hi = hi > lo ? hi : lo + 1.0;
        int m = colors.Length - 1;
        var stops = new List<object?>();
        for (int k = 0; k < colors.Length; k++)
        {
            stops.Add(Stop(Num(lo + ((hi - lo) * k / m)), colors[k]));
        }
        return Strict(stops);
    }

    private static List<object?> Diverging(double lo, double hi, int[][] colors)
    {
        int half = colors.Length / 2;
        int[][] neg = colors[..(half + 1)];
        int[][] pos = colors[half..];
        var stops = new List<object?>();
        if (lo < 0)
        {
            for (int k = 0; k < half; k++)
            {
                stops.Add(Stop(Num(lo * (1 - ((double)k / half))), neg[k]));
            }
        }
        stops.Add(Stop(0.0, colors[half]));
        if (hi > 0)
        {
            for (int k = 1; k <= half; k++)
            {
                stops.Add(Stop(Num(hi * k / half), pos[k]));
            }
        }
        if (stops.Count == 1)
        {
            stops.Add(Stop(1.0, pos[^1]));
        }
        return Strict(stops);
    }

    private static List<object?> ElevationColormap(double dmax, double hmax)
    {
        var stops = new List<object?>();
        foreach ((double t, int[] c) in GlobeText.OceanStops)
        {
            stops.Add(Stop(Num(t * dmax), c));
        }
        stops.Add(Stop(GlobeText.ShoreStopM, GlobeText.OceanShoreRgb));
        foreach ((double t, int[] c) in GlobeText.LandStops)
        {
            stops.Add(Stop(Num(t * hmax), c));
        }
        return Strict(stops);
    }

    private static double Pct(IEnumerable<double> values, double q, double @default)
    {
        double[] v = values.Where(double.IsFinite).ToArray();
        return v.Length > 0 ? NpStats.Percentile(v, q) : @default;
    }

    // colorsys.hsv_to_rgb
    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        if (s == 0.0)
        {
            return (v, v, v);
        }
        int i = (int)(h * 6.0);
        double f = (h * 6.0) - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - (s * f));
        double t = v * (1.0 - (s * (1.0 - f)));
        return (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private static int[] PlateRgb(int k)
    {
        double h = (k * 137.50776405) % 360.0 / 360.0;
        (double s, double v) = k % 2 == 0 ? (0.55, 0.85) : (0.70, 0.70);
        (double r, double g, double b) = HsvToRgb(h, s, v);
        return [(int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255)];
    }

    private static double? CfgFloat(Config? cfg, string section, string key)
    {
        if (cfg is null || !cfg.Contains(section) || !cfg.Sec(section).Contains(key))
        {
            return null;
        }
        try
        {
            return cfg.F($"{section}.{key}");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>면 가운데 칸 한 변 [km] = 2πR / (4·면당 칸 수) (cell_km).</summary>
    public static double CellKm(double radiusM, int nPerFace) => 2.0 * Math.PI * radiusM / (4.0 * nPerFace) / 1000.0;

    private static OrderedDictionary<string, object?> Entry(string name, string kind, string unit, string source, string sampling, IReadOnlyDictionary<string, object?> fmt)
    {
        (string label, string group, string desc, string how) = GlobeText.Text[name];
        return new OrderedDictionary<string, object?>
        {
            ["name"] = name,
            ["label"] = label,
            ["group"] = group,
            ["unit"] = unit,
            ["file"] = $"{name}.bin",
            ["dtype"] = kind == "continuous" ? "float32" : "uint8",
            ["kind"] = kind,
            ["source"] = source,
            ["sampling"] = sampling,
            ["description"] = PyFormat.Format(desc, fmt),
            ["how_to_read"] = PyFormat.Format(how, fmt),
        };
    }

    private static (OrderedDictionary<string, object?> E, Array Arr) Continuous(
        string name, double[] values, string unit, string source, List<object?> colormap, IReadOnlyDictionary<string, object?> fmt,
        bool log = false, string? nanLabel = null, double? colorBreak = null)
    {
        OrderedDictionary<string, object?> e = Entry(name, "continuous", unit, source, "mean_k4", fmt);
        double[] fin = values.Where(double.IsFinite).ToArray();
        e["min"] = ((List<object?>)colormap[0]!)[0];
        e["max"] = ((List<object?>)colormap[^1]!)[0];
        e["colormap"] = colormap;
        e["log"] = log;
        e["data_min"] = fin.Length > 0 ? Num(fin.Min()) : null;
        e["data_max"] = fin.Length > 0 ? Num(fin.Max()) : null;
        e["nan_label"] = nanLabel;
        if (colorBreak is double cb)
        {
            e["color_break"] = Num(cb);
        }
        return (e, Array.ConvertAll(values, v => (float)v));
    }

    private static (OrderedDictionary<string, object?> E, Array Arr) Categorical(
        string name, byte[] values, string source, IEnumerable<(int Id, string Label, int[] Rgb)> cats, double[] areaFrac,
        IReadOnlyDictionary<string, object?> fmt, string sampling = "nearest", string basis = "planet", string? noDataLabel = null)
    {
        OrderedDictionary<string, object?> e = Entry(name, "categorical", "", source, sampling, fmt);
        var list = new List<object?>();
        foreach ((int id, string label, int[] rgb) in cats)
        {
            list.Add(new OrderedDictionary<string, object?>
            {
                ["id"] = (long)id,
                ["label"] = label,
                ["rgb"] = Rgb(rgb),
                ["area_fraction"] = id < areaFrac.Length ? Num(areaFrac[id]) : 0.0,
            });
        }
        if (noDataLabel is not null)
        {
            if (cats.Any(c => c.Id == GlobeText.NoDataId))
            {
                throw new ArgumentException($"{name}: 범주 번호 {GlobeText.NoDataId} 는 '값 없음' 자리입니다");
            }
            list.Add(new OrderedDictionary<string, object?>
            {
                ["id"] = (long)GlobeText.NoDataId,
                ["label"] = noDataLabel,
                ["rgb"] = Rgb(NanRgb),
                ["area_fraction"] = null,
                ["no_data"] = true,
            });
        }
        e["categories"] = list;
        e["area_fraction_basis"] = basis;
        e["area_fraction_note"] = basis == "land" ? GlobeText.AreaNoteLand : GlobeText.AreaNotePlanet;
        return (e, values);
    }

    private static double[] AreaFraction(long[] ids, double[] area, int n)
    {
        int len = Math.Max(n, ids.Length > 0 ? (int)ids.Max() + 1 : 0);
        double[] w = new double[len];
        for (int i = 0; i < ids.Length; i++)
        {
            w[ids[i]] += area[i];
        }
        double total = NpMath.PyMax(NpReduce.Sum(area), 1e-300);
        return Array.ConvertAll(w, v => v / total);
    }

    private static string HydroNote(FieldSet fields, bool[] land, double? meanFraction)
    {
        if (!fields.Contains("relief_m") || !land.Any(b => b))
        {
            return "";
        }
        double[] relAll = fields.GetFloat64("relief_m");
        double[] rel = NpStats.Select(relAll, land);
        double relMed = Pct(rel, 50.0, 0.0);
        double? gap = null;
        if (fields.Contains("water_table_m") && fields.Contains("z_m"))
        {
            double[] z = fields.GetFloat64("z_m");
            double[] wt = fields.GetFloat64("water_table_m");
            var d = new List<double>();
            for (int c = 0; c < z.Length; c++)
            {
                double v = z[c] - wt[c];
                if (land[c] && double.IsFinite(v))
                {
                    d.Add(v);
                }
            }
            if (d.Count > 0 && relMed > 0)
            {
                double g = NpStats.Median(d.Select(v => Math.Max(v, 0.0)));
                gap = g < WaterTableGapMaxFraction * relMed ? g : null;
            }
        }
        double? ratio = null;
        if (fields.Contains("valley_depth_m"))
        {
            double[] vd = NpStats.Select(fields.GetFloat64("valley_depth_m"), land);
            var r = new List<double>();
            for (int i = 0; i < vd.Length; i++)
            {
                if (double.IsFinite(vd[i]) && double.IsFinite(rel[i]) && rel[i] > 0)
                {
                    r.Add(vd[i] / rel[i]);
                }
            }
            if (r.Count > 0)
            {
                double q = NpStats.Median(r);
                ratio = q >= ValleyRatioRange.Lo && q <= ValleyRatioRange.Hi ? q : null;
            }
        }
        return GlobeText.ReliefHydroNote(meanFraction, gap, ratio);
    }

    private static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b)
    {
        var o = new Dictionary<string, object?>(a);
        foreach (KeyValuePair<string, object?> kv in b)
        {
            o[kv.Key] = kv.Value;
        }
        return o;
    }

    private static (List<OrderedDictionary<string, object?>> Entries, List<Array> Arrays) BuildFields(
        FieldSet fields, Resampler rs, CellGraph graph, Config? cfg, Action<string>? log)
    {
        double[] area = graph.Area;
        bool[]? ocean = fields.Contains("is_ocean") ? (bool[])FieldSet.CastTo(fields["is_ocean"], typeof(bool)) : null;
        bool[]? oceanG = ocean is null ? null : rs.Pick(ocean);
        bool[] land = ocean is null ? Enumerable.Repeat(true, area.Length).ToArray() : Array.ConvertAll(ocean, b => !b);
        int nL0 = (int)graph.Shape[1];
        var baseFmt = new Dictionary<string, object?> { ["l0_km"] = CellKm(graph.R!.Value, nL0) };
        double? meanFraction = CfgFloat(cfg, "relief", "mean_fraction");
        var output = new List<(OrderedDictionary<string, object?>, Array)>();

        double[] LandOnly(double[] x)
        {
            double[] o = (double[])x.Clone();
            if (ocean is not null)
            {
                for (int i = 0; i < o.Length; i++)
                {
                    if (ocean[i])
                    {
                        o[i] = NpMath.NaN;
                    }
                }
            }
            return o;
        }

        (OrderedDictionary<string, object?>, Array) LandCategorical(string name, long[] ids, string source, (int, string, int[])[] cats, int nIds, IReadOnlyDictionary<string, object?> fmt)
        {
            byte[] values = rs.Pick(ids).Select(v => unchecked((byte)v)).ToArray();
            if (ocean is null)
            {
                var text = new Dictionary<string, object?> { ["ocean_note"] = "", ["area_note"] = GlobeText.PlanetAreaNote, ["land"] = "" };
                return Categorical(name, values, source, cats, AreaFraction(ids, area, nIds), Merge(fmt, text));
            }
            for (int i = 0; i < values.Length; i++)
            {
                if (oceanG![i])
                {
                    values[i] = GlobeText.NoDataId;
                }
            }
            var text2 = new Dictionary<string, object?>
            {
                ["ocean_note"] = GlobeText.OceanGeologyNote,
                ["area_note"] = GlobeText.LandAreaNote,
                ["land"] = "육지의 ",
            };
            long[] landIds = ids.Where((_, i) => land[i]).ToArray();
            double[] landArea = NpStats.Select(area, land);
            return Categorical(name, values, source, cats, AreaFraction(landIds, landArea, nIds), Merge(fmt, text2),
                basis: "land", noDataLabel: GlobeText.OceanNoGeologyLabel);
        }

        string? zName = fields.Contains("z_mean_m") ? "z_mean_m" : (fields.Contains("z_m") ? "z_m" : null);
        if (zName is not null)
        {
            double[] z = fields.GetFloat64(zName);
            double dmax = NpMath.PyMax(Pct(z.Where(v => v < 0).Select(v => -v), 99.0, 100.0), 100.0);
            double hmax = NpMath.PyMax(Pct(z.Where(v => v >= 0), 99.0, 10.0), 10.0);
            var fmt = new Dictionary<string, object?>(baseFmt)
            {
                ["dmax"] = dmax,
                ["hmax"] = hmax,
                ["z_land"] = GlobeText.ElevationLandText(zName, meanFraction),
            };
            output.Add(Continuous("elevation", rs.Mean(z), "m", zName, ElevationColormap(dmax, hmax), fmt, colorBreak: SeaLevelM));
        }
        if (fields.Contains("relief_m"))
        {
            double[] src = LandOnly(fields.GetFloat64("relief_m"));
            List<object?> cm = Sequential(0.0, NpMath.PyMax(Pct(src, 99.0, 1.0), 1.0), GlobeText.Magma);
            var fmt = new Dictionary<string, object?>(baseFmt)
            {
                ["hi"] = ((List<object?>)cm[^1]!)[0],
                ["hydro_note"] = HydroNote(fields, land, meanFraction),
            };
            output.Add(Continuous("relief_m", rs.Mean(src), "m", "relief_m", cm, fmt, nanLabel: "바다 (계산하지 않음)"));
        }
        if (fields.Contains("plate_id"))
        {
            long[] pid = (long[])FieldSet.CastTo(fields["plate_id"], typeof(long));
            int nPlates = pid.Length > 0 ? (int)pid.Max() + 1 : 0;
            if (pid.Length > 0 && (pid.Min() < 0 || nPlates > 256))
            {
                log?.Invoke($"[지구본] 경고: 판 번호가 {pid.Min()}..{pid.Max()} 라 uint8 (0..255)에 담을 수 없어 판 레이어만 건너뜁니다");
            }
            else
            {
                var cats = Enumerable.Range(0, nPlates).Select(k => (k, $"판 {k}", PlateRgb(k))).ToArray();
                output.Add(Categorical("plate_id", rs.Pick(pid).Select(v => unchecked((byte)v)).ToArray(), "plate_id", cats,
                    AreaFraction(pid, area, nPlates), new Dictionary<string, object?>(baseFmt) { ["n_plates"] = (long)nPlates }));
            }
        }
        if (fields.Contains("boundary_type"))
        {
            byte[] bt = (byte[])FieldSet.CastTo(fields["boundary_type"], typeof(byte));
            output.Add(Categorical("boundary_type", rs.InCell(bt, "nearest_fill_in_cell"), "boundary_type", GlobeText.BoundaryCats,
                AreaFraction(bt.Select(v => (long)v).ToArray(), area, 4), baseFmt, sampling: "nearest_fill_in_cell"));
        }
        if (fields.Contains("crust_type"))
        {
            byte[] ct = (byte[])FieldSet.CastTo(fields["crust_type"], typeof(byte));
            output.Add(Categorical("crust_type", rs.Pick(ct), "crust_type", GlobeText.CrustCats,
                AreaFraction(ct.Select(v => (long)v).ToArray(), area, 2), baseFmt));
        }
        if (fields.Contains("uplift_m_per_yr"))
        {
            double[] u = Array.ConvertAll(fields.GetFloat64("uplift_m_per_yr"), v => v * 1000.0);
            double lo = NpMath.PyMin(Pct(u.Where(v => v < 0), 1.0, 0.0), 0.0);
            double hi = NpMath.PyMax(Pct(u.Where(v => v > 0), 99.5, 0.0), 0.0);
            List<object?> cm = Diverging(lo, hi, GlobeText.PuOrR);
            string how = GlobeText.UpliftHow((double)((List<object?>)cm[0]!)[0]!, (double)((List<object?>)cm[^1]!)[0]!, lo < 0, hi > 0);
            output.Add(Continuous("uplift_mm_per_yr", rs.Mean(u), "mm/yr", "uplift_m_per_yr × 1000", cm,
                new Dictionary<string, object?>(baseFmt) { ["how"] = how }));
        }
        if (fields.Contains("ocean_age_myr"))
        {
            double[] age = fields.GetFloat64("ocean_age_myr");
            List<object?> cm = Sequential(0.0, NpMath.PyMax(Pct(age, 100.0, 1.0), 1.0), GlobeText.RdYlBu);
            output.Add(Continuous("ocean_age_myr", rs.Mean(age), "Myr", "ocean_age_myr", cm,
                new Dictionary<string, object?>(baseFmt) { ["hi"] = ((List<object?>)cm[^1]!)[0] }, nanLabel: "대륙 지각 (해양저가 아님)"));
        }
        if (fields.Contains("temperature_c"))
        {
            double[] t = fields.GetFloat64("temperature_c");
            double lo = Pct(t, 1.0, -1.0);
            double hi = Pct(t, 99.0, 1.0);
            List<object?> cm = Diverging(NpMath.PyMin(lo, 0.0), NpMath.PyMax(hi, 0.0), GlobeText.RdBuR);
            string how = GlobeText.TemperatureHow((double)((List<object?>)cm[0]!)[0]!, (double)((List<object?>)cm[^1]!)[0]!, Pct(t, 0.0, lo), Pct(t, 100.0, hi));
            double? lapseCfg = CfgFloat(cfg, "climate", "lapse_rate_c_per_m");
            double lapse = lapseCfg is double lc ? 1000.0 * lc : 6.5;
            output.Add(Continuous("temperature_c", rs.Mean(t), "°C", "temperature_c", cm,
                new Dictionary<string, object?>(baseFmt) { ["how"] = how, ["lapse"] = lapse }));
        }
        if (fields.Contains("precip_m_per_yr"))
        {
            double[] p = fields.GetFloat64("precip_m_per_yr");
            List<object?> cm = Sequential(Pct(p, 1.0, 0.0), Pct(p, 99.0, 1.0), GlobeText.YlGnBu);
            output.Add(Continuous("precip_m_per_yr", rs.Mean(p), "m/yr", "precip_m_per_yr", cm,
                new Dictionary<string, object?>(baseFmt) { ["lo"] = ((List<object?>)cm[0]!)[0], ["hi"] = ((List<object?>)cm[^1]!)[0] }));
        }
        if (fields.Contains("discharge_m3_per_yr"))
        {
            double[] q = fields.GetFloat64("discharge_m3_per_yr");
            double[] lq = new double[q.Length];
            for (int i = 0; i < q.Length; i++)
            {
                double qs = q[i] / Constants.SecondsPerYear;
                lq[i] = qs > 0 ? Math.Log10(qs) : NpMath.NaN;
            }
            lq = LandOnly(lq);
            List<object?> cm = Sequential(Pct(lq, 1.0, -1.0), Pct(lq, 99.5, 3.0), GlobeText.Viridis);
            (OrderedDictionary<string, object?> e, Array arr) = Continuous(
                "discharge_log10_m3_per_s", rs.Mean(lq), "log10(m³/s)", "log10(discharge_m3_per_yr / SECONDS_PER_YEAR)", cm,
                new Dictionary<string, object?>(baseFmt) { ["lo"] = ((List<object?>)cm[0]!)[0], ["hi"] = ((List<object?>)cm[^1]!)[0] },
                log: true, nanLabel: "바다");
            e["log_note"] = "값이 이미 log10(m³/s) 입니다. 실제 유량은 10^값 m³/s, 색표 값도 log10";
            output.Add((e, arr));
        }
        if (fields.Contains("surface_rock"))
        {
            var cats = Enumerable.Range(0, Rocks.NRocks)
                .Select(k => (k, GlobeText.RockLabels[k], new[] { (int)Rocks.ColorRgb[k * 3], Rocks.ColorRgb[(k * 3) + 1], Rocks.ColorRgb[(k * 3) + 2] }))
                .ToArray();
            (OrderedDictionary<string, object?> e, Array arr) = LandCategorical(
                "surface_rock", (long[])FieldSet.CastTo(fields["surface_rock"], typeof(long)), "surface_rock", cats, Rocks.NRocks, baseFmt);
            foreach (OrderedDictionary<string, object?> c in ((List<object?>)e["categories"]!).Cast<OrderedDictionary<string, object?>>())
            {
                long id = (long)c["id"]!;
                if (id < Rocks.NRocks)
                {
                    c["key"] = Rocks.RockNames[id];
                    c["soluble"] = Rocks.Soluble[id];
                }
            }
            output.Add((e, arr));
        }
        if (fields.Contains("template_id"))
        {
            output.Add(LandCategorical("geology_template", (long[])FieldSet.CastTo(fields["template_id"], typeof(long)), "template_id",
                GlobeText.TemplateCats, GlobeText.TemplateCats.Length, baseFmt));
        }
        if (fields.Contains("cave_level_0_m") || fields.Contains("cave_level_1_m"))
        {
            int n = graph.NCells;
            byte[] cave = new byte[n];
            foreach ((byte bit, string key) in new[] { ((byte)1, "cave_level_0_m"), ((byte)2, "cave_level_1_m") })
            {
                if (fields.Contains(key))
                {
                    double[] lv = fields.GetFloat64(key);
                    for (int i = 0; i < n; i++)
                    {
                        if (double.IsFinite(lv[i]))
                        {
                            cave[i] |= bit;
                        }
                    }
                }
            }
            byte[] caveG = rs.InCell(cave, "bits_or_in_cell");
            long nGlobe = caveG.Count(b => b != 0);
            bool markers = nGlobe > 0 && nGlobe <= MarkerMaxFraction * caveG.Length;
            var fmt = new Dictionary<string, object?>(baseFmt)
            {
                ["n_cave_cells"] = (long)cave.Count(b => b > 0),
                ["n_globe_cells"] = nGlobe,
                ["marker_note"] = markers ? GlobeText.MarkerNote : "",
            };
            (OrderedDictionary<string, object?> e, Array arr) = Categorical("caves", caveG, "cave_level_0_m, cave_level_1_m", GlobeText.CaveCats,
                AreaFraction(cave.Select(v => (long)v).ToArray(), area, 4), fmt, sampling: "bits_or_in_cell");
            e["point_markers"] = markers;
            e["n_nonzero_cells"] = nGlobe;
            output.Add((e, arr));
        }
        return (output.Select(t => t.Item1).ToList(), output.Select(t => t.Item2).ToList());
    }

    private static (List<OrderedDictionary<string, object?>> Entries, List<Array> Arrays) BuildOverlays(FieldSet fields, Resampler rs, Config? cfg)
    {
        var entries = new List<OrderedDictionary<string, object?>>();
        var arrays = new List<Array>();
        if (fields.Contains("is_river"))
        {
            bool[] river = (bool[])FieldSet.CastTo(fields["is_river"], typeof(bool));
            byte[] cls = new byte[river.Length];
            double[]? q = fields.Contains("discharge_m3_per_yr") ? fields.GetFloat64("discharge_m3_per_yr") : null;
            for (int i = 0; i < river.Length; i++)
            {
                if (river[i])
                {
                    cls[i] = q is null ? (byte)1 : (byte)(1 + SciPy.SearchSorted(RiverClassEdgesM3PerS, q[i] / Constants.SecondsPerYear, true));
                }
            }
            double thr = cfg is not null ? cfg.F("rivers.min_discharge_m3_per_s") : double.NaN;
            entries.Add(new OrderedDictionary<string, object?>
            {
                ["name"] = "rivers",
                ["label"] = "강",
                ["file"] = "rivers.bin",
                ["dtype"] = "uint8",
                ["source"] = "is_river, discharge_m3_per_yr",
                ["sampling"] = "max_in_cell",
                ["description"] = $"유량이 강 문턱(초당 {PyFormat.General(thr)} m³) 이상인 육지 칸입니다. 값 1~4 는 "
                    + "유량 등급이고 0 은 강이 아닙니다. L0 칸이 커서 육지 대부분이 문턱을 넘으므로, "
                    + "큰 강만 보려면 등급 3 이상만 그리면 됩니다.",
                ["how_to_read"] = "0 이 아닌 칸에 파란색을 덧칠합니다. 진한 파랑일수록 큰 강입니다.",
                ["rgb"] = Rgb(GlobeText.RiverCats[^1].Rgb!),
                ["categories"] = GlobeText.RiverCats.Select(c => (object?)new OrderedDictionary<string, object?>
                {
                    ["id"] = (long)c.Id,
                    ["label"] = c.Label,
                    ["rgb"] = c.Rgb is null ? null : Rgb(c.Rgb),
                }).ToList(),
            });
            arrays.Add(rs.InCell(cls, "max_in_cell"));
        }
        if (fields.Contains("is_lake"))
        {
            byte[] lake = Array.ConvertAll((bool[])FieldSet.CastTo(fields["is_lake"], typeof(bool)), b => b ? (byte)1 : (byte)0);
            entries.Add(new OrderedDictionary<string, object?>
            {
                ["name"] = "lakes",
                ["label"] = "호수",
                ["file"] = "lakes.bin",
                ["dtype"] = "uint8",
                ["source"] = "is_lake",
                ["sampling"] = "max_in_cell",
                ["description"] = "물이 고여 수면이 지표보다 높은 육지 칸(호수)입니다. 1 이면 호수, "
                    + $"0 이면 아닙니다. 이 행성의 호수 L0 칸은 {lake.Count(b => b == 1)}개입니다.",
                ["how_to_read"] = "1 인 칸에 하늘색을 덧칠합니다.",
                ["rgb"] = new List<object?> { 90L, 200L, 250L },
                ["categories"] = new List<object?>
                {
                    new OrderedDictionary<string, object?> { ["id"] = 0L, ["label"] = "호수 아님", ["rgb"] = null },
                    new OrderedDictionary<string, object?> { ["id"] = 1L, ["label"] = "호수", ["rgb"] = new List<object?> { 90L, 200L, 250L } },
                },
            });
            arrays.Add(rs.InCell(lake, "max_in_cell"));
        }
        return (entries, arrays);
    }

    private static long WriteBinAtomic(string path, Array arr)
    {
        byte[] data;
        if (arr is float[] f)
        {
            data = new byte[f.Length * 4];
            Buffer.BlockCopy(f, 0, data, 0, data.Length);
        }
        else
        {
            data = (byte[])arr;
        }
        string tmp = path + ".part";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
        return data.Length;
    }

    private static HashSet<string> OldFiles(string folder)
    {
        string p = Path.Combine(folder, GlobeJson);
        try
        {
            if (PyJson.Loads(File.ReadAllText(p)) is not OrderedDictionary<string, object?> m || !Equals(m.GetValueOrDefault("format"), FormatName))
            {
                return [];
            }
            var names = new HashSet<string>();
            foreach (string key in new[] { "fields", "overlays" })
            {
                if (m.GetValueOrDefault(key) is List<object?> l)
                {
                    foreach (OrderedDictionary<string, object?> e in l.OfType<OrderedDictionary<string, object?>>())
                    {
                        if (e.GetValueOrDefault("file") is string s)
                        {
                            names.Add(s);
                        }
                    }
                }
            }
            if (m.GetValueOrDefault("corners") is OrderedDictionary<string, object?> c && c.GetValueOrDefault("file") is string cf)
            {
                names.Add(cf);
            }
            return names.Where(n => !n.Contains('/') && n.EndsWith(".bin", StringComparison.Ordinal)).ToHashSet();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>굽은 파일을 다른 폴더(보통 engine/baked/globe)에 복사합니다. globe.json 은 맨 끝에 (copy_globe).</summary>
    public static void CopyGlobe(string srcDir, string dstDir, List<string> files)
    {
        Directory.CreateDirectory(dstDir);
        var stale = OldFiles(dstDir);
        stale.ExceptWith(files);
        foreach (string name in files.Append(GlobeJson))
        {
            string tmp = Path.Combine(dstDir, name + ".part");
            File.Copy(Path.Combine(srcDir, name), tmp, true);
            File.Move(tmp, Path.Combine(dstDir, name), true);
        }
        foreach (string name in stale)
        {
            File.Delete(Path.Combine(dstDir, name));
        }
    }

    /// <summary>면당 칸 수가 범위 밖일 때의 알림 글 (face_res_error).</summary>
    public static string FaceResError(int faceRes) =>
        $"face_res(면당 칸 수)는 2..{MaxFaceRes} 이어야 합니다: {faceRes}. {MaxFaceRes} 를 넘으면 꼭짓점 방향·이웃 표만 수 GB 라 "
        + "노트북 메모리가 모자라고, 엔진이 레이어 하나의 텍스처를 만드는 데도 몇 분이 걸립니다. L0 만큼 곱게 보려면 L0 면당 칸 수"
        + "(laptop 512)면 충분합니다.";

    /// <summary>행성 상태를 지구본 파일로 굽습니다 (bake_globe). 반환: globe.json 내용.</summary>
    public static OrderedDictionary<string, object?> BakeGlobe(
        PlanetState planetState, Config? cfg, string outDir, int faceRes = DefaultFaceRes, OrderedDictionary<string, object?>? hero = null,
        Action<string>? log = null, OrderedDictionary<string, object?>? faceBasis = null, string? engineDir = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        CellGraph graph = planetState.Graph;
        FieldSet fields = planetState.Fields;
        if (graph.Kind != "sphere")
        {
            throw new ArgumentException("planet_state 는 구면 그래프와 fields 를 가진 PlanetState 여야 합니다");
        }
        string zName = fields.Contains("z_mean_m") ? "z_mean_m" : (fields.Contains("z_m") ? "z_m"
            : throw new ArgumentException("행성 필드에 고도(z_mean_m 또는 z_m)가 없습니다"));
        int res = faceRes;
        if (!(res >= 2 && res <= MaxFaceRes))
        {
            throw new ArgumentException(FaceResError(faceRes));
        }
        Directory.CreateDirectory(outDir);
        var sec = new OrderedDictionary<string, double>();

        long t = Stopwatch.GetTimestamp();
        double[] bases = FaceBases(faceBasis);
        double jitter = cfg is not null ? cfg.F("landscape.jitter") : 1.0;
        double[] rotation = cfg is null ? [1, 0, 0, 0, 1, 0, 0, 0, 1] : AxisRotation(cfg.FArray("planet.axis"));
        if (!AllCloseIdentity(rotation))
        {
            log?.Invoke("[지구본] 자전축이 z 가 아니라, 자전축이 Godot +Y(북쪽)가 되도록 지구본 좌표를 돌립니다 (frame.axis_rotation)");
        }
        OrderedDictionary<string, object?> check = CheckBundleMapping(graph, bases, jitter);
        double[] unit = UnitOf(graph);
        var rs = new Resampler(unit, bases, res);
        sec["index"] = Seconds(t);
        log?.Invoke($"[지구본] 면당 {res}칸 ({6 * res * res}칸)으로 다시 담습니다. L0 면당 {check["n_per_face"]}칸, "
            + $"사상 검사 최대 {(double)check["max_error_cells"]!:F3} 칸 어긋남 (허용치의 {(double)check["max_error_ratio"]!:F3} 배), {sec["index"]:F2} s");

        t = Stopwatch.GetTimestamp();
        (List<OrderedDictionary<string, object?>> entries, List<Array> arrays) = BuildFields(fields, rs, graph, cfg, log);
        (List<OrderedDictionary<string, object?>> ovEntries, List<Array> ovArrays) = BuildOverlays(fields, rs, cfg);
        float[] corners = rs.Corners(fields.GetFloat64(zName), bases);
        sec["resample"] = Seconds(t);
        log?.Invoke($"[지구본] 필드 {entries.Count}개, 덧그림 {ovEntries.Count}개, 꼭짓점 고도를 만들었습니다: {sec["resample"]:F2} s");

        var shape = new List<object?> { 6L, (long)res, (long)res };
        foreach (OrderedDictionary<string, object?> e in entries.Concat(ovEntries))
        {
            e["shape"] = shape;
        }
        if (hero is not null && cfg is not null && hero.GetValueOrDefault("seed") is not null)
        {
            if (Convert.ToInt64(hero["seed"], System.Globalization.CultureInfo.InvariantCulture) != cfg.I("planet.seed"))
            {
                log?.Invoke($"[지구본] 히어로 시드 {hero["seed"]} 가 행성 시드와 달라 표시하지 않습니다");
                hero = null;
            }
            else if (hero.GetValueOrDefault("config_digest") is string hd && hd != cfg.Digest())
            {
                log?.Invoke("[지구본] 경고: 히어로 설정 해시가 행성과 다릅니다 (위치는 그대로 씁니다)");
            }
        }
        OrderedDictionary<string, object?>? heroE = HeroEntry(hero, rotation);

        t = Stopwatch.GetTimestamp();
        var sizes = new OrderedDictionary<string, object?>();
        foreach ((OrderedDictionary<string, object?> e, Array arr) in entries.Concat(ovEntries).Zip(arrays.Concat(ovArrays)))
        {
            string file = (string)e["file"]!;
            sizes[file] = WriteBinAtomic(Path.Combine(outDir, file), arr);
        }
        sizes[CornersFile] = WriteBinAtomic(Path.Combine(outDir, CornersFile), corners);
        HashSet<string> stale = OldFiles(outDir);
        stale.ExceptWith(sizes.Keys);
        sec["write"] = Seconds(t);

        int nL0 = (int)graph.Shape[1];
        double l0Km = CellKm(graph.R!.Value, nL0);
        double globeKm = CellKm(graph.R!.Value, res);
        var faces = new List<object?>();
        for (int f = 0; f < 6; f++)
        {
            faces.Add(new OrderedDictionary<string, object?>
            {
                ["index"] = (long)f,
                ["name"] = FaceName(bases.AsSpan(f * 9, 3)),
                ["n"] = ToGodot(bases.AsSpan(f * 9, 3), rotation).Select(x => (object?)x).ToList(),
                ["u"] = ToGodot(bases.AsSpan((f * 9) + 3, 3), rotation).Select(x => (object?)x).ToList(),
                ["v"] = ToGodot(bases.AsSpan((f * 9) + 6, 3), rotation).Select(x => (object?)x).ToList(),
            });
        }
        var secOut = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, double> kv in sec)
        {
            secOut[kv.Key] = Num(kv.Value);
        }
        var meta = new OrderedDictionary<string, object?>
        {
            ["format"] = FormatName,
            ["format_version"] = (long)FormatVersion,
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = Bundle.GitCommit(),
            ["config_digest"] = cfg?.Digest(),
            ["seed"] = cfg is null ? null : cfg.I("planet.seed"),
            ["profile"] = cfg?.Sec("profile").Get("name"),
            ["title"] = "행성 지구본",
            ["description"] = "생성한 행성 전체를 지구본처럼 돌려 보기 위한 자료입니다. 큐브스피어 면 6개를 "
                + $"면마다 {res}×{res} 칸으로 나눠 칸마다 지형·판·기후·물·지질·동굴 값을 담았습니다. "
                + $"원래 계산은 면당 {nL0}칸(L0)에서 했습니다. 칸 크기를 같은 잣대(면 가운데 칸 "
                + $"한 변)로 재면 L0 칸은 약 {l0Km:F1} km, 지구본 칸은 약 {globeKm:F1} km 입니다. "
                + "구의 높낮이는 꼭짓점 고도로 만들고, 색은 고른 필드로 칠합니다.",
            ["frame"] = FrameEntry(rotation),
            ["radius_m"] = graph.R!.Value,
            ["face_res"] = (long)res,
            ["faces"] = faces,
            ["handedness"] = "u × v = n (n 은 구 바깥쪽)",
            ["mapping"] = Mapping,
            ["mapping_inverse"] = MappingInverse,
            ["layout"] = Layout,
            ["sampling"] = Sampling,
            ["corners"] = new OrderedDictionary<string, object?>
            {
                ["file"] = CornersFile,
                ["dtype"] = "float32",
                ["shape"] = new List<object?> { 6L, (long)(res + 1), (long)(res + 1) },
                ["unit"] = "m",
                ["source"] = zName,
                ["min"] = Num(corners.Min()),
                ["max"] = Num(corners.Max()),
                ["description"] = $"칸 꼭짓점의 고도({zName})입니다(해수면 0 m 기준, 바다는 음수). "
                    + "꼭짓점에서 가장 가까운 L0 칸 4개의 평균이고, 이웃 면이 같이 쓰는 꼭짓점은 같은 "
                    + "값이라 구 메시에 틈이 생기지 않습니다. 실제 높이는 반지름에 비해 아주 작으므로"
                    + "(0.1% 안팎) 보이게 하려면 과장 배율을 곱합니다.",
            },
            ["sea_level_m"] = SeaLevelM,
            ["nan_rgb"] = Rgb(NanRgb),
            ["nan_label"] = "값 없음",
            ["groups"] = GlobeText.Groups.Cast<object?>().ToList(),
            ["fields"] = entries.Cast<object?>().ToList(),
            ["overlays"] = ovEntries.Cast<object?>().ToList(),
            ["hero"] = heroE,
            ["source"] = new OrderedDictionary<string, object?>
            {
                ["n_per_face"] = (long)nL0,
                ["spacing_m"] = graph.Spacing,
                ["cell_km_face_center"] = Num(l0Km),
                ["elevation_field"] = zName,
                ["mapping_check"] = check,
            },
            ["file_bytes"] = sizes,
            ["seconds"] = secOut,
        };
        double total = Seconds(tAll);
        secOut["total"] = Num(total);
        Bundle.WriteJson(Path.Combine(outDir, GlobeJson), meta, maxArray: null);
        foreach (string name in stale)
        {
            File.Delete(Path.Combine(outDir, name));
        }
        double totalMb = sizes.Values.Sum(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)) / 1e6;
        log?.Invoke($"[지구본] 파일 {sizes.Count + 1}개 ({totalMb:F1} MB)를 썼습니다: {outDir}");
        if (engineDir is not null)
        {
            CopyGlobe(outDir, engineDir, sizes.Keys.ToList());
            log?.Invoke($"[지구본] 엔진 폴더에도 썼습니다: {engineDir}");
        }
        log?.Invoke($"[지구본] 끝: {total:F2} s");
        return meta;
    }
}
