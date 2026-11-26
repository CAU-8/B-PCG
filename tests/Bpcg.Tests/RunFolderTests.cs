using System;
using System.Collections.Generic;
using System.IO;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests;

/// <summary>
/// 실행 폴더 정리와 묶음 표지(parent) 대조 (docs/pipeline.md 11장 '실행 폴더'). 같은 폴더를 다시 쓰면 지난 실행의
/// 단계 폴더가 남지 않아야 하고, 서로 다른 실행의 묶음을 함께 쓰려 하면 무엇이 다른지 알리고 멈춰야 합니다.
/// </summary>
public sealed class RunFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bpcg-runfolder-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static OrderedDictionary<string, object?> Planet(string digest, long seed) => new()
    {
        ["format"] = Bundle.Format,
        ["kind"] = "planet",
        ["config_digest"] = digest,
        ["seed"] = seed,
        ["parent"] = null,
    };

    private static OrderedDictionary<string, object?> Hero(string digest, long seed, object? parent) => new()
    {
        ["format"] = Bundle.Format,
        ["kind"] = "hero",
        ["config_digest"] = digest,
        ["seed"] = seed,
        ["parent"] = parent,
    };

    // 단계 폴더 하나를 목차 파일만 넣어 흉내 냅니다.
    private static void Write(string dir, string file, OrderedDictionary<string, object?> index)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), PyJson.DumpsManifest(index));
    }

    private static OrderedDictionary<string, object?> Read(string path) =>
        (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(path))!;

    [Fact]
    public void BundleIdNamesKindDigestSeedAndTheHerosPlanet()
    {
        OrderedDictionary<string, object?> planetId = RunFolder.BundleId(Planet("aaa", 0))!;
        Assert.Equal(new[] { "kind", "config_digest", "seed" }, planetId.Keys);
        OrderedDictionary<string, object?> heroId = RunFolder.BundleId(Hero("bbb", 0, planetId))!;
        Assert.True(RunFolder.SameBundle(heroId["parent"], planetId));

        // 설정이 같아도 평면 히어로와 행성에서 만든 히어로는 다른 표지를 받습니다.
        Assert.False(RunFolder.SameBundle(heroId, RunFolder.BundleId(Hero("bbb", 0, null))));

        // parent 를 적기 전에 만든 히어로 묶음은 어느 행성에서 만들었는지 몰라 표지가 없습니다.
        OrderedDictionary<string, object?> old = Hero("bbb", 0, null);
        old.Remove("parent");
        Assert.Null(RunFolder.BundleId(old));
        Assert.False(RunFolder.SameBundle(null, null));
    }

    // 목차가 이 생성기의 형식인 단계 폴더만 지웁니다. 같은 이름이라도 목차가 없는 폴더(사용자가 둔 폴더, 쓰다 멈춘 폴더)는 둡니다.
    [Fact]
    public void DropStagesRemovesOnlyFoldersWrittenByTheGenerator()
    {
        Write(Path.Combine(_root, Runs.HeroDir), Bundle.ManifestFile, Hero("bbb", 0, null));
        Write(Path.Combine(_root, Runs.CorridorDir), Bundle.ManifestFile, new() { ["format"] = RunFolder.CorridorFormat });
        Directory.CreateDirectory(Path.Combine(_root, Runs.GlobeDir));
        File.WriteAllText(Path.Combine(_root, Runs.GlobeDir, "내 메모.txt"), "사용자 파일");
        var lines = new List<string>();

        List<string> dropped = RunFolder.DropStages(_root, [Runs.HeroDir, Runs.CorridorDir, Runs.GlobeDir], "시험", lines.Add);

        Assert.Equal(new[] { Runs.HeroDir, Runs.CorridorDir }, dropped);
        Assert.False(Directory.Exists(Path.Combine(_root, Runs.HeroDir)));
        Assert.False(Directory.Exists(Path.Combine(_root, Runs.CorridorDir)));
        Assert.True(File.Exists(Path.Combine(_root, Runs.GlobeDir, "내 메모.txt")));
        Assert.Equal($"[실행 폴더] 묵은 단계 폴더 2개를 지웠습니다: hero, corridor (시험) → {_root}", Assert.Single(lines));
        Assert.Empty(RunFolder.DropStages(_root, [Runs.HeroDir], "시험", lines.Add));
        Assert.Single(lines);
    }

    [Fact]
    public void HeroIsNotWrittenNextToAPlanetFromAnotherRun()
    {
        Write(Path.Combine(_root, Runs.PlanetDir), Bundle.ManifestFile, Planet("aaa", 5));
        OrderedDictionary<string, object?> here = RunFolder.BundleId(Planet("aaa", 5))!;

        RunFolder.RequireHeroFitsRun(_root, here);
        RunFolder.RequireHeroFitsRun(Path.Combine(_root, "빈 폴더"), null);

        RunError other = Assert.Throws<RunError>(() => RunFolder.RequireHeroFitsRun(_root, RunFolder.BundleId(Planet("ccc", 0))));
        Assert.Contains("시드 5, 설정 해시 aaa", other.Message, StringComparison.Ordinal);
        Assert.Contains("시드 0, 설정 해시 ccc", other.Message, StringComparison.Ordinal);
        Assert.Contains("--out", other.Message, StringComparison.Ordinal);

        RunError flat = Assert.Throws<RunError>(() => RunFolder.RequireHeroFitsRun(_root, null));
        Assert.Contains("평면 히어로", flat.Message, StringComparison.Ordinal);
        Assert.Contains("시드 5, 설정 해시 aaa", flat.Message, StringComparison.Ordinal);
        Assert.Contains("--out", flat.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroPlanetMismatchSaysWhatDiffers()
    {
        OrderedDictionary<string, object?> planet = Planet("aaa", 0);
        Assert.Null(RunFolder.HeroPlanetMismatch(planet, Hero("bbb", 0, RunFolder.BundleId(planet))));
        Assert.Contains("평면 히어로", RunFolder.HeroPlanetMismatch(planet, Hero("bbb", 0, null)), StringComparison.Ordinal);
        Assert.Contains(
            "다른 행성(시드 1, 설정 해시 ccc)",
            RunFolder.HeroPlanetMismatch(planet, Hero("bbb", 1, RunFolder.BundleId(Planet("ccc", 1)))),
            StringComparison.Ordinal);

        // parent 를 적기 전의 히어로 묶음은 시드만 비교합니다.
        OrderedDictionary<string, object?> old = Hero("bbb", 0, null);
        old.Remove("parent");
        Assert.Null(RunFolder.HeroPlanetMismatch(planet, old));
        old["seed"] = 7L;
        Assert.Equal("시드가 다릅니다(행성 0, 히어로 7)", RunFolder.HeroPlanetMismatch(planet, old));
    }

    // --no-globe 로 굽거나 행성이 없어 지구본을 굽지 않을 때, 이 히어로와 함께 구운 지구본만 남깁니다.
    [Fact]
    public void GlobeIsKeptOnlyIfBakedWithThisHero()
    {
        OrderedDictionary<string, object?> heroId = RunFolder.BundleId(Hero("bbb", 0, RunFolder.BundleId(Planet("aaa", 0))))!;
        string globe = Path.Combine(_root, Runs.GlobeDir);
        var lines = new List<string>();
        Write(globe, Globe.GlobeJson, new() { ["format"] = Globe.FormatName, ["hero_bundle"] = heroId });
        Assert.False(RunFolder.DropGlobeUnlessFor(globe, heroId, lines.Add));
        Assert.True(Directory.Exists(globe));

        OrderedDictionary<string, object?> moved = RunFolder.BundleId(Hero("ddd", 0, RunFolder.BundleId(Planet("aaa", 0))))!;
        Assert.True(RunFolder.DropGlobeUnlessFor(globe, moved, lines.Add));
        Assert.False(Directory.Exists(globe));
        Assert.StartsWith("[지구본] 이번 히어로와 맞지 않는 지난 지구본을 지웠습니다: 함께 구운 히어로(시드 0, 설정 해시 bbb", Assert.Single(lines), StringComparison.Ordinal);

        // 함께 구운 히어로의 기록이 없는 지구본(그 기록을 적기 전에 구움)은 맞는지 알 수 없어 지웁니다.
        Write(globe, Globe.GlobeJson, new() { ["format"] = Globe.FormatName });
        Assert.True(RunFolder.DropGlobeUnlessFor(globe, heroId, lines.Add));
        Assert.False(RunFolder.DropGlobeUnlessFor(globe, heroId, lines.Add));
    }

    // 행성을 다시 쓰면 같은 폴더의 히어로·회랑·지구본(옛 행성으로 만든 결과)이 남지 않고, 그 폴더에 평면 히어로를 쓰려 하면 멈춥니다.
    [Fact]
    public void RewritingThePlanetDropsStagesMadeFromTheOldPlanet()
    {
        Write(Path.Combine(_root, Runs.HeroDir), Bundle.ManifestFile, Hero("bbb", 0, RunFolder.BundleId(Planet("aaa", 0))));
        Write(Path.Combine(_root, Runs.CorridorDir), Bundle.ManifestFile, new() { ["format"] = RunFolder.CorridorFormat });
        Write(Path.Combine(_root, Runs.GlobeDir), Globe.GlobeJson, new() { ["format"] = Globe.FormatName });
        var lines = new List<string>();
        Config cfg = Config.LoadConfig("earth", "tiny");

        Runs.RunPlanet(cfg, _root, lines.Add);

        OrderedDictionary<string, object?> planet = Bundle.ReadManifest(Path.Combine(_root, Runs.PlanetDir));
        Assert.Null(planet["parent"]);
        Assert.Equal(cfg.Digest(), planet["config_digest"]);
        foreach (string stage in new[] { Runs.HeroDir, Runs.CorridorDir, Runs.GlobeDir })
        {
            Assert.False(Directory.Exists(Path.Combine(_root, stage)), stage);
        }
        Assert.Contains(lines, l => l.StartsWith("[실행 폴더] 묵은 단계 폴더 3개를 지웠습니다: hero, corridor, globe", StringComparison.Ordinal));

        RunError e = Assert.Throws<RunError>(() => Runs.RunHero(cfg, _root, null, flat: true, lines.Add));
        Assert.Contains("평면 히어로", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, Runs.HeroDir)));
    }

    // 평면 히어로 all 은 행성이 없는 실행이라, 지난 실행의 planet/ 과 globe/ 를 남기지 않습니다. 묶음마다 만든 재료의 표지를 적습니다.
    [Fact]
    public void FlatAllLeavesNoPlanetOrGlobeFromAnEarlierRunAndRecordsParents()
    {
        Write(Path.Combine(_root, Runs.PlanetDir), Bundle.ManifestFile, Planet("aaa", 0));
        Write(Path.Combine(_root, Runs.GlobeDir), Globe.GlobeJson, new() { ["format"] = Globe.FormatName });

        Runs.All(Config.LoadConfig("earth", "tiny"), _root, flat: true, engineDir: null, _ => { });

        Assert.False(Directory.Exists(Path.Combine(_root, Runs.PlanetDir)));
        Assert.False(Directory.Exists(Path.Combine(_root, Runs.GlobeDir)));
        OrderedDictionary<string, object?> hero = Bundle.ReadManifest(Path.Combine(_root, Runs.HeroDir));
        Assert.True(hero.ContainsKey("parent"));
        Assert.Null(hero["parent"]);
        OrderedDictionary<string, object?> corridor = Read(Path.Combine(_root, Runs.CorridorDir, Bundle.ManifestFile));
        Assert.True(RunFolder.SameBundle(corridor["parent"], RunFolder.BundleId(hero)));
    }
}
