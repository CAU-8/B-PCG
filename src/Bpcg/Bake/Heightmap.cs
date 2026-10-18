using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bpcg.IO;

namespace Bpcg.Bake;

/// <summary>
/// 높이맵을 Godot 엔진이 바로 읽는 원시 파일 한 쌍(.bin float32 LE + .json)으로 굽고 읽습니다 (src/bpcg/bake/heightmap.py).
/// </summary>
public static class Heightmap
{
    public const string FormatName = "float32_le";
    public const string Layout = "row_major";
    public const string Axes = "x_east_y_up_z_south";

    private static readonly string[] RequiredKeys = ["format", "width", "height", "spacing_m", "min", "max", "origin"];

    /// <summary>확장자를 뺀 경로에서 .bin 과 .json 경로 (stem_paths).</summary>
    public static (string Bin, string Json) StemPaths(string pathStem) => (pathStem + ".bin", pathStem + ".json");

    private static void WriteAtomic(string path, byte[] payload)
    {
        string tmp = path + ".part";
        File.WriteAllBytes(tmp, payload);
        File.Move(tmp, path, true);
    }

    /// <summary>
    /// 높이맵 z (rows, cols) 행 우선 [m] 을 &lt;stem&gt;.bin 과 &lt;stem&gt;.json 으로 씁니다 (write_heightmap). 반환: .json 내용.
    /// </summary>
    public static OrderedDictionary<string, object?> WriteHeightmap(string pathStem, double[] z, int rows, int cols, double spacingM, double[]? origin = null)
    {
        double[] o = CheckHeightmap(z.Length, rows, cols, spacingM, origin);
        return WriteFloats(pathStem, Array.ConvertAll(z, v => (float)v), rows, cols, spacingM, o);
    }

    /// <summary>
    /// 이미 float32 인 높이(예: 저장 정밀도로 둔 z_m)를 그대로 씁니다. double 로 넓혔다가 다시 float 로 줄이는 것과 같은 바이트입니다.
    /// </summary>
    public static OrderedDictionary<string, object?> WriteHeightmap(string pathStem, float[] z, int rows, int cols, double spacingM, double[]? origin = null)
    {
        double[] o = CheckHeightmap(z.Length, rows, cols, spacingM, origin);
        return WriteFloats(pathStem, z, rows, cols, spacingM, o);
    }

    // 크기·간격·원점을 확인하고 원점을 돌려줍니다 (없으면 0, 0, 0).
    private static double[] CheckHeightmap(int length, int rows, int cols, double spacingM, double[]? origin)
    {
        if (length != rows * cols)
        {
            throw new ArgumentException("높이맵은 2차원 배열이어야 합니다");
        }
        if (rows < 2 || cols < 2)
        {
            throw new ArgumentException($"높이맵은 한 변이 2 표본 이상이어야 합니다 (받은 크기: ({rows}, {cols}))");
        }
        if (!(double.IsFinite(spacingM) && spacingM > 0.0))
        {
            throw new ArgumentException($"spacing_m 은 0 보다 큰 유한한 값이어야 합니다 (받은 값: {spacingM})");
        }
        double[] o = origin ?? [0.0, 0.0, 0.0];
        if (o.Length != 3 || o.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("origin 은 유한한 값 3개 (x, y, z) 여야 합니다");
        }
        return o;
    }

    // float32 표본을 .bin 으로, 정보를 .json 으로 씁니다 (data 는 읽기만 함).
    private static OrderedDictionary<string, object?> WriteFloats(
        string pathStem, float[] data, int rows, int cols, double spacingM, double[] o)
    {
        int bad = data.Count(v => !float.IsFinite(v));
        if (bad > 0)
        {
            throw new ArgumentException(
                $"높이맵에 NaN 이나 무한대 표본이 {bad}개 있습니다 (float32 범위를 넘는 값도 무한대가 됩니다). 굽기 전에 채우거나 잘라 내야 합니다.");
        }
        var meta = new OrderedDictionary<string, object?>
        {
            ["format"] = FormatName,
            ["layout"] = Layout,
            ["axes"] = Axes,
            ["width"] = (long)cols,
            ["height"] = (long)rows,
            ["spacing_m"] = spacingM,
            ["origin"] = o.Select(v => (object?)v).ToList(),
            ["min"] = (double)data.Min(),
            ["max"] = (double)data.Max(),
            ["bpcg_version"] = Package.Version,
        };
        (string binPath, string jsonPath) = StemPaths(pathStem);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(binPath));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        byte[] bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("리틀 엔디언 기기만 지원합니다");
        }
        WriteAtomic(binPath, bytes);
        string text = PyJson.Dumps(meta, new PyJson.Options(Indent: 2, EnsureAscii: false)) + "\n";
        WriteAtomic(jsonPath, new System.Text.UTF8Encoding(false).GetBytes(text));
        return meta;
    }

    /// <summary>write_heightmap 이 쓴 파일 한 쌍을 읽어 (z (height·width) float32, meta) 를 돌려줍니다 (read_heightmap).</summary>
    public static (float[] Z, int Height, int Width, OrderedDictionary<string, object?> Meta) ReadHeightmap(string pathStem)
    {
        (string binPath, string jsonPath) = StemPaths(pathStem);
        var meta = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(jsonPath))!;
        var missing = RequiredKeys.Where(k => !meta.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"{Path.GetFileName(jsonPath)} 에 필드가 없습니다: {string.Join(", ", missing)}");
        }
        if (!Equals(meta["format"], FormatName))
        {
            throw new ArgumentException($"지원하지 않는 형식입니다: '{meta["format"]}' (읽을 수 있는 형식: {FormatName})");
        }
        int width = Convert.ToInt32(meta["width"], System.Globalization.CultureInfo.InvariantCulture);
        int height = Convert.ToInt32(meta["height"], System.Globalization.CultureInfo.InvariantCulture);
        byte[] raw = File.ReadAllBytes(binPath);
        if (raw.Length / 4 != width * height)
        {
            throw new ArgumentException(
                $"{Path.GetFileName(binPath)} 의 표본 수 {raw.Length / 4} 가 width * height = {width * height} 와 다릅니다");
        }
        float[] z = new float[width * height];
        Buffer.BlockCopy(raw, 0, z, 0, raw.Length);
        return (z, height, width, meta);
    }
}
