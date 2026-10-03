using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.IO;
using Bpcg.Numerics;

namespace Bpcg.Bake;

/// <summary>
/// 행성 면 텍스처 PNG (src/bpcg/bake/textures.py, docs/pipeline.md 11장, 궤도 장면용).
/// </summary>
public static class Textures
{
    public static readonly string[] Layers = ["elevation", "plate", "ocean_age", "precip"];
    public static readonly double[] NanRgba = [0.55, 0.55, 0.55, 1.0];
    public const double ElevationPercentile = 99.0;

    /// <summary>matplotlib Colormap.__call__ (float 입력): x·N 을 0 쪽으로 자른 번호의 조회표 값. NaN 은 (0, 0, 0, 0).</summary>
    internal static void Lookup(double[] lut, double x, Span<double> rgba)
    {
        int n = lut.Length / 4;
        if (double.IsNaN(x))
        {
            rgba.Clear();
            return;
        }
        double xa = x * n;
        if (xa == n)
        {
            xa = n - 1;
        }
        int idx = xa < 0 ? 0 : (xa >= n ? n - 1 : (int)xa);
        // TODO(port): 범위 밖 값의 under/over 색(_i_under, _i_over)은 끝 색으로 두었습니다(지금 쓰는 곳은 늘 [0, 1]).
        lut.AsSpan(idx * 4, 4).CopyTo(rgba);
    }

    /// <summary>고도 [m] → RGBA. z &lt; 0 은 바다 색표, z ≥ 0 은 육지 색표 (elevation_rgba).</summary>
    public static double[] ElevationRgba(double[] z, double depthMax, double heightMax)
    {
        double[] o = new double[z.Length * 4];
        double dm = NpMath.PyMax(depthMax, 1.0);
        double hm = NpMath.PyMax(heightMax, 1.0);
        for (int i = 0; i < z.Length; i++)
        {
            Span<double> px = o.AsSpan(i * 4, 4);
            if (z[i] < 0)
            {
                Lookup(ColormapData.BpcgOcean, NpMath.Clip(1.0 + (z[i] / dm), 0.0, 1.0), px);
            }
            else
            {
                Lookup(ColormapData.BpcgLand, NpMath.Clip(z[i] / hm, 0.0, 1.0), px);
            }
            if (!double.IsFinite(z[i]))
            {
                NanRgba.CopyTo(px);
            }
        }
        return o;
    }

    private static double[] ScalarRgba(double[] v, double[] lut, double lo, double hi)
    {
        double[] o = new double[v.Length * 4];
        double span = hi > lo ? hi - lo : 1.0;
        for (int i = 0; i < v.Length; i++)
        {
            double t = NpMath.Clip((v[i] - lo) / span, 0.0, 1.0);
            Lookup(lut, NpMath.NanToNum(t), o.AsSpan(i * 4, 4));
            if (!double.IsFinite(v[i]))
            {
                NanRgba.CopyTo(o.AsSpan(i * 4, 4));
            }
        }
        return o;
    }

    // mpimg.imsave(origin="lower"): float RGBA → (x·255) 를 0 쪽으로 자른 uint8, 행을 뒤집어 j = 0 이 맨 아래.
    private static void SaveFace(string path, double[] rgba, int faceOffset, int n)
    {
        byte[] img = new byte[n * n * 4];
        for (int j = 0; j < n; j++)
        {
            int dstRow = n - 1 - j;
            for (int i = 0; i < n; i++)
            {
                for (int ch = 0; ch < 4; ch++)
                {
                    double v = rgba[((faceOffset + (j * n) + i) * 4) + ch];
                    img[(((dstRow * n) + i) * 4) + ch] = (byte)(v * 255);
                }
            }
        }
        Png.WriteRgba(path, img, n, n, "Matplotlib version3.11.2, https://matplotlib.org/");
    }

    private static double NanMax(double[] a)
    {
        double m = double.NegativeInfinity;
        bool any = false;
        foreach (double v in a)
        {
            if (!double.IsNaN(v))
            {
                any = true;
                m = Math.Max(m, v);
            }
        }
        return any ? m : double.NaN;
    }

    /// <summary>면 6개 × 층 4개 PNG 와 textures.json 을 outDir 에 씁니다 (face_textures). 반환: textures.json 내용.</summary>
    public static OrderedDictionary<string, object?> FaceTextures(PlanetState planetState, string outDir)
    {
        CellGraph graph = planetState.Graph;
        FieldSet fields = planetState.Fields;
        if (graph.Kind != "sphere")
        {
            throw new ArgumentException("planet_state 는 구면 그래프와 fields 를 가진 PlanetState 여야 합니다");
        }
        int n = (int)graph.Shape[1];
        Directory.CreateDirectory(outDir);
        var layers = new OrderedDictionary<string, object?>();
        var skipped = new List<object?>();
        var rgbaByLayer = new OrderedDictionary<string, double[]>();
        string? zName = fields.Contains("z_mean_m") ? "z_mean_m" : (fields.Contains("z_m") ? "z_m" : null);
        if (zName is not null)
        {
            double[] z = fields.GetFloat64(zName);
            double[] sea = z.Where(v => v < 0).Select(v => -v).ToArray();
            double[] land = z.Where(v => v >= 0).ToArray();
            double depthMax = sea.Length > 0 ? NpStats.Percentile(sea, ElevationPercentile) : 1.0;
            double heightMax = land.Length > 0 ? NpStats.Percentile(land, ElevationPercentile) : 1.0;
            rgbaByLayer["elevation"] = ElevationRgba(z, depthMax, heightMax);
            layers["elevation"] = new OrderedDictionary<string, object?>
            {
                ["field"] = zName,
                ["depth_max_m"] = depthMax,
                ["height_max_m"] = heightMax,
            };
        }
        else
        {
            skipped.Add("elevation");
        }
        if (fields.Contains("plate_id"))
        {
            double[] pid = fields.GetFloat64Any("plate_id");
            double[] o = new double[pid.Length * 4];
            for (int i = 0; i < pid.Length; i++)
            {
                long k = (long)NpMath.NanToNum(pid[i]);
                Lookup(ColormapData.Tab20, (double)(((k % 20) + 20) % 20) / 19.0, o.AsSpan(i * 4, 4));
            }
            rgbaByLayer["plate"] = o;
            layers["plate"] = new OrderedDictionary<string, object?> { ["field"] = "plate_id", ["n_plates"] = (long)NanMax(pid) + 1 };
        }
        else
        {
            skipped.Add("plate");
        }
        if (fields.Contains("ocean_age_myr"))
        {
            double[] age = fields.GetFloat64("ocean_age_myr");
            double hi = age.Any(double.IsFinite) ? NanMax(age) : 1.0;
            rgbaByLayer["ocean_age"] = ScalarRgba(age, ColormapData.Viridis, 0.0, hi);
            layers["ocean_age"] = new OrderedDictionary<string, object?> { ["field"] = "ocean_age_myr", ["min"] = 0.0, ["max"] = hi };
        }
        else
        {
            skipped.Add("ocean_age");
        }
        if (fields.Contains("precip_m_per_yr"))
        {
            double[] p = fields.GetFloat64("precip_m_per_yr");
            double hi = NpStats.Percentile(p.Where(v => !double.IsNaN(v)), ElevationPercentile);
            rgbaByLayer["precip"] = ScalarRgba(p, ColormapData.YlGnBu, 0.0, hi);
            layers["precip"] = new OrderedDictionary<string, object?> { ["field"] = "precip_m_per_yr", ["min"] = 0.0, ["max"] = hi };
        }
        else
        {
            skipped.Add("precip");
        }
        foreach (KeyValuePair<string, double[]> kv in rgbaByLayer)
        {
            var files = new List<object?>();
            for (int f = 0; f < 6; f++)
            {
                string fname = $"{kv.Key}_{f}.png";
                SaveFace(Path.Combine(outDir, fname), kv.Value, f * n * n, n);
                files.Add(fname);
            }
            ((OrderedDictionary<string, object?>)layers[kv.Key]!)["files"] = files;
        }
        var meta = new OrderedDictionary<string, object?>
        {
            ["layers"] = layers,
            ["skipped"] = skipped,
            ["n_per_face"] = (long)n,
            ["orientation"] = "column i = face u (right), row j = face v (bottom to top, origin lower)",
            ["face_basis"] = new OrderedDictionary<string, object?>
            {
                ["u"] = Nested(Cubesphere.FaceU),
                ["v"] = Nested(Cubesphere.FaceV),
                ["n"] = Nested(Cubesphere.FaceN),
            },
        };
        Bundle.WriteJson(Path.Combine(outDir, "textures.json"), meta);
        return meta;
    }

    internal static List<object?> Nested(double[] flat)
    {
        var o = new List<object?>();
        for (int r = 0; r < flat.Length / 3; r++)
        {
            o.Add(new List<object?> { flat[r * 3], flat[(r * 3) + 1], flat[(r * 3) + 2] });
        }
        return o;
    }
}
