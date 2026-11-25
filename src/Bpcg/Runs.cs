using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.Hero;

namespace Bpcg;

/// <summary>실행을 멈추는 오류 (Python SystemExit(글)). 명령줄은 종료 코드로, 엔진은 화면 글로 보입니다.</summary>
public class RunError(string message, int code = 1) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// 실행 단위 (src/bpcg/cli.py 의 run_planet·run_hero·bake_config·run_bake·run_globe 와 all 명령의 순서).
/// 명령줄(Bpcg.Cli)과 Godot 엔진(Bpcg.Engine)이 같이 씁니다. 기록 줄은 log 로 넘깁니다.
/// </summary>
public static class Runs
{
    public const string PlanetDir = "planet";
    public const string HeroDir = "hero";
    public const string CorridorDir = "corridor";
    public const string TextureDir = "textures";
    public const string GlobeDir = "globe";
    public static readonly string[] BakeSections = ["detail"];
    public static readonly string[] BakeSetPrefixes = ["detail.", "profile.corridor."];
    public static readonly string[] HeroSections = ["hero"];
    public static readonly string[] HeroSetPrefixes = ["hero."];

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    // 디스크를 거치던 단계 경계의 float32 반올림을 유지합니다.
    // 배열을 직접 갱신하므로 같은 배열을 들고 있는 다른 객체(strata_bottom_m 을 공유하는 Columns 등)도 반올림한 값을 봅니다.
    // 그다음 필드 묶음에는 같은 값의 float[] 를 넣어 메모리를 반으로 줄입니다(읽는 쪽은 FieldSet.GetReal 등으로 넓혀 읽음).
    // strata_bottom_m 은 Columns 가 double[] 로 함께 쓰므로 바꾸지 않습니다(바꾸면 두 벌이 됨).
    private static void UseSavedPrecision(FieldSet fields)
    {
        var narrowed = new List<(string Name, float[] Values)>();
        foreach (KeyValuePair<string, Array> field in fields)
        {
            if (Core.Fields.All.TryGetValue(field.Key, out FieldInfo? info)
                && info.Dtype == "float32" && field.Value is double[] values)
            {
                float[] f = new float[values.Length];
                Parallelism.For(0, values.Length, i =>
                {
                    f[i] = (float)values[i];
                    values[i] = f[i];
                });
                if (field.Key != SharedWithColumns)
                {
                    narrowed.Add((field.Key, f));
                }
            }
        }
        foreach ((string name, float[] f) in narrowed)
        {
            fields[name] = f;
        }
    }

    // 층 기둥(LayerColumns.Bottom)과 같은 배열이라 double[] 로 남기는 필드.
    internal const string SharedWithColumns = "strata_bottom_m";

    /// <summary>--set 키=값 목록 → 순서 있는 덮어쓰기 (틀리면 RunError).</summary>
    public static OrderedDictionary<string, object?> SetOverrides(IEnumerable<string>? items)
    {
        var o = new OrderedDictionary<string, object?>();
        foreach (string item in items ?? [])
        {
            try
            {
                (string key, object? value) = Config.ParseAssignment(item);
                o[key] = value;
            }
            catch (ArgumentException e)
            {
                throw new RunError($"--set: {e.Message}");
            }
        }
        return o;
    }

    /// <summary>시드만 바꾼 설정 (seed 가 null 이면 그대로).</summary>
    public static Config WithSeed(Config cfg, long? seed) =>
        seed is long s ? cfg.WithOverrides([new("planet.seed", s)]) : cfg;

    // 행성을 새로 쓰면, 같은 실행 폴더의 히어로·회랑·지구본은 옛 행성으로 만든 결과라 이 행성과 맞지 않습니다.
    private static readonly string[] BelowPlanet = [HeroDir, CorridorDir, GlobeDir];

    // 히어로를 새로 쓰면, 같은 실행 폴더의 회랑·지구본은 옛 히어로로 구운 결과라 이 히어로와 맞지 않습니다.
    private static readonly string[] BelowHero = [CorridorDir, GlobeDir];

    /// <summary>
    /// 행성을 만들어 runDir/planet 에 묶음·면 텍스처를 씁니다 (run_planet). 반환: 묶음 폴더.
    /// 다 쓴 뒤 같은 실행 폴더의 hero/, corridor/, globe/ 를 지웁니다. 옛 행성으로 만든 결과라 새 행성과 맞지 않기 때문입니다.
    /// </summary>
    public static string RunPlanet(Config cfg, string runDir, Action<string> log)
    {
        string output = GenerateAndSavePlanet(cfg, runDir, log).Output;
        RunFolder.DropStages(runDir, BelowPlanet, "행성을 새로 만들어, 옛 행성으로 만든 결과임", log);
        return output;
    }

    private static (string Output, PlanetState State) GenerateAndSavePlanet(Config cfg, string runDir, Action<string> log)
    {
        long t = Stopwatch.GetTimestamp();
        log($"[행성] 시작: 프로필 {cfg.Sec("profile").Get("name")}, 시드 {cfg.I("planet.seed")}");
        PlanetState planet = Pipeline.GeneratePlanet(cfg, log);
        string output = Path.Combine(runDir, PlanetDir);
        Bundle.SavePlanetState(output, planet, cfg);
        Textures.FaceTextures(planet, Path.Combine(output, TextureDir));
        Bundle.WriteJson(Path.Combine(output, "scorecard.json"), planet.Diag.GetValueOrDefault("scorecard") ?? new OrderedDictionary<string, object?>());
        log($"[행성] 묶음을 썼습니다: {output} ({Seconds(t):F1} s)");
        return (output, planet);
    }

    /// <summary>
    /// 히어로를 만들어 runDir/hero 에 묶음을 씁니다 (run_hero). planetDir 이 없거나 flat 이면 평면 히어로.
    /// 만들기 전에, runDir 에 다른 행성 묶음이 있으면 RunError 로 멈춥니다(<see cref="RunFolder.RequireHeroFitsRun"/>).
    /// 다 쓴 뒤 같은 실행 폴더의 corridor/, globe/ 를 지웁니다. 옛 히어로로 구운 결과라 새 히어로와 맞지 않기 때문입니다.
    /// </summary>
    public static string RunHero(Config cfg, string runDir, string? planetDir, bool flat, Action<string> log)
    {
        long t = Stopwatch.GetTimestamp();
        PlanetState? planet = null;
        OrderedDictionary<string, object?>? planetId = null;
        if (flat)
        {
            RunFolder.RequireHeroFitsRun(runDir, null);
        }
        else
        {
            if (planetDir is null)
            {
                throw new RunError("히어로를 만들려면 --from <행성 묶음> 이나 --flat 이 필요합니다");
            }
            planetId = RunFolder.BundleId(Bundle.ReadManifest(planetDir));
            RunFolder.RequireHeroFitsRun(runDir, planetId);
            log($"[히어로] 행성 묶음을 읽습니다: {planetDir}");
            planet = Bundle.LoadPlanetState(planetDir);
        }
        string output = GenerateAndSaveHero(cfg, runDir, planet, planetId, log, t).Output;
        RunFolder.DropStages(runDir, BelowHero, "히어로를 새로 만들어, 옛 히어로로 구운 결과임", log);
        return output;
    }

    // planetId: 히어로를 만들 때 읽은 행성 묶음의 표지. 평면 히어로는 null 이지만, all 이 행성에서 자리를 찾지 못해 바꾼
    // 평면 히어로는 같은 실행의 행성 표지를 적습니다(그 행성과 함께 만든 히어로이기 때문).
    private static (string Output, HeroState State) GenerateAndSaveHero(
        Config cfg, string runDir, PlanetState? planet, OrderedDictionary<string, object?>? planetId, Action<string> log, long t)
    {
        HeroState hero = Pipeline.GenerateHero(cfg, planet, log);
        string output = Path.Combine(runDir, HeroDir);
        Bundle.SaveHeroState(output, hero, cfg, planetId);
        Bundle.WriteJson(Path.Combine(output, "scorecard.json"), hero.Diag.GetValueOrDefault("scorecard") ?? new OrderedDictionary<string, object?>());
        log($"[히어로] 묶음을 썼습니다: {output} ({Seconds(t):F1} s)");
        return (output, hero);
    }

    /// <summary>회랑 굽기 설정 = 히어로 묶음 설정 + (없으면) 지금 설정 파일의 굽기 절 + bake --set (bake_config).</summary>
    public static Config BakeConfig(string heroDir, Action<string> log, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = WithSeed(Bundle.ConfigFromManifest(Bundle.ReadManifest(heroDir)), seed);
        cfg = WithMissingSections(cfg, BakeSections, "[굽기]", log);
        return WithStageOverrides(cfg, sets, BakeSetPrefixes, "bake", "지형을 바꾸려면 bpcg all 에서 --set", log);
    }

    /// <summary>
    /// 히어로 설정 = 행성 묶음 설정 + hero --from 의 --set. --set 은 hero. 로 시작하는 키(히어로 자리)만 받습니다.
    /// 행성을 다시 만들지 않고 걸어 볼 자리만 옮길 때 씁니다. 묶음 설정에 hero 절이 없으면(옛 묶음) 지금 설정 파일의 값을 씁니다.
    /// </summary>
    public static Config HeroConfig(string planetDir, Action<string> log, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = WithSeed(Bundle.ConfigFromManifest(Bundle.ReadManifest(planetDir)), seed);
        if (sets is null || !sets.Any())
        {
            return cfg;
        }
        cfg = WithMissingSections(cfg, HeroSections, "[히어로]", log);
        return WithStageOverrides(cfg, sets, HeroSetPrefixes, "hero --from", "행성을 바꾸려면 bpcg all 에서 --set", log);
    }

    // 묶음 설정에 없는 절을 지금 설정 파일(같은 행성·프로필)에서 가져옵니다. 절이 생기기 전에 만든 묶음을 읽을 때 씁니다.
    private static Config WithMissingSections(Config cfg, string[] sections, string head, Action<string> log)
    {
        var missing = sections.Where(s => !cfg.Contains(s)).ToList();
        if (missing.Count == 0)
        {
            return cfg;
        }
        string planet = Convert.ToString(cfg.Sec("planet").Get("name", "earth"), CultureInfo.InvariantCulture) ?? "earth";
        string profile = cfg.Sec("profile").Get("name") is string pn && pn.Length > 0 ? pn : "laptop";
        Config current = Config.LoadConfig(planet, profile);
        var add = new OrderedDictionary<string, object?>();
        foreach (string s in missing)
        {
            if (current.Contains(s))
            {
                add[s] = current.Sec(s).AsDict();
            }
        }
        if (add.Count == 0)
        {
            return cfg;
        }
        log($"{head} 묶음 설정에 [{string.Join(", ", add.Keys)}] 가 없어 지금 설정 파일({planet}.toml)의 값을 씁니다");
        return cfg.WithOverrides(add);
    }

    // 묶음에서 이어 가는 단계(bake, hero --from)의 --set: 그 단계가 쓰는 키(prefixes)만 받고, 형식을 맞춰 덮어씁니다.
    private static Config WithStageOverrides(
        Config cfg, IEnumerable<string>? sets, string[] prefixes, string command, string elsewhere, Action<string> log)
    {
        OrderedDictionary<string, object?> overrides = SetOverrides(sets);
        if (overrides.Count == 0)
        {
            return cfg;
        }
        var bad = overrides.Keys.Where(k => !prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList();
        if (bad.Count > 0)
        {
            throw new RunError(
                $"--set: {command} 에서는 {string.Join(", ", prefixes)} 로 시작하는 키만 바꿀 수 있습니다: {string.Join(", ", bad)} ({elsewhere})");
        }
        OrderedDictionary<string, object?> checkedOv;
        try
        {
            checkedOv = Config.CheckedOverrides(cfg, overrides);
        }
        catch (ArgumentException e)
        {
            throw new RunError($"--set: {e.Message}");
        }
        foreach (KeyValuePair<string, object?> kv in checkedOv)
        {
            log($"[설정] {kv.Key} = {ConfigValue.PyRepr(kv.Value)} (묶음 {ConfigValue.PyRepr(cfg[kv.Key])})");
        }
        return cfg.WithOverrides(checkedOv);
    }

    // 굽기가 읽는 설정 키 가운데, 그 키가 생기기 전의 코드로 만든 히어로 묶음에는 없는 키.
    private static readonly string[] BakeKeysMissingInOlderBundles = ["caves.sink_spacing_m", "caves.sink_min_area_m2", "caves.full_size_area_m2"];

    /// <summary>
    /// 굽기는 히어로 묶음에 저장된 설정을 씁니다. 묶음을 만든 코드가 지금 코드보다 오래되어 굽기가 읽는 키가 없으면,
    /// 어느 키가 없는지와 고치는 법을 알려 주고 멈춥니다.
    /// </summary>
    private static void RequireBakeKeys(Config cfg, string heroDir)
    {
        var missing = BakeKeysMissingInOlderBundles.Where(key =>
        {
            string[] parts = key.Split('.');
            return !(cfg.Contains(parts[0]) && cfg.Sec(parts[0]).Contains(parts[1]));
        }).ToList();
        if (missing.Count > 0)
        {
            throw new RunError(
                $"히어로 묶음 {heroDir} 의 설정에 굽기가 읽는 키가 없습니다: {string.Join(", ", missing)}. "
                + "이 키가 생기기 전의 코드로 만든 묶음입니다. "
                + "같은 설정과 시드로 `bpcg all` 이나 `bpcg hero` 를 다시 돌려 히어로 유역을 새로 만든 뒤 구우세요.");
        }
    }

    /// <summary>
    /// 히어로 묶음에서 회랑을 outDir 에 굽습니다 (run_bake). engineDir 이 있으면 그곳에도 씁니다.
    /// 회랑 목차의 parent 에 이 히어로 묶음의 표지를 적습니다.
    /// </summary>
    public static OrderedDictionary<string, object?> RunBake(
        string heroDir, string outDir, string? engineDir, Action<string> log, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = BakeConfig(heroDir, log, seed, sets);
        RequireBakeKeys(cfg, heroDir);
        Pipeline.ApplyThreads(cfg);
        OrderedDictionary<string, object?>? heroId = RunFolder.BundleId(Bundle.ReadManifest(heroDir));
        log($"[굽기] 히어로 묶음을 읽습니다: {heroDir}");
        HeroState hero = Bundle.LoadHeroState(heroDir);
        return Corridor.BakeCorridor(hero, cfg, outDir, engineDir, log, heroId);
    }

    /// <summary>
    /// 지구본을 굽기 전에, 히어로 묶음이 이 행성 묶음에서 만든 묶음인지 봅니다. 다르면 지구본이 회랑과 상관없는 행성이나
    /// 다른 자리를 보여 주므로, 무엇이 다른지·받은 값·고치는 법을 적은 RunError 로 멈춥니다.
    /// </summary>
    public static void RequireSameRun(string planetDir, string heroDir)
    {
        OrderedDictionary<string, object?> planet = Bundle.ReadManifest(planetDir);
        string? why = RunFolder.HeroPlanetMismatch(planet, Bundle.ReadManifest(heroDir));
        if (why is not null)
        {
            throw new RunError(
                $"지구본을 굽지 못합니다: 행성 묶음 {planetDir}({RunFolder.Describe(RunFolder.BundleId(planet))})은 "
                + $"히어로 묶음 {heroDir} 를 만든 행성이 아닙니다. {why}. "
                + $"이 행성에서 히어로를 다시 만들거나(bpcg hero --from {planetDir}), 지구본 없이 구우려면 --no-globe 를 붙이세요");
        }
    }

    /// <summary>
    /// 행성 묶음과 그 행성에서 만든 히어로 묶음으로 runDir/globe 에 지구본 파일을 굽습니다 (run_globe).
    /// engineGlobeDir 이 있으면 그곳에도 씁니다. 두 묶음이 같은 실행이 아니면 RunError (<see cref="RequireSameRun"/>).
    /// </summary>
    public static string RunGlobe(string planetDir, string heroDir, string runDir, string? engineGlobeDir, Action<string> log)
    {
        RequireSameRun(planetDir, heroDir);
        OrderedDictionary<string, object?> man = Bundle.ReadManifest(planetDir);
        OrderedDictionary<string, object?> heroMan = Bundle.ReadManifest(heroDir);
        Config cfg = Bundle.ConfigFromManifest(man);
        Pipeline.ApplyThreads(cfg);
        string output = Path.Combine(runDir, GlobeDir);
        Globe.BakeGlobe(
            Bundle.LoadPlanetState(planetDir), cfg, output, hero: Globe.HeroFromBundle(heroMan), log: log,
            faceBasis: man.GetValueOrDefault("face_basis") as OrderedDictionary<string, object?>,
            engineDir: engineGlobeDir, parent: RunFolder.BundleId(man), heroBundle: RunFolder.BundleId(heroMan));
        return output;
    }

    /// <summary>
    /// 이번 굽기가 지구본을 굽지 않을 때(--no-globe, 행성 없음), globeDirs 의 지난 지구본 가운데 이 히어로 묶음과 함께
    /// 구운 지구본이 아닌 것을 지웁니다 (<see cref="RunFolder.DropGlobeUnlessFor"/>). 실행 폴더의 globe/ 와 엔진의 globe/ 에 씁니다.
    /// </summary>
    public static void DropGlobesNotFor(string heroDir, IEnumerable<string> globeDirs, Action<string> log)
    {
        OrderedDictionary<string, object?>? heroId = RunFolder.BundleId(Bundle.ReadManifest(heroDir));
        foreach (string dir in globeDirs)
        {
            RunFolder.DropGlobeUnlessFor(dir, heroId, log);
        }
    }

    /// <summary>엔진 굽기 폴더의 지난 지구본을 지웁니다 (행성이 없는 실행, cli.py 의 _drop_engine_globe).</summary>
    public static void DropEngineGlobe(string engineDir, Action<string> log)
    {
        string old = Path.Combine(engineDir, GlobeDir);
        if (File.Exists(Path.Combine(old, "globe.json")))
        {
            Directory.Delete(old, true);
            log($"[지구본] 행성이 없는 실행이라 엔진의 지난 지구본을 지웠습니다: {old}");
        }
    }

    /// <summary>
    /// 행성 → 히어로 → 회랑 굽기 → 지구본 (all 명령). 행성에서 히어로 자리를 찾지 못하면 평면 히어로로 바꿉니다.
    /// engineDir 이 있으면 회랑과 지구본을 그곳에도 씁니다. 반환: runDir.
    /// 실행 폴더 전체를 새로 쓰므로, 첫 묶음(행성, --flat 이면 히어로)을 쓴 뒤 나머지 단계 폴더를 지우고 차례로 다시 씁니다.
    /// 평면 히어로 실행이 끝나면 planet/ 과 globe/ 가 남지 않습니다.
    /// </summary>
    public static string All(Config cfg, string runDir, bool flat, string? engineDir, Action<string> log)
    {
        long t = Stopwatch.GetTimestamp();
        string heroDir;
        HeroState hero;
        PlanetState? planet = null;
        OrderedDictionary<string, object?>? planetMan = null;
        if (flat)
        {
            (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, null, null, log, Stopwatch.GetTimestamp());
            RunFolder.DropStages(runDir, [PlanetDir, CorridorDir, GlobeDir], "평면 히어로로 실행 폴더를 새로 써, 지난 실행의 결과임", log);
        }
        else
        {
            string planetDir;
            (planetDir, planet) = GenerateAndSavePlanet(cfg, runDir, log);
            RunFolder.DropStages(runDir, BelowPlanet, "행성을 새로 만들어, 옛 행성으로 만든 결과임", log);
            UseSavedPrecision(planet.Fields);
            planetMan = Bundle.ReadManifest(planetDir);
            cfg = Bundle.ConfigFromManifest(planetMan);
            OrderedDictionary<string, object?>? planetId = RunFolder.BundleId(planetMan);
            try
            {
                (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, planet, planetId, log, Stopwatch.GetTimestamp());
            }
            catch (NoHeroSiteException e)
            {
                // 자리가 없을 때만 바꿉니다. 설정 값 오류나 코드 결함으로 난 예외까지 잡으면,
                // 오류가 알려지지 않고 행성과 상관없는 평면 히어로가 결과로 나갑니다.
                // 바꾼 평면 히어로도 이 행성과 같은 실행이므로 행성의 표지를 적습니다.
                log($"[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: {e.Message}");
                (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, null, planetId, log, Stopwatch.GetTimestamp());
            }
        }
        UseSavedPrecision(hero.Fields);
        OrderedDictionary<string, object?> heroMan = Bundle.ReadManifest(heroDir);
        OrderedDictionary<string, object?>? heroId = RunFolder.BundleId(heroMan);
        Corridor.BakeCorridor(hero, BakeConfig(heroDir, log), Path.Combine(runDir, CorridorDir), engineDir, log, heroId);
        if (planet is not null && planetMan is not null)
        {
            Globe.BakeGlobe(planet, Bundle.ConfigFromManifest(planetMan), Path.Combine(runDir, GlobeDir),
                hero: Globe.HeroFromBundle(heroMan), log: log,
                faceBasis: planetMan.GetValueOrDefault("face_basis") as OrderedDictionary<string, object?>,
                engineDir: engineDir is null ? null : Path.Combine(engineDir, GlobeDir),
                parent: RunFolder.BundleId(planetMan), heroBundle: heroId);
        }
        else if (engineDir is not null)
        {
            DropEngineGlobe(engineDir, log);
        }
        log($"[전체] 끝: {Seconds(t):F1} s → {runDir}");
        return runDir;
    }
}
