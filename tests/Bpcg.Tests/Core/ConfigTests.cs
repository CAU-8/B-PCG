using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.IO;
using Bpcg.Tests.IO;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>
/// core/config.py 대조: 설정 순서·형·digest·manifest 글자, --set 읽기와 검사가 Python 과 같아야 합니다.
/// </summary>
public sealed class ConfigTests
{
    [Theory]
    [InlineData("tiny")]
    [InlineData("laptop")]
    [InlineData("lab")]
    public void LoadEarth(string profile)
    {
        GoldenCase g = Golden.Load("core/config", $"load_earth_{profile}");
        Config cfg = Config.LoadConfig("earth", profile);
        Assert.Equal(g.Meta.GetProperty("sorted_json").GetString(), PyJson.DumpsSorted(cfg.AsDict()));
        Assert.Equal(g.Meta.GetProperty("manifest_json").GetString(), PyJson.DumpsManifest(cfg.AsDict()) + "\n");
        // golden 의 digest 는 Python 판처럼 설정 전체의 해시입니다. 목차의 설정 해시(Config.Digest)는
        // 결과를 바꾸지 않는 키를 뺀 뒤 같은 해시 함수(HashOf)를 씁니다.
        Assert.Equal(g.Meta.GetProperty("digest").GetString(), Config.HashOf(cfg.AsDict()));
        AssertTyped(g.Meta.GetProperty("typed"), cfg.AsDict(), "config");
    }

    // 스레드 수만 다른 두 실행은 결과 파일이 바이트까지 같으므로 설정 해시도 같아야 합니다. 결과를 바꾸는 키는 설정 해시를 바꿉니다.
    [Fact]
    public void DigestIgnoresOnlyKeysThatDoNotChangeResults()
    {
        Config base0 = Config.LoadConfig("earth", "tiny");
        Assert.Equal(0L, base0["profile.compute.numba_threads"]);
        foreach (long threads in new long[] { 1, 2, 8 })
        {
            Config other = base0.WithOverrides([new("profile.compute.numba_threads", threads)]);
            Assert.Equal(base0.Digest(), other.Digest());
            Assert.NotEqual(Config.HashOf(base0.AsDict()), Config.HashOf(other.AsDict()));
        }
        Assert.NotEqual(base0.Digest(), base0.WithOverrides([new("landscape.theta", 0.5)]).Digest());
        Assert.NotEqual(base0.Digest(), base0.WithOverrides([new("planet.seed", 1L)]).Digest());

        // [compute] 절이 아예 없는 설정(스레드 수를 정하지 않으면 모든 코어)도 같은 설정 해시를 받습니다.
        OrderedDictionary<string, object?> noCompute = base0.AsDict();
        ((OrderedDictionary<string, object?>)noCompute["profile"]!).Remove("compute");
        Assert.Equal(base0.Digest(), new Config(noCompute).Digest());

        // 설정 해시를 세도 설정 자체는 바뀌지 않습니다 (목차의 config 에는 스레드 수가 그대로 남음).
        Assert.Equal(0L, base0["profile.compute.numba_threads"]);
    }

    [Fact]
    public void Overrides()
    {
        GoldenCase g = Golden.Load("core/config", "overrides");
        Config base0 = Config.LoadConfig("earth", "tiny");
        foreach (JsonElement c in g.Meta.GetProperty("cases").EnumerateArray())
        {
            var ov = (OrderedDictionary<string, object?>)FromTyped(c.GetProperty("overrides"))!;
            Config result;
            if (c.GetProperty("checked").ValueKind == JsonValueKind.Null)
            {
                result = base0.WithOverrides(ov);
                Assert.Equal(c.GetProperty("manifest_json").GetString(), PyJson.DumpsManifest(result.AsDict()) + "\n");
            }
            else
            {
                OrderedDictionary<string, object?> checkedOv = Config.CheckedOverrides(base0, ov);
                AssertTyped(c.GetProperty("checked"), checkedOv, "checked");
                result = base0.WithOverrides(checkedOv);
            }
            Assert.Equal(c.GetProperty("sorted_json").GetString(), PyJson.DumpsSorted(result.AsDict()));
            Assert.Equal(c.GetProperty("digest").GetString(), Config.HashOf(result.AsDict()));
        }
    }

    [Fact]
    public void OverrideErrors()
    {
        GoldenCase g = Golden.Load("core/config", "overrides_errors");
        Config base0 = Config.LoadConfig("earth", "tiny");
        foreach (JsonElement c in g.Meta.GetProperty("cases").EnumerateArray())
        {
            var ov = (OrderedDictionary<string, object?>)FromTyped(c.GetProperty("overrides"))!;
            string? expected = c.GetProperty("error").GetString();
            if (expected is null)
            {
                Config.CheckedOverrides(base0, ov);
                continue;
            }
            ArgumentException e = Assert.Throws<ArgumentException>(() => Config.CheckedOverrides(base0, ov));
            Assert.Equal(expected, e.Message);
        }
    }

    [Fact]
    public void ParseAssignment()
    {
        GoldenCase g = Golden.Load("core/config", "parse_assignment");
        foreach (JsonElement c in g.Meta.GetProperty("cases").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            string? error = c.GetProperty("error").GetString();
            if (error is not null)
            {
                ArgumentException e = Assert.Throws<ArgumentException>(() => Config.ParseAssignment(text));
                Assert.Equal(error, e.Message);
                continue;
            }
            (string key, object? value) = Config.ParseAssignment(text);
            Assert.Equal(c.GetProperty("key").GetString(), key);
            JsonElement tv = c.GetProperty("value");
            string kind = tv[0].GetString()!;
            if (kind is "date" or "datetime" or "time")
            {
                TomlDateTime dt = Assert.IsType<TomlDateTime>(value);
                Assert.Equal(kind, dt.Kind);
                continue;
            }
            AssertTyped(tv, value, $"parse_assignment({text})");
        }
    }

    [Fact]
    public void ConstantsAndGravity()
    {
        GoldenCase g = Golden.Load("core/constants", "gravity");
        double[] r = g.In("radius_m").AsDouble();
        double[] d = g.In("density_kg_m3").AsDouble();
        double[] got = r.Select((ri, i) => Constants.Gravity(ri, d[i])).ToArray();
        Compare.Bits(g.Out("g").AsDouble(), got, "gravity");
        Assert.Equal(Constants.GGrav, g.Meta.GetProperty("G_GRAV").GetDouble());
        Assert.Equal(Constants.SecondsPerYear, g.Meta.GetProperty("SECONDS_PER_YEAR").GetDouble());
        Assert.Equal(Constants.RhoWater, g.Meta.GetProperty("RHO_WATER").GetDouble());
        Assert.Equal(Constants.MuWater, g.Meta.GetProperty("MU_WATER").GetDouble());
        Assert.Throws<ArgumentException>(() => Constants.Gravity(0.0, 5514.0));
        Assert.Throws<ArgumentException>(() => Constants.Gravity(6.371e6, -1.0));
        Assert.Throws<ArgumentException>(() => Constants.Gravity(double.NaN, 5514.0));
        Assert.Throws<ArgumentException>(() => Constants.Gravity(6.371e6, double.PositiveInfinity));
    }

    [Fact]
    public void FieldsTable()
    {
        GoldenCase g = Golden.Load("core/fields", "fields_table");
        string[][] rows = g.Meta.GetProperty("fields").EnumerateArray()
            .Select(r => r.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
        Assert.Equal(rows.Length, Fields.All.Count);
        int i = 0;
        foreach ((string name, FieldInfo info) in Fields.All)
        {
            Assert.Equal(rows[i], new[] { name, info.Group, info.Unit, info.Dtype, info.Description });
            i++;
        }
        Assert.Equal(g.Meta.GetProperty("groups").EnumerateArray().Select(x => x.GetString()!), Fields.Groups);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Fields.CheckFields(["z_m", "zz", "Aa", "a"]));
        Assert.Equal(g.Meta.GetProperty("check_error").GetString(), e.Message);
        Assert.Equal("0.1.0", Package.Version);
    }

    /// <summary>golden 의 typed(v) ([형, 값]) 와 C# 값이 형까지 같은지.</summary>
    private static void AssertTyped(JsonElement typed, object? value, string where)
    {
        string kind = typed[0].GetString()!;
        JsonElement v = typed[1];
        switch (kind)
        {
            case "bool":
                Assert.True(value is bool, $"{where}: bool 이어야 합니다");
                Assert.Equal(v.GetBoolean(), (bool)value!);
                break;
            case "int":
                Assert.True(value is long, $"{where}: 정수(long)여야 합니다: {value}");
                Assert.Equal(long.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture), (long)value!);
                break;
            case "float":
                Assert.True(value is double, $"{where}: 실수(double)여야 합니다: {value}");
                Assert.Equal(v.GetString(), PyJson.Repr((double)value!));
                break;
            case "str":
                Assert.Equal(v.GetString(), Assert.IsType<string>(value));
                break;
            case "bare":
                Assert.Equal(v.GetString(), Assert.IsType<BareText>(value).Text);
                break;
            case "list":
                var list = Assert.IsType<List<object?>>(value);
                JsonElement[] items = v.EnumerateArray().ToArray();
                Assert.Equal(items.Length, list.Count);
                for (int i = 0; i < items.Length; i++)
                {
                    AssertTyped(items[i], list[i], $"{where}[{i}]");
                }
                break;
            case "dict":
                var dict = Assert.IsType<OrderedDictionary<string, object?>>(value);
                JsonProperty[] props = v.EnumerateObject().ToArray();
                Assert.Equal(props.Select(p => p.Name), dict.Keys);
                foreach (JsonProperty p in props)
                {
                    AssertTyped(p.Value, dict[p.Name], $"{where}.{p.Name}");
                }
                break;
            default:
                Assert.Fail($"{where}: 알 수 없는 형 {kind}");
                break;
        }
    }

    /// <summary>golden 의 typed(v) → C# 값 (덮어쓰기 입력을 Python 과 같은 형으로 만들려고).</summary>
    private static object? FromTyped(JsonElement typed)
    {
        string kind = typed[0].GetString()!;
        JsonElement v = typed[1];
        return kind switch
        {
            "bool" => v.GetBoolean(),
            "int" => long.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            "float" => ParsePyFloat(v.GetString()!),
            "str" => v.GetString(),
            "bare" => new BareText(v.GetString()!),
            "list" => v.EnumerateArray().Select(FromTyped).ToList(),
            "dict" => new OrderedDictionary<string, object?>(
                v.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, FromTyped(p.Value)))),
            _ => throw new ArgumentException($"알 수 없는 형 {kind}"),
        };
    }

    private static double ParsePyFloat(string s) => s switch
    {
        "nan" => double.NaN,
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        _ => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public void TomlBasics()
    {
        OrderedDictionary<string, object?> d = Toml.Loads(
            "a = 1\nb = 6_400.0\n[c.d]\ne = [0.02, 0.07]\n[c]\nf = 'x'\n");
        Assert.Equal(["a", "b", "c"], d.Keys.ToArray());
        Assert.Equal(1L, d["a"]);
        Assert.Equal(6400.0, d["b"]);
        var c = (OrderedDictionary<string, object?>)d["c"]!;
        Assert.Equal(["d", "f"], c.Keys.ToArray());
        Assert.Throws<TomlDecodeException>(() => Toml.Loads("﻿a = 1"));
        Assert.Throws<TomlDecodeException>(() => Toml.Loads("a = 9223372036854775808"));
        _ = IoTests.FromJson(JsonDocument.Parse("1").RootElement);
    }
}
