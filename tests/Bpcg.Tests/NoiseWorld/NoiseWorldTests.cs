using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.IO;
using Bpcg.NoiseWorld;
using Xunit;

namespace Bpcg.Tests.NoiseWorld;

/// <summary>tiny 프로필·시드 0 노이즈 비교본 하나를 만들어 두고 여러 시험이 함께 읽습니다.</summary>
public sealed class NoiseRunFixture : IDisposable
{
    public NoiseRunFixture()
    {
        RunDir = Path.Combine(Path.GetTempPath(), "bpcg-noise-" + Guid.NewGuid());
        Log = [];
        NoiseRuns.All(NoiseRuns.LoadConfig("earth", "tiny"), RunDir, null, Log.Add);
    }

    public string RunDir { get; }

    public List<string> Log { get; }

    public void Dispose()
    {
        if (Directory.Exists(RunDir))
        {
            Directory.Delete(RunDir, true);
        }
    }
}

/// <summary>
/// 노이즈 비교본(<see cref="NoiseRuns.All"/>): 같은 입력이면 같은 바이트, 엔진이 읽는 회랑·지구본 약속, 바다 몫과 지하수면 같은 성질.
/// </summary>
public sealed class NoiseWorldTests(NoiseRunFixture run) : IClassFixture<NoiseRunFixture>
{
    // 숫자 배열을 그대로 담은 파일만 바이트로 견줍니다 (JSON 에는 걸린 시간이 들어 있음).
    private static readonly string[] BinaryExtensions = [".bin", ".u8", ".glb"];

    private static Dictionary<string, byte[]> Binaries(string runDir) =>
        Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories)
            .Where(p => BinaryExtensions.Contains(Path.GetExtension(p), StringComparer.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(runDir, p).Replace('\\', '/'), File.ReadAllBytes);

    private static string TempRun() => Path.Combine(Path.GetTempPath(), "bpcg-noise-" + Guid.NewGuid());

    private static OrderedDictionary<string, object?> Obj(object? v) => (OrderedDictionary<string, object?>)v!;

    private static OrderedDictionary<string, object?> ReadJson(string path) => Obj(PyJson.Loads(File.ReadAllText(path)));

    private static float[] ReadFloats(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        float[] f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, b.Length);
        return f;
    }

    private static void AssertSameBinaries(string a, string b, string what)
    {
        Dictionary<string, byte[]> x = Binaries(a);
        Dictionary<string, byte[]> y = Binaries(b);
        Assert.Equal(x.Keys.Order(StringComparer.Ordinal), y.Keys.Order(StringComparer.Ordinal));
        var different = x.Keys.Where(k => !x[k].AsSpan().SequenceEqual(y[k])).Order(StringComparer.Ordinal).ToList();
        Assert.True(different.Count == 0, $"{what}: 바이트가 다른 파일 {different.Count}개: {string.Join(", ", different)}");
    }

    [Fact]
    public void SameSeedGivesSameBytes()
    {
        string again = TempRun();
        try
        {
            NoiseRuns.All(NoiseRuns.LoadConfig("earth", "tiny"), again, null, _ => { });
            AssertSameBinaries(run.RunDir, again, "같은 시드로 두 번 만든 노이즈 비교본");
        }
        finally
        {
            Directory.Delete(again, true);
        }
    }

    [Fact]
    public void ThreadCountDoesNotChangeBytes()
    {
        // 병렬은 칸마다 서로 독립인 일에만 씁니다. 한 스레드로 돌려도 바이트까지 같아야 합니다.
        string one = TempRun();
        try
        {
            Config cfg = NoiseRuns.LoadConfig("earth", "tiny").WithOverrides([new("profile.compute.numba_threads", 1L)]);
            NoiseRuns.All(cfg, one, null, _ => { });
            AssertSameBinaries(run.RunDir, one, "한 스레드와 모든 코어로 만든 노이즈 비교본");
        }
        finally
        {
            Directory.Delete(one, true);
            Pipeline.ApplyThreads(Config.LoadConfig("earth", "tiny"));
        }
    }

    [Fact]
    public void OtherSeedGivesOtherWorld()
    {
        string other = TempRun();
        try
        {
            Config cfg = NoiseRuns.LoadConfig("earth", "tiny").WithOverrides([new("planet.seed", 1L)]);
            NoiseRuns.All(cfg, other, null, _ => { });
            byte[] a = File.ReadAllBytes(Path.Combine(run.RunDir, "globe", "elevation.bin"));
            byte[] b = File.ReadAllBytes(Path.Combine(other, "globe", "elevation.bin"));
            Assert.False(a.AsSpan().SequenceEqual(b), "시드 0 과 1 의 노이즈 행성 고도가 같습니다");
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public void CorridorFollowsEngineContract()
    {
        // 엔진(engine/Scripts/CorridorFiles.cs)이 읽는 목차 키와 파일이 B-PCG 회랑과 같아야 Godot 가 그대로 엽니다.
        string dir = Path.Combine(run.RunDir, Runs.CorridorDir);
        OrderedDictionary<string, object?> m = ReadJson(Path.Combine(dir, "manifest.json"));
        Assert.Equal(RunFolder.CorridorFormat, m["format"]);
        Assert.Equal(NoiseCorridor.Generator, m["generator"]);
        Assert.IsType<double>(Obj(m["frame"])["y_offset_m"]);
        OrderedDictionary<string, object?> re = Obj(Obj(m["corridor"])["rect_engine_m"]);
        foreach (string k in new[] { "x_min", "x_max", "z_min", "z_max" })
        {
            Assert.True(re.ContainsKey(k), $"corridor.rect_engine_m 에 {k} 가 없습니다");
        }
        var files = ((List<object?>)m["files"]!).Cast<string>().ToList();
        foreach (string need in new[]
        {
            "heightmap.json", "surround25.json", "water.json", "water_table.json", "water_table_lower.json",
            "cave_mouth.json", "strata.u8", "strata.json", "strata_top.json", "entrances.json",
        })
        {
            Assert.Contains(need, files);
        }
        foreach (string f in files)
        {
            Assert.True(File.Exists(Path.Combine(dir, f)), $"목차에 있는 {f} 가 없습니다");
        }
        Assert.False(File.Exists(Path.Combine(dir, "heightmap_detail.json")), "노이즈 회랑에는 프랙탈 디테일 파일이 없어야 합니다");
        OrderedDictionary<string, object?> strata = ReadJson(Path.Combine(dir, "strata.json"));
        Assert.Equal("uint8", strata["format"]);
        Assert.Equal("row_layer_col", strata["layout"]);
        OrderedDictionary<string, object?> globe = ReadJson(Path.Combine(run.RunDir, Runs.GlobeDir, "globe.json"));
        Assert.Equal(NoiseCorridor.Generator, globe["generator"]);
        Assert.Equal("노이즈 비교본 지구본", globe["title"]);
        var names = ((List<object?>)globe["fields"]!).Select(e => (string)Obj(e)["name"]!).ToList();
        Assert.Contains("elevation", names);
        Assert.Contains("biome", names);
        // 판·융기는 계산하지 않으므로 그 레이어가 없어야 합니다 (있으면 노이즈가 판을 흉내 낸다고 오해하게 됨).
        Assert.DoesNotContain("plate_id", names);
        Assert.DoesNotContain("uplift_mm_per_yr", names);
        Assert.NotNull(globe["hero"]);
    }

    [Fact]
    public void OceanFractionMatchesSetting()
    {
        OrderedDictionary<string, object?> m = ReadJson(Path.Combine(run.RunDir, NoiseRuns.NoiseDir, "manifest.json"));
        OrderedDictionary<string, object?> planet = Obj(m["planet"]);
        double got = Convert.ToDouble(planet["ocean_fraction"], System.Globalization.CultureInfo.InvariantCulture);
        double want = Convert.ToDouble(planet["ocean_fraction_target"], System.Globalization.CultureInfo.InvariantCulture);
        // tiny 행성 지도는 칸이 6,144개라 칸 하나가 넓이의 약 0.02% 입니다.
        Assert.InRange(got, want - 0.005, want + 0.005);
    }

    [Fact]
    public void WaterTableStaysBelowLand()
    {
        // 노이즈 지하수면은 지표를 따라 정해 둔 깊이 아래에 놓입니다. 육지에서 지표 위로 올라오면 안 됩니다.
        string dir = Path.Combine(run.RunDir, Runs.CorridorDir);
        float[] z = ReadFloats(Path.Combine(dir, "heightmap.bin"));
        float[] w = ReadFloats(Path.Combine(dir, "water_table.bin"));
        Assert.Equal(z.Length, w.Length);
        for (int i = 0; i < z.Length; i++)
        {
            if (z[i] > 0.0f)
            {
                Assert.True(w[i] <= z[i], $"표본 {i}: 지하수면 {w[i]} m 가 지표 {z[i]} m 보다 높습니다");
            }
        }
    }

    [Fact]
    public void LogLinesHaveStageHeads()
    {
        // 엔진 처음 화면의 진행률(engine/Scripts/StageProgress.cs 의 NoiseStages)이 이 줄 머리를 찾습니다.
        foreach (string head in new[]
        {
            "[노이즈] 행성 지도", "[노이즈] 히어로 자리", "[노이즈] 히어로 유역", "[노이즈] 회랑:", "[노이즈] 높이맵",
            "[노이즈] 물:", "[노이즈] 동굴 메시", "[노이즈] 재질 부피", "[지구본] 끝", "[노이즈] 끝",
        })
        {
            Assert.Contains(run.Log, l => l.StartsWith(head, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SettingsErrorsNameKeyAndValue()
    {
        Config cfg = NoiseRuns.LoadConfig("earth", "tiny").WithOverrides([new("noise.mountains.wavelength_m", -5.0)]);
        var e = Assert.Throws<ArgumentException>(() => new NoiseTerrain(cfg));
        Assert.Contains("noise.mountains.wavelength_m", e.Message, StringComparison.Ordinal);
        Assert.Contains("-5", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeightIsOneFunctionAcrossScales()
    {
        // 행성 지도·히어로·회랑이 같은 세계 함수를 다른 간격으로 읽습니다. 짧은 물결을 빼도 긴 물결의 세기는 그대로이므로,
        // 히어로 간격(100 m)과 회랑 간격(8 m)으로 읽은 높이의 차이는 빠진 짧은 물결만큼(수십 m 안)입니다.
        Config cfg = NoiseRuns.LoadConfig("earth", "tiny");
        var terrain = new NoiseTerrain(cfg);
        NoisePlanetResult planet = NoisePlanet.Build(terrain, cfg);
        double worst = 0.0;
        for (int k = 0; k < 64; k++)
        {
            (double x, double y, double z) = planet.Site.ToUnit((k % 8) * 700.0, (k / 8) * 700.0);
            double coarse = terrain.Surface(x, y, z, 100.0).HeightM;
            double fine = terrain.Surface(x, y, z, 8.0).HeightM;
            worst = Math.Max(worst, Math.Abs(coarse - fine));
        }
        Assert.InRange(worst, 0.0, 60.0);
    }
}
