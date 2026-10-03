using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>planet golden 사례를 읽고 비교하는 도우미 (csharp/golden/export_golden.py export_planet).</summary>
internal static class PlanetGolden
{
    public static readonly TheoryData<string> MaterialsCases =
        new() { "materials_c16", "materials_c8", "materials_c8_s72", "materials_c8_p20s43" };

    public static readonly TheoryData<string> TransferCases =
        new() { "transfer_c16_f32", "transfer_c8_f12", "transfer_c8_f12_noinfo" };

    /// <summary>golden 의 config 메타 (["earth", "tiny"]) 와 overrides 로 설정을 읽습니다.</summary>
    public static Config Cfg(GoldenCase g)
    {
        string[] c = Strings(g.Meta.GetProperty("config"));
        var overrides = new List<KeyValuePair<string, object?>>();
        if (g.Meta.TryGetProperty("overrides", out JsonElement o))
        {
            foreach (JsonProperty p in o.EnumerateObject())
            {
                overrides.Add(new(p.Name, FromJson(p.Value)));
            }
        }
        return Config.LoadConfig(c[0], c[1], overrides);
    }

    /// <summary>JSON 값을 Python json.loads 와 같은 모양의 값으로 (정수 long, 실수 double, 객체는 순서 있는 dict).</summary>
    public static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new OrderedDictionary<string, object?>(
            e.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, FromJson(p.Value)))),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>meta 의 값과 C# 값을 json.dumps(sort_keys=True) 글자로 비교합니다.</summary>
    public static void AssertJson(JsonElement expected, object? actual, string what) =>
        Assert.True(
            PyJson.DumpsSorted(FromJson(expected)) == PyJson.DumpsSorted(actual),
            $"{what}: 기대 {PyJson.DumpsSorted(FromJson(expected))}, 실제 {PyJson.DumpsSorted(actual)}");

    public static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    /// <summary>prefix 가 붙은 배열들을 순서대로 FieldSet 으로 모읍니다 (order 가 있으면 그 순서).</summary>
    public static FieldSet Fields(GoldenCase g, string prefix, IEnumerable<string>? order = null)
    {
        var fs = new FieldSet();
        IEnumerable<string> names = order
            ?? g.Arrays.Keys.Where(k => k.StartsWith(prefix, System.StringComparison.Ordinal)).Select(k => k[prefix.Length..]);
        foreach (string name in names)
        {
            fs[name] = g.Arrays[prefix + name].Data;
        }
        return fs;
    }

    /// <summary>golden 의 prefix 배열을 C# FieldSet 과 비교합니다 (이름 집합이 같아야 함).</summary>
    public static void AssertFields(GoldenCase g, string prefix, FieldSet actual, string what)
    {
        string[] expectedNames = g.Arrays.Keys.Where(k => k.StartsWith(prefix, System.StringComparison.Ordinal))
            .Select(k => k[prefix.Length..]).OrderBy(k => k, System.StringComparer.Ordinal).ToArray();
        string[] actualNames = actual.Names.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedNames, actualNames);
        foreach (string name in expectedNames)
        {
            Compare.Array(g.Arrays[prefix + name], actual[name], $"{what}.{name}");
        }
    }

    /// <summary>golden 의 prefix 배열을 info dict 의 같은 이름 값과 비교합니다 (NpyArray 값은 Data 로).</summary>
    public static void AssertInfoArrays(GoldenCase g, string prefix, IReadOnlyDictionary<string, object?> info, string what)
    {
        foreach (string key in g.Arrays.Keys.Where(k => k.StartsWith(prefix, System.StringComparison.Ordinal)))
        {
            string name = key[prefix.Length..];
            Assert.True(info.ContainsKey(name), $"{what}: info 에 '{name}' 이 없습니다");
            System.Array actual = info[name] switch
            {
                NpyArray a => a.Data,
                System.Array a => a,
                object o => throw new System.InvalidCastException($"{what}.{name}: 배열이 아닙니다 ({o.GetType().Name})"),
                null => throw new System.InvalidCastException($"{what}.{name}: null"),
            };
            Compare.Array(g.Arrays[key], actual, $"{what}.{name}");
        }
    }
}
