using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.IO;

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
/// golden 자료 읽기 (docs/csharp_port.md 7장). 찾는 순서는 BPCG_GOLDEN → &lt;OUT&gt;/golden → csharp/golden/data 입니다.
/// 없으면 건너뛰지 않고 실패하며, 만드는 명령을 알려 줍니다.
/// </summary>
public static class Golden
{
    private const string Hint = "uv run python csharp/golden/export_golden.py 로 golden 자료를 먼저 만드세요";

    private static IEnumerable<string> Roots()
    {
        string? env = Environment.GetEnvironmentVariable("BPCG_GOLDEN");
        if (!string.IsNullOrEmpty(env))
        {
            yield return env;
        }
        yield return Path.Combine(Paths.Out, "golden");
        yield return Path.Combine(Paths.Root, "csharp", "golden", "data");
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
        throw new FileNotFoundException($"golden 사례 {module}/{name} 가 없습니다. {Hint}");
    }
}
