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
        if (LargeCases.Contains($"{module}/{name}"))
        {
            // 커밋하지 않은 큰 사례: 포팅 때 만든 out/golden 이 있는 기계에서만 돕니다 (새로 만들 수 없음).
            Assert.Skip($"큰 golden 사례 {module}/{name} 가 없어 건너뜁니다. {Hint}");
        }
        // 커밋한 사례도 큰 사례도 아닌 이름은 오타이거나 바뀐 이름입니다. 건너뛰면 그 시험이 영영 돌지 않으므로 실패로 둡니다.
        string known = CommittedCases.Value.Contains($"{module}/{name}") ? "커밋한 사례인데 파일이 없습니다" : "사례 목록에 없는 이름입니다";
        throw new FileNotFoundException($"golden 사례 {module}/{name} 가 없습니다 ({known}). {Hint}");
    }

    /// <summary>
    /// 커밋하지 않은 큰 사례 이름. 파일이 없으면 시험을 건너뜁니다. 이 목록에 없는 이름은 파일이 없을 때 실패합니다
    /// (이름을 잘못 적은 시험이 조용히 건너뛰어지지 않게).
    /// </summary>
    private static readonly HashSet<string> LargeCases = new(StringComparer.Ordinal)
    {
        "core/cubesphere/cross_face_pairs_n64",
        "core/cubesphere/grid_n16_r9964050",
        "core/cubesphere/grid_n32_r6371000",
        "core/cubesphere/grid_n33_r6371000",
        "core/cubesphere/neighbor_table_n32",
        "core/cubesphere/neighbor_table_n33",
        "core/distance/flat64_1000",
        "core/distance/flat64_inf",
        "core/distance/sphere32_3sp",
        "core/distance/sphere32_inf",
        "core/distance/sphere32_zero",
        "core/graph/flat_64x64_j1.0_s1738104521",
        "core/graph/sphere_n16_j0.4_s5",
        "core/graph/sphere_n32_j1.0_s0",
        "core/resample/ghost_stencil_n32_l1",
        "core/resample/ghost_stencil_n33_l3",
        "core/resample/resample_16_to_32",
        "core/resample/sample_sphere_n16",
        "hydro/flat64",
        "hydro/sphere32",
        "planet/transfer_c16_f32",
    };

    /// <summary>커밋한 작은 사례 이름 (tests/golden/data/manifest.json 의 cases). 오류 글에서 원인을 구분하는 데 씁니다.</summary>
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
