using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bpcg.Bake;
using Bpcg.IO;

namespace Bpcg.Compare;

/// <summary>
/// 고도 그림과 연산 과정 그림 (PNG 한 장씩).
/// </summary>
/// <remarks>
/// 모든 방법을 같은 빛(북서 315°, 고도각 45°), 같은 과장(1배), 같은 색표로 그립니다. Galin 2019 6.2 가 지적하듯 시점·양식이
/// 다르면 사실감 비교가 흔들리기 때문입니다. 음영은 matplotlib LightSource 의 hillshade 와 'soft' 섞기 식을 따릅니다.
/// 그림은 요청마다 한 장만 그리고, 프로세스 안에서 잠금으로 한 번에 하나씩만 그립니다. 글자는 그림에 넣지 않고 범례 값은
/// 화면이 따로 보여 줍니다(<see cref="Legend"/>).
/// </remarks>
public static class CompareRender
{
    public const int StyleVersion = 1;
    public const double AzimuthDeg = 315.0;
    public const double AltitudeDeg = 45.0;
    public const int MinPixels = 768; // 한 변이 이보다 작으면 정수배로 키움 (가장 가까운 칸)

    /// <summary>고도 색표: 낮음 → 높음 (물을 뜻하는 파랑은 쓰지 않음).</summary>
    public static readonly string[] TerrainStops = ["#2f6b4f", "#5f9a5a", "#a7c27a", "#d9cf8f", "#c49a6c", "#8f6a52", "#e8e4df"];

    /// <summary>증감 색표 (ColorBrewer RdBu 를 뒤집음: 음수 파랑, 양수 빨강).</summary>
    private static readonly string[] DivergingStops =
        ["#053061", "#2166ac", "#4393c3", "#92c5de", "#d1e5f0", "#f7f7f7", "#fddbc7", "#f4a582", "#d6604d", "#b2182b", "#67001f"];

    private static readonly (double R, double G, double B) NanRgb = (0.55, 0.55, 0.57);
    private static readonly Dictionary<int, (double R, double G, double B)> MaskRgb = new()
    {
        [1] = (0.12, 0.47, 0.71),
        [2] = (0.09, 0.75, 0.81),
        [3] = (0.84, 0.15, 0.16),
    };

    private static readonly object Gate = new();

    private static (double R, double G, double B) Hex(string h) => (
        int.Parse(h.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
        int.Parse(h.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
        int.Parse(h.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);

    private static string ToHex((double R, double G, double B) c) =>
        $"#{(int)Math.Round(Math.Clamp(c.R, 0, 1) * 255):x2}{(int)Math.Round(Math.Clamp(c.G, 0, 1) * 255):x2}{(int)Math.Round(Math.Clamp(c.B, 0, 1) * 255):x2}";

    /// <summary>색 마디 사이 선형 보간 (t 는 [0, 1] 로 자름).</summary>
    private static (double R, double G, double B) Stops(string[] stops, double t)
    {
        t = Math.Clamp(double.IsNaN(t) ? 0.5 : t, 0.0, 1.0) * (stops.Length - 1);
        int i = Math.Min((int)t, stops.Length - 2);
        double f = t - i;
        (double r0, double g0, double b0) = Hex(stops[i]);
        (double r1, double g1, double b1) = Hex(stops[i + 1]);
        return (r0 + ((r1 - r0) * f), g0 + ((g1 - g0) * f), b0 + ((b1 - b0) * f));
    }

    private static (double R, double G, double B) Viridis(double t)
    {
        double[] lut = ColormapData.Viridis;
        int nLut = lut.Length / 4;
        int k = Math.Clamp((int)(Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1) * nLut), 0, nLut - 1);
        return (lut[k * 4], lut[(k * 4) + 1], lut[(k * 4) + 2]);
    }

    private static (double R, double G, double B) Tab20(int id)
    {
        double[] lut = ColormapData.Tab20;
        int k = ((id % 20) + 20) % 20;
        return (lut[k * 4], lut[(k * 4) + 1], lut[(k * 4) + 2]);
    }

    /// <summary>matplotlib LightSource.hillshade (fraction 1, 과장 1): 빛 세기 [0, 1], (n·n,).</summary>
    public static double[] Hillshade(double[] z, int n, double dx)
    {
        double az = (90.0 - AzimuthDeg) * Math.PI / 180.0;
        double alt = AltitudeDeg * Math.PI / 180.0;
        double lx = Math.Cos(az) * Math.Cos(alt), ly = Math.Sin(az) * Math.Cos(alt), lz = Math.Sin(alt);
        double[] it = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double gx = i == 0 ? (z[(j * n) + 1] - z[j * n]) / dx
                    : i == n - 1 ? (z[(j * n) + i] - z[(j * n) + i - 1]) / dx
                    : (z[(j * n) + i + 1] - z[(j * n) + i - 1]) / (2 * dx);
                double gy = j == 0 ? (z[n + i] - z[i]) / dx
                    : j == n - 1 ? (z[(j * n) + i] - z[((j - 1) * n) + i]) / dx
                    : (z[((j + 1) * n) + i] - z[((j - 1) * n) + i]) / (2 * dx);
                gy = -gy; // 0번 행이 위(북쪽)라서 행 방향 기울기는 부호가 반대 (matplotlib 과 같음)
                double nx = -gx, ny = -gy, nz = 1.0;
                double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                it[(j * n) + i] = ((nx * lx) + (ny * ly) + (nz * lz)) / len;
            }
        }
        (double lo, double hi) = CompareGrid.MinMax(it);
        if (hi - lo > 1e-6)
        {
            for (int k = 0; k < it.Length; k++)
            {
                it[k] = Math.Clamp((it[k] - lo) / (hi - lo), 0.0, 1.0);
            }
        }
        return it;
    }

    /// <summary>고도 음영 기복 RGB (n·n·3,) [0, 1]: 색표 × 빛 세기 ('soft' 섞기).</summary>
    public static double[] Shade(double[] z, int n, double dx, double vmin, double vmax)
    {
        double[] fill = (double[])z.Clone();
        (double zlo, _) = CompareGrid.MinMax(z);
        for (int k = 0; k < fill.Length; k++)
        {
            if (double.IsNaN(fill[k]))
            {
                fill[k] = zlo;
            }
        }
        double[] inten = Hillshade(fill, n, dx);
        double span = vmax > vmin ? vmax - vmin : 1.0;
        double[] rgb = new double[n * n * 3];
        for (int k = 0; k < n * n; k++)
        {
            (double r, double g, double b) = Stops(TerrainStops, (fill[k] - vmin) / span);
            double s = inten[k];
            rgb[k * 3] = (2 * s * r) + ((1 - (2 * s)) * r * r);
            rgb[(k * 3) + 1] = (2 * s * g) + ((1 - (2 * s)) * g * g);
            rgb[(k * 3) + 2] = (2 * s * b) + ((1 - (2 * s)) * b * b);
        }
        return rgb;
    }

    /// <summary>단계 지도 한 장 → RGB (n·n·3,). kind·vmin·vmax 는 stages.json 의 항목.</summary>
    public static double[] StageRgb(float[] arr, string kind, double vmin, double vmax, int n, double dx, double[] zFinal)
    {
        double[] a = Array.ConvertAll(arr, v => (double)v);
        double[] rgb;
        if (kind == "height")
        {
            rgb = Shade(a, n, dx, vmin, vmax);
        }
        else if (kind == "mask")
        {
            double[] inten = Hillshade(zFinal, n, dx);
            rgb = new double[n * n * 3];
            for (int k = 0; k < n * n; k++)
            {
                double g = 0.35 + (0.6 * inten[k]);
                (double R, double G, double B) c = (g, g, g);
                if (!double.IsNaN(a[k]) && MaskRgb.TryGetValue((int)Math.Round(a[k]), out (double R, double G, double B) m))
                {
                    c = ((0.25 * g) + (0.75 * m.R), (0.25 * g) + (0.75 * m.G), (0.25 * g) + (0.75 * m.B));
                }
                (rgb[k * 3], rgb[(k * 3) + 1], rgb[(k * 3) + 2]) = c;
            }
            return rgb;
        }
        else
        {
            rgb = new double[n * n * 3];
            double m = Math.Max(Math.Max(Math.Abs(vmin), Math.Abs(vmax)), 1e-12);
            for (int k = 0; k < n * n; k++)
            {
                (double R, double G, double B) c = kind switch
                {
                    "field" or "log" => Viridis(vmax > vmin ? (a[k] - vmin) / (vmax - vmin) : 0.0),
                    "diverging" => Stops(DivergingStops, 0.5 + (0.5 * a[k] / m)),
                    "category" => double.IsNaN(a[k]) || a[k] < 0 ? NanRgb : Tab20((int)Math.Round(a[k])),
                    _ => throw new ArgumentException($"모르는 단계 종류입니다: {kind}"),
                };
                (rgb[k * 3], rgb[(k * 3) + 1], rgb[(k * 3) + 2]) = c;
            }
        }
        for (int k = 0; k < n * n; k++)
        {
            if (double.IsNaN(a[k]))
            {
                (rgb[k * 3], rgb[(k * 3) + 1], rgb[(k * 3) + 2]) = NanRgb;
            }
        }
        return rgb;
    }

    /// <summary>RGB (n·n·3,) 를 정수배로 키워 PNG 로 씁니다 (임시 파일에 쓰고 바꿈).</summary>
    public static void SavePng(string path, double[] rgb, int n)
    {
        int scale = Math.Max(1, (int)Math.Ceiling((double)MinPixels / n));
        int w = n * scale;
        byte[] px = new byte[w * w * 4];
        for (int y = 0; y < w; y++)
        {
            int j = y / scale;
            for (int x = 0; x < w; x++)
            {
                int k = (j * n) + (x / scale);
                int o = ((y * w) + x) * 4;
                px[o] = (byte)Math.Round(Math.Clamp(rgb[k * 3], 0, 1) * 255);
                px[o + 1] = (byte)Math.Round(Math.Clamp(rgb[(k * 3) + 1], 0, 1) * 255);
                px[o + 2] = (byte)Math.Round(Math.Clamp(rgb[(k * 3) + 2], 0, 1) * 255);
                px[o + 3] = 255;
            }
        }
        string tmp = path + ".tmp";
        Png.WriteRgba(tmp, px, w, w);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Key(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..10];

    /// <summary>결과 폴더의 z.npy 로 고도 그림을 만듭니다(이미 있으면 그대로). vmin·vmax 가 null 이면 이 지형의 범위. 반환: PNG 경로.</summary>
    public static string RenderElevation(string entryDir, double dx, double? vmin = null, double? vmax = null)
    {
        string zPath = Path.Combine(entryDir, "z.npy");
        string key = Key($"{StyleVersion}|{vmin?.ToString("R", CultureInfo.InvariantCulture)}|{vmax?.ToString("R", CultureInfo.InvariantCulture)}|{dx.ToString("R", CultureInfo.InvariantCulture)}");
        string outPath = Path.Combine(entryDir, $"elevation_{key}.png");
        lock (Gate)
        {
            if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(zPath))
            {
                return outPath;
            }
            NpyArray npy = Npy.ReadFile(zPath);
            int n = (int)npy.Shape[0];
            double[] z = Array.ConvertAll(npy.AsFloat(), v => (double)v);
            (double lo, double hi) = CompareGrid.MinMax(z);
            foreach (string old in Directory.GetFiles(entryDir, "elevation_*.png"))
            {
                if (old != outPath)
                {
                    File.Delete(old);
                }
            }
            SavePng(outPath, Shade(z, n, dx, vmin ?? lo, vmax ?? hi), n);
        }
        return outPath;
    }

    /// <summary>연산 과정 i 번째 단계 그림 (이미 있으면 그대로). 반환: PNG 경로.</summary>
    public static string RenderStage(string entryDir, int i, double dx)
    {
        string stDir = Path.Combine(entryDir, "stages");
        string listPath = Path.Combine(stDir, "stages.json");
        if (!File.Exists(listPath))
        {
            throw new ArgumentException($"연산 과정 기록이 없습니다: {entryDir}");
        }
        var listing = (List<object?>)PyJson.Loads(File.ReadAllText(listPath, Encoding.UTF8))!;
        if (i < 0 || i >= listing.Count)
        {
            throw new ArgumentException($"단계 번호가 범위 밖입니다: {i} (0 ~ {listing.Count - 1})");
        }
        var info = (OrderedDictionary<string, object?>)listing[i]!;
        string kind = (string)info["kind"]!;
        double vmin = Convert.ToDouble(info["vmin"], CultureInfo.InvariantCulture);
        double vmax = Convert.ToDouble(info["vmax"], CultureInfo.InvariantCulture);
        string key = Key($"{StyleVersion}|{kind}|{vmin:R}|{vmax:R}|{dx:R}")[..8];
        string outPath = Path.Combine(stDir, $"{i:00}_{key}.png");
        string src = Path.Combine(stDir, $"{i:00}.npy");
        lock (Gate)
        {
            if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(src))
            {
                return outPath;
            }
            NpyArray npy = Npy.ReadFile(src);
            int n = (int)npy.Shape[0];
            double[] zFinal = Array.ConvertAll(Npy.ReadFile(Path.Combine(entryDir, "z.npy")).AsFloat(), v => (double)v);
            foreach (string old in Directory.GetFiles(stDir, $"{i:00}_*.png"))
            {
                if (old != outPath)
                {
                    File.Delete(old);
                }
            }
            SavePng(outPath, StageRgb(npy.AsFloat(), kind, vmin, vmax, n, dx, zFinal), n);
        }
        return outPath;
    }

    /// <summary>화면이 그릴 범례: 색 목록과 양 끝 값, 또는 표시 색, 또는 종류 표시.</summary>
    public static OrderedDictionary<string, object?> Legend(string kind, double vmin, double vmax)
    {
        static List<object?> Sample(Func<double, (double R, double G, double B)> f) =>
            [.. Enumerable.Range(0, 9).Select(k => (object?)ToHex(f(k / 8.0)))];
        switch (kind)
        {
            case "height":
                return new() { ["colors"] = TerrainStops.Cast<object?>().ToList(), ["vmin"] = vmin, ["vmax"] = vmax };
            case "field":
            case "log":
                return new() { ["colors"] = Sample(Viridis), ["vmin"] = vmin, ["vmax"] = vmax };
            case "diverging":
                {
                    double m = Math.Max(Math.Abs(vmin), Math.Abs(vmax));
                    return new() { ["colors"] = Sample(t => Stops(DivergingStops, t)), ["vmin"] = -m, ["vmax"] = m };
                }
            case "mask":
                {
                    var sw = new OrderedDictionary<string, object?>();
                    foreach ((int k, (double R, double G, double B) c) in MaskRgb)
                    {
                        sw[k.ToString(CultureInfo.InvariantCulture)] = ToHex(c);
                    }
                    return new() { ["swatches"] = sw };
                }
            default:
                return new() { ["categorical"] = true };
        }
    }
}
