using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Bpcg.IO;

namespace Bpcg.Engine;

/// <summary>
/// 파일 읽기 도우미. res:// 와 운영체제 경로를 모두 받도록 Godot FileAccess 로 읽습니다
/// (내보낸 게임의 res:// 는 System.IO 로 읽을 수 없음).
/// </summary>
public static class Files
{
    public static bool Exists(string path) => Godot.FileAccess.FileExists(path);

    public static byte[] Bytes(string path) => Godot.FileAccess.GetFileAsBytes(path);

    public static string Text(string path) => Godot.FileAccess.GetFileAsString(path);

    /// <summary>리틀 엔디언 float32 바이트 → float 배열.</summary>
    public static float[] Floats(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    /// <summary>float 배열 → 바이트 (Image FORMAT_RF 용).</summary>
    public static byte[] FloatBytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
}

/// <summary>
/// 구운 JSON 읽기 도우미. Python·C# 굽기가 쓴 JSON 을 <see cref="PyJson.Loads"/> 로 읽어
/// 객체는 순서 있는 사전, 배열은 List, 수는 long 또는 double 로 받습니다.
/// </summary>
public static class Json
{
    /// <summary>파일을 JSON 객체로 읽습니다. 없거나 객체가 아니거나 문법이 틀리면 null.</summary>
    public static OrderedDictionary<string, object?>? ReadObject(string path)
    {
        if (!Files.Exists(path))
        {
            return null;
        }
        try
        {
            return PyJson.Loads(Files.Text(path)) as OrderedDictionary<string, object?>;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static object? Get(OrderedDictionary<string, object?>? d, string key) =>
        d is not null && d.TryGetValue(key, out object? v) ? v : null;

    public static bool Has(OrderedDictionary<string, object?>? d, string key) =>
        d is not null && d.ContainsKey(key);

    public static OrderedDictionary<string, object?>? Obj(object? v) => v as OrderedDictionary<string, object?>;

    public static List<object?>? Arr(object? v) => v as List<object?>;

    public static bool IsNumber(object? v) => v is long or double or int or float;

    public static double D(object? v) => v switch
    {
        null => double.NaN,
        bool b => b ? 1.0 : 0.0,
        string s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    public static float F(object? v) => (float)D(v);

    public static int I(object? v) => v switch
    {
        long l => (int)l,
        double d => (int)d,
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };

    /// <summary>GDScript str(v) 처럼 글로 바꿉니다 (null 은 빈 글이 아니라 "null").</summary>
    public static string S(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>값이 있으면 글로, 없으면(null) fallback.</summary>
    public static string Str(OrderedDictionary<string, object?>? d, string key, string fallback = "") =>
        Get(d, key) is object v ? S(v) : fallback;
}
