using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bpcg.Compare;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests.CompareTool;

/// <summary>방법 비교 도구 (src/Bpcg/Compare, docs/compare.md) 시험.</summary>
public sealed class CompareToolTests : IDisposable
{
    private static readonly CompareGrid Small = new(32, 100.0, 800.0);

    // 시험을 빨리 돌리려고 줄이는 매개변수
    private static readonly Dictionary<string, OrderedDictionary<string, object?>> Fast = new()
    {
        ["stream_power"] = new() { ["steps"] = 15L },
        ["hydraulic"] = new() { ["drops_per_cell"] = 0.3 },
    };

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bpcg-compare-" + Guid.NewGuid().ToString("N"));

    public CompareToolTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
    }

    public static TheoryData<string> MethodNames()
    {
        var data = new TheoryData<string>();
        foreach (CompareMethod m in MethodRegistry.AllMethods())
        {
            data.Add(m.Name);
        }
        return data;
    }

    private static (MethodOutput Output, CompareContext Ctx) Gen(string name, long seed, bool recording = false)
    {
        CompareMethod m = MethodRegistry.Get(name);
        ParamValues p = m.Resolve(Fast.GetValueOrDefault(name), name);
        var ctx = new CompareContext { ExemplarLoader = CompareRunner.LoadExemplar, Recording = recording };
        return (m.Generate(Small, seed, p, ctx), ctx);
    }

    [Fact]
    public void BuiltinMethodsRegistered()
    {
        string[] want = ["fbm", "ridged", "multifractal", "warped", "diamond_square", "faulting", "thermal", "hydraulic", "stream_power", "quilting", "bpcg"];
        HashSet<string> names = [.. MethodRegistry.AllMethods().Select(m => m.Name)];
        Assert.Subset(names, want.ToHashSet());
        foreach (CompareMethod m in MethodRegistry.AllMethods())
        {
            Assert.Contains(m.Family, MethodRegistry.Families.Keys);
            Assert.NotEmpty(m.Ref);
            Assert.NotEmpty(m.Summary);
            m.Resolve(null, "t"); // 기본값이 모두 검사를 통과
        }
    }

    [Fact]
    public void ParamChecks()
    {
        CompareMethod m = MethodRegistry.Get("fbm");
        Assert.Contains("없는 매개변수", Assert.Throws<ArgumentException>(() => m.Resolve(new Dictionary<string, object?> { ["nope"] = 1L }, "t")).Message);
        Assert.Contains("이상", Assert.Throws<ArgumentException>(() => m.Resolve(new Dictionary<string, object?> { ["octaves"] = 0L }, "t")).Message);
        Assert.Contains("정수", Assert.Throws<ArgumentException>(() => m.Resolve(new Dictionary<string, object?> { ["octaves"] = 2.5 }, "t")).Message);
        var choice = new Param("k", "a", "h", Kind: "str", Choices: ["a", "b"]);
        Assert.Contains("가운데 하나", Assert.Throws<ArgumentException>(() => choice.Coerce("c", "t")).Message);
        Assert.Equal(4, m.Resolve(new Dictionary<string, object?> { ["octaves"] = 4L }, "t").I("octaves"));
        Assert.Contains("등록되지 않은", Assert.Throws<ArgumentException>(() => MethodRegistry.Get("nope")).Message);
    }

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void MethodIsDeterministicAndSeeded(string name)
    {
        double[] a = Gen(name, 3).Output.Z;
        double[] b = Gen(name, 3).Output.Z;
        double[] c = Gen(name, 4).Output.Z;
        Assert.Equal(Small.Cells, a.Length);
        Assert.All(a, v => Assert.True(double.IsFinite(v)));
        Compare.Bits(a, b, name);
        Assert.False(a.SequenceEqual(c), $"{name}: 시드가 달라도 결과가 같습니다");
    }

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void RecordingDoesNotChangeResult(string name)
    {
        double[] plain = Gen(name, 1).Output.Z;
        (MethodOutput rec, CompareContext ctx) = Gen(name, 1, recording: true);
        Compare.Bits(plain, rec.Z, name);
        Assert.True(ctx.Stages.Count > 0, $"{name} 은 연산 과정 단계를 하나 이상 남겨야 합니다");
        Assert.All(ctx.Stages, s => Assert.Equal(Small.Cells, s.Data.Length));
        Assert.All(ctx.Stages, s => Assert.Contains(s.Kind, CompareContext.StageKinds.Keys));
    }

    [Fact]
    public void ErosionStartsFromSameFbm()
    {
        double[] f = Gen("fbm", 2).Output.Z;
        CompareContext ctx = Gen("thermal", 2, recording: true).Ctx;
        Assert.Equal(f.Select(v => (float)v), ctx.Stages[0].Data);
    }

    [Fact]
    public void HydraulicStaysBounded()
    {
        double[] z = Gen("hydraulic", 0).Output.Z;
        Assert.True(z.Min() >= -1e-6 && z.Max() <= Small.ReliefM + 1e-6, $"범위 밖: {z.Min()} ~ {z.Max()}");
    }

    [Fact]
    public void MetricsOnPlaneAndBowl()
    {
        const int n = 40;
        double[] plane = new double[n * n];
        double[] bowl = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                plane[(j * n) + i] = (2.0 * j) + (0.5 * i);
                bowl[(j * n) + i] = ((j - (n / 2.0)) * (j - (n / 2.0))) + ((i - (n / 2.0)) * (i - (n / 2.0)));
            }
        }
        OrderedDictionary<string, object?> v = CompareMetrics.Compute(plane, n, 50.0);
        Assert.Equal(0.0, v["pit_density_per_km2"]);
        Assert.Equal(0.0, v["depression_area_pct"]);
        v = CompareMetrics.Compute(bowl, n, 50.0);
        Assert.True((double)v["pit_density_per_km2"]! > 0.0);
        Assert.True((double)v["depression_area_pct"]! > 50.0);
        Assert.Equal(CompareMetrics.All.Select(m => m.Name), v.Keys);
    }

    private static CompareConfig Cfg(bool snapshots = true)
    {
        var data = new OrderedDictionary<string, object?>
        {
            ["grid"] = new OrderedDictionary<string, object?> { ["n"] = 32L, ["dx"] = 100.0, ["relief_m"] = 800.0 },
            ["run"] = new OrderedDictionary<string, object?> { ["seeds"] = new List<object?> { 0L, 1L }, ["snapshots"] = snapshots },
            ["methods"] = new List<object?>
            {
                new OrderedDictionary<string, object?> { ["id"] = "fbm" },
                new OrderedDictionary<string, object?> { ["id"] = "fbm_rough", ["method"] = "fbm", ["params"] = new OrderedDictionary<string, object?> { ["gain"] = 0.7 } },
                new OrderedDictionary<string, object?> { ["id"] = "thermal", ["params"] = new OrderedDictionary<string, object?> { ["iterations"] = 5L } },
            },
        };
        return CompareRunner.ConfigFromDict(data, "t");
    }

    private static OrderedDictionary<string, object?> Methods(params string[] ids) => new()
    {
        ["methods"] = ids.Select(id => (object?)new OrderedDictionary<string, object?> { ["id"] = id, ["method"] = id == "x" ? "nope" : "fbm" }).ToList(),
    };

    [Fact]
    public void ConfigErrors()
    {
        Assert.Contains("겹칩니다", Assert.Throws<ArgumentException>(() => CompareRunner.ConfigFromDict(Methods("fbm", "fbm"), "t")).Message);
        Assert.Contains("등록되지 않은", Assert.Throws<ArgumentException>(() => CompareRunner.ConfigFromDict(Methods("x"), "t")).Message);
        OrderedDictionary<string, object?> badSeed = Methods("fbm");
        badSeed["run"] = new OrderedDictionary<string, object?> { ["seeds"] = new List<object?> { -1L } };
        Assert.Contains("시드", Assert.Throws<ArgumentException>(() => CompareRunner.ConfigFromDict(badSeed, "t")).Message);
        OrderedDictionary<string, object?> unknown = Methods("fbm");
        unknown["oops"] = new OrderedDictionary<string, object?>();
        Assert.Contains("모르는", Assert.Throws<ArgumentException>(() => CompareRunner.ConfigFromDict(unknown, "t")).Message);
        CompareConfig cfg = Cfg();
        Assert.Contains("없는 인스턴스", Assert.Throws<ArgumentException>(() => CompareRunner.ApplyParamOverrides(cfg, [new("nope.gain", 0.5)])).Message);
        CompareRunner.ApplyParamOverrides(cfg, [new("grid.n", 48L), new("fbm.octaves", 3L)]);
        Assert.Equal(48, cfg.Grid.N);
        Assert.Equal(3, cfg.Instances[0].Resolve().Values.I("octaves"));
    }

    [Fact]
    public void ReadsTomlConfig()
    {
        string path = Path.Combine(_tmp, "c.toml");
        File.WriteAllText(path, "[grid]\nn = 32\ndx = 100.0\n[run]\nseeds = [0]\n[[methods]]\nid = \"fbm\"\n[[methods]]\nid = \"faulting\"\nparams = { faults = 20 }\n");
        CompareConfig cfg = CompareRunner.LoadConfigFile(path);
        Assert.Equal("c", cfg.Name);
        Assert.Equal(["fbm", "faulting"], cfg.Instances.Select(i => i.Id));
        Assert.Equal(20, cfg.Instances[1].Resolve().Values.I("faults"));
        CompareConfig shipped = CompareRunner.LoadConfigFile(Path.Combine(Bpcg.Core.Paths.Configs, "compare", "default.toml"));
        Assert.Contains(shipped.Instances, i => i.Id == "bpcg");
    }

    [Fact]
    public void RunGroupsBySeedAndCaches()
    {
        CompareConfig cfg = Cfg();
        string d = CompareRunner.Run(cfg, Path.Combine(_tmp, "set"));
        OrderedDictionary<string, object?> data = CompareRunner.LoadSet(d);
        var groups = ((List<object?>)data["groups"]!).Cast<OrderedDictionary<string, object?>>().ToList();
        Assert.Equal([0L, 1L], groups.Select(g => (long)g["seed"]!));
        foreach (OrderedDictionary<string, object?> g in groups)
        {
            var entries = ((List<object?>)g["entries"]!).Cast<OrderedDictionary<string, object?>>().ToList();
            Assert.Equal(["fbm", "fbm_rough", "thermal"], entries.Select(e => (string)e["id"]!));
            foreach (OrderedDictionary<string, object?> e in entries)
            {
                var metrics = (OrderedDictionary<string, object?>)e["metrics"]!;
                Assert.True(Convert.ToDouble(metrics["relief_m"], System.Globalization.CultureInfo.InvariantCulture) > 0);
                Assert.True((long)e["stages"]! > 0);
                Assert.False(string.IsNullOrEmpty((string?)e["z_key"]));
            }
        }
        string entry = Path.Combine(d, "seed_0000", "fbm");
        string metaPath = Path.Combine(entry, "meta.json");
        string before = File.ReadAllText(metaPath);
        float[] zBefore = Npy.ReadFile(Path.Combine(entry, "z.npy")).AsFloat();
        var lines = new List<string>();
        CompareRunner.Run(cfg, d, log: lines.Add); // 같은 설정: 다시 만들지 않음
        Assert.Equal(before, File.ReadAllText(metaPath));
        Assert.Equal(zBefore, Npy.ReadFile(Path.Combine(entry, "z.npy")).AsFloat());
        Assert.Equal(6, lines.Count(l => l.Contains("캐시", StringComparison.Ordinal)));
        OrderedDictionary<string, object?> pr = CompareRunner.LoadProcess(Path.Combine(d, "seed_0000", "thermal"));
        var stages = ((List<object?>)pr["stages"]!).Cast<OrderedDictionary<string, object?>>().ToList();
        Assert.NotEmpty(stages);
        Assert.NotEmpty((List<object?>)pr["series"]!);
        Assert.Subset(stages[0].Keys.ToHashSet(), new HashSet<string> { "label", "kind", "vmin", "vmax", "legend" });
    }

    [Fact]
    public void RunRejectsOtherGrid()
    {
        CompareConfig cfg = Cfg();
        string d = CompareRunner.Run(cfg, Path.Combine(_tmp, "set"), [0], ["fbm"]);
        cfg.Grid = new CompareGrid(48, 100.0, 800.0);
        Assert.Contains("다른 격자", Assert.Throws<ArgumentException>(() => CompareRunner.Run(cfg, d, [0], ["fbm"])).Message);
        Assert.Contains("없는 인스턴스", Assert.Throws<ArgumentException>(() => CompareRunner.Run(Cfg(), d, [0], ["nope"])).Message);
    }

    [Fact]
    public void RendersOneImageAndStages()
    {
        string d = CompareRunner.Run(Cfg(), Path.Combine(_tmp, "set"), [0], ["thermal"]);
        string entry = CompareRunner.EntryDir(d, 0, "thermal");
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        string png = CompareRender.RenderElevation(entry, 100.0);
        Assert.Equal(signature, File.ReadAllBytes(png)[..8]);
        Assert.Equal(png, CompareRender.RenderElevation(entry, 100.0)); // 같은 그림은 다시 그리지 않음
        string group = CompareRender.RenderElevation(entry, 100.0, 0.0, 800.0);
        Assert.NotEqual(png, group);
        Assert.Single(Directory.GetFiles(entry, "elevation_*.png"));
        var listing = (List<object?>)PyJson.Loads(File.ReadAllText(Path.Combine(entry, "stages", "stages.json")))!;
        for (int i = 0; i < listing.Count; i++)
        {
            Assert.Equal(signature, File.ReadAllBytes(CompareRender.RenderStage(entry, i, 100.0))[..8]);
        }
        Assert.Throws<ArgumentException>(() => CompareRender.RenderStage(entry, listing.Count, 100.0));
        Assert.Throws<ArgumentException>(() => CompareRunner.EntryDir(d, 0, "../thermal"));
        Assert.Throws<ArgumentException>(() => CompareRunner.EntryDir(d, 0, "nope"));
    }

    [Fact]
    public void SnapshotsOff()
    {
        string d = CompareRunner.Run(Cfg(snapshots: false), Path.Combine(_tmp, "set"), [0], ["fbm"]);
        Assert.Empty((List<object?>)CompareRunner.LoadProcess(Path.Combine(d, "seed_0000", "fbm"))["stages"]!);
    }
}
