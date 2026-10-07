using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Bake;
using Bpcg.Core;

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

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    // 디스크를 거치던 단계 경계의 float32 반올림을 유지합니다.
    // 배열을 직접 갱신하므로 상태 객체와 strata_bottom_m을 공유하는 Columns도 그대로 씁니다.
    private static void UseSavedPrecision(FieldSet fields)
    {
        foreach (KeyValuePair<string, Array> field in fields)
        {
            if (Core.Fields.All.TryGetValue(field.Key, out FieldInfo? info)
                && info.Dtype == "float32" && field.Value is double[] values)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = (double)(float)values[i];
                }
            }
        }
    }

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

    /// <summary>행성을 만들어 runDir/planet 에 묶음·면 텍스처를 씁니다 (run_planet). 반환: 묶음 폴더.</summary>
    public static string RunPlanet(Config cfg, string runDir, Action<string> log) =>
        GenerateAndSavePlanet(cfg, runDir, log).Output;

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

    /// <summary>히어로를 만들어 runDir/hero 에 묶음을 씁니다 (run_hero). planetDir 이 없거나 flat 이면 평면 히어로.</summary>
    public static string RunHero(Config cfg, string runDir, string? planetDir, bool flat, Action<string> log)
    {
        long t = Stopwatch.GetTimestamp();
        PlanetState? planet = null;
        if (!flat)
        {
            if (planetDir is null)
            {
                throw new RunError("히어로를 만들려면 --from <행성 묶음> 이나 --flat 이 필요합니다");
            }
            log($"[히어로] 행성 묶음을 읽습니다: {planetDir}");
            planet = Bundle.LoadPlanetState(planetDir);
        }
        return GenerateAndSaveHero(cfg, runDir, planet, log, t).Output;
    }

    private static (string Output, HeroState State) GenerateAndSaveHero(
        Config cfg, string runDir, PlanetState? planet, Action<string> log, long t)
    {
        HeroState hero = Pipeline.GenerateHero(cfg, planet, log);
        string output = Path.Combine(runDir, HeroDir);
        Bundle.SaveHeroState(output, hero, cfg);
        Bundle.WriteJson(Path.Combine(output, "scorecard.json"), hero.Diag.GetValueOrDefault("scorecard") ?? new OrderedDictionary<string, object?>());
        log($"[히어로] 묶음을 썼습니다: {output} ({Seconds(t):F1} s)");
        return (output, hero);
    }

    /// <summary>회랑 굽기 설정 = 히어로 묶음 설정 + (없으면) 지금 설정 파일의 굽기 절 + bake --set (bake_config).</summary>
    public static Config BakeConfig(string heroDir, Action<string> log, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = WithSeed(Bundle.ConfigFromManifest(Bundle.ReadManifest(heroDir)), seed);
        var missing = BakeSections.Where(s => !cfg.Contains(s)).ToList();
        if (missing.Count > 0)
        {
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
            if (add.Count > 0)
            {
                cfg = cfg.WithOverrides(add);
                log($"[굽기] 묶음 설정에 [{string.Join(", ", add.Keys)}] 가 없어 지금 설정 파일({planet}.toml)의 값을 씁니다");
            }
        }
        OrderedDictionary<string, object?> overrides = SetOverrides(sets);
        if (overrides.Count > 0)
        {
            var bad = overrides.Keys.Where(k => !BakeSetPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList();
            if (bad.Count > 0)
            {
                throw new RunError(
                    $"--set: bake 에서는 {string.Join(", ", BakeSetPrefixes)} 로 시작하는 키만 바꿀 수 있습니다: {string.Join(", ", bad)} (지형을 바꾸려면 bpcg all 에서 --set)");
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
            cfg = cfg.WithOverrides(checkedOv);
        }
        return cfg;
    }

    /// <summary>히어로 묶음에서 회랑을 outDir 에 굽습니다 (run_bake). engineDir 이 있으면 그곳에도 씁니다.</summary>
    public static OrderedDictionary<string, object?> RunBake(
        string heroDir, string outDir, string? engineDir, Action<string> log, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = BakeConfig(heroDir, log, seed, sets);
        Pipeline.ApplyThreads(cfg);
        log($"[굽기] 히어로 묶음을 읽습니다: {heroDir}");
        HeroState hero = Bundle.LoadHeroState(heroDir);
        return Corridor.BakeCorridor(hero, cfg, outDir, engineDir, log);
    }

    /// <summary>행성 묶음에서 runDir/globe 에 지구본 파일을 굽습니다 (run_globe). engineGlobeDir 이 있으면 그곳에도 씁니다.</summary>
    public static string RunGlobe(string planetDir, string runDir, string? engineGlobeDir, Action<string> log)
    {
        OrderedDictionary<string, object?> man = Bundle.ReadManifest(planetDir);
        Config cfg = Bundle.ConfigFromManifest(man);
        Pipeline.ApplyThreads(cfg);
        string output = Path.Combine(runDir, GlobeDir);
        Globe.BakeGlobe(
            Bundle.LoadPlanetState(planetDir), cfg, output, hero: Globe.HeroFromRun(runDir), log: log,
            faceBasis: man.GetValueOrDefault("face_basis") as OrderedDictionary<string, object?>,
            engineDir: engineGlobeDir);
        return output;
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
    /// </summary>
    public static string All(Config cfg, string runDir, bool flat, string? engineDir, Action<string> log)
    {
        long t = Stopwatch.GetTimestamp();
        string heroDir;
        HeroState hero;
        PlanetState? planet = null;
        string? planetDir = null;
        if (flat)
        {
            (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, null, log, Stopwatch.GetTimestamp());
        }
        else
        {
            (planetDir, planet) = GenerateAndSavePlanet(cfg, runDir, log);
            UseSavedPrecision(planet.Fields);
            cfg = Bundle.ConfigFromManifest(Bundle.ReadManifest(planetDir));
            try
            {
                (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, planet, log, Stopwatch.GetTimestamp());
            }
            catch (ArgumentException e)
            {
                log($"[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: {e.Message}");
                (heroDir, hero) = GenerateAndSaveHero(cfg, runDir, null, log, Stopwatch.GetTimestamp());
            }
        }
        UseSavedPrecision(hero.Fields);
        Corridor.BakeCorridor(hero, BakeConfig(heroDir, log), Path.Combine(runDir, CorridorDir), engineDir, log);
        if (!flat)
        {
            var man = Bundle.ReadManifest(planetDir!);
            Globe.BakeGlobe(planet!, Bundle.ConfigFromManifest(man), Path.Combine(runDir, GlobeDir),
                hero: Globe.HeroFromRun(runDir), log: log,
                faceBasis: man.GetValueOrDefault("face_basis") as OrderedDictionary<string, object?>,
                engineDir: engineDir is null ? null : Path.Combine(engineDir, GlobeDir));
        }
        else if (engineDir is not null)
        {
            DropEngineGlobe(engineDir, log);
        }
        log($"[전체] 끝: {Seconds(t):F1} s → {runDir}");
        return runDir;
    }
}
