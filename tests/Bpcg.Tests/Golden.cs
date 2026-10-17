using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests;

/// <summary>golden 사례 하나: npz 배열(입력 'in.', 출력 'out.')과 JSON meta.</summary>
public sealed class GoldenCase
{
    public GoldenCase(string module, string name, OrderedDictionary<string, NpyArray> arrays, JsonElement meta, string source)
    {
        Module = module;
        Name = name;
        Arrays = arrays;
        Meta = meta;
        Source = source;
    }

    public string Module { get; }

    public string Name { get; }

    public OrderedDictionary<string, NpyArray> Arrays { get; }

    /// <summary>JSON 의 "meta" 객체.</summary>
    public JsonElement Meta { get; }

    /// <summary>읽은 폴더 (오류 문구용).</summary>
    public string Source { get; }

    public NpyArray In(string name) => Get("in." + name);

    public NpyArray Out(string name) => Get("out." + name);

    public bool HasIn(string name) => Arrays.ContainsKey("in." + name);

    private NpyArray Get(string key) =>
        Arrays.TryGetValue(key, out NpyArray? a)
            ? a
            : throw new KeyNotFoundException($"golden {Module}/{Name} 에 '{key}' 가 없습니다 ({Source})");
}

/// <summary>
/// golden 자료 읽기 (docs/csharp_port.md 7장). 찾는 순서는 BPCG_GOLDEN → &lt;OUT&gt;/golden → tests/golden/data 입니다.
/// golden 은 포팅 때 Python 생성기가 만든 기준값이고, Python 생성기를 지운 뒤로는 다시 만들 수 없습니다.
/// 큰 사례(out/golden)가 없으면 그 사례를 쓰는 시험은 건너뜁니다(Assert.Skip). 커밋한 작은 사례가 없으면 실패합니다.
/// </summary>
public static class Golden
{
    private const string Hint = "커밋한 작은 사례는 tests/golden/data 에 있고, 큰 사례는 포팅 때 만든 out/golden 이 있어야 합니다 (BPCG_GOLDEN 으로 위치를 줄 수 있음)";

    private static IEnumerable<string> Roots()
    {
        string? env = Environment.GetEnvironmentVariable("BPCG_GOLDEN");
        if (!string.IsNullOrEmpty(env))
        {
            yield return env;
        }
        yield return Path.Combine(Paths.Out, "golden");
        yield return Path.Combine(Paths.Root, "tests", "golden", "data");
    }

    /// <summary>사례 하나를 읽습니다 (module 예: "core/noise", name 예: "fbm3_test_p").</summary>
    public static GoldenCase Load(string module, string name)
    {
        foreach (string root in Roots())
        {
            string npz = Path.Combine(root, module, name + ".npz");
            string json = Path.Combine(root, module, name + ".json");
            if (File.Exists(npz) && File.Exists(json))
            {
                OrderedDictionary<string, NpyArray> arrays = Npz.ReadFile(npz);
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(json));
                JsonElement meta = doc.RootElement.GetProperty("meta").Clone();
                return new GoldenCase(module, name, arrays, meta, root);
            }
        }
        if (!CommittedCases.Value.Contains($"{module}/{name}"))
        {
            // 커밋하지 않은 큰 사례: 포팅 때 만든 out/golden 이 있는 기계에서만 돕니다 (새로 만들 수 없음).
            Assert.Skip($"큰 golden 사례 {module}/{name} 가 없어 건너뜁니다. {Hint}");
        }
        throw new FileNotFoundException($"golden 사례 {module}/{name} 가 없습니다. {Hint}");
    }

    /// <summary>커밋한 작은 사례 이름 (tests/golden/data/manifest.json 의 cases). 이것이 없으면 실패입니다.</summary>
    private static readonly Lazy<HashSet<string>> CommittedCases = new(() =>
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string path = Path.Combine(Paths.Root, "tests", "golden", "data", "manifest.json");
        if (!File.Exists(path))
        {
            return names;
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            names.Add($"{c.GetProperty("module").GetString()}/{c.GetProperty("case").GetString()}");
        }
        return names;
    });
}
