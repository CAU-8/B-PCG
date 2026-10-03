using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bpcg.Bake;
using Bpcg.Core;

namespace Bpcg.Cli;

/// <summary>명령줄 오류로 멈춤 (Python SystemExit(글)).</summary>
public sealed class CliExit(string message, int code = 1) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// 명령줄 실행 bpcg (src/bpcg/cli.py 의 planet·hero·bake·all, docs/pipeline.md 13장). studio 는 옮기지 않습니다.
/// </summary>
public static class Program
{
    public const string PlanetDir = "planet";
    public const string HeroDir = "hero";
    public const string CorridorDir = "corridor";
    public const string TextureDir = "textures";
    public const string GlobeDir = "globe";
    public static readonly string[] BakeSections = ["detail"];
    public static readonly string[] BakeSetPrefixes = ["detail.", "profile.corridor."];

    private static void Say(string msg)
    {
        Console.WriteLine(msg);
        Console.Out.Flush();
    }

    /// <summary>명령줄 인자 (argparse 결과).</summary>
    public sealed class Args
    {
        public string Command { get; set; } = "";

        public string Planet { get; set; } = "earth";

        public string Profile { get; set; } = "laptop";

        public string? Out { get; set; }

        public long? Seed { get; set; }

        public List<string> Set { get; } = [];

        public string? FromDir { get; set; }

        public bool Flat { get; set; }

        public string? Hero { get; set; }

        public bool Engine { get; set; }

        public bool NoGlobe { get; set; }
    }

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["planet"] = ["--planet", "--profile", "--out", "--seed", "--set"],
        ["hero"] = ["--planet", "--profile", "--out", "--seed", "--set", "--from", "--flat"],
        ["bake"] = ["--hero", "--out", "--engine", "--seed", "--set", "--no-globe"],
        ["all"] = ["--planet", "--profile", "--out", "--seed", "--set", "--flat", "--engine"],
    };

    private const string Usage = "usage: bpcg {planet,hero,bake,all} ...";

    /// <summary>argparse 와 같은 뜻으로 인자를 읽습니다. 틀리면 CliExit(코드 2).</summary>
    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0 || !Allowed.ContainsKey(argv[0]))
        {
            throw new CliExit($"{Usage}\nbpcg: error: 명령은 planet, hero, bake, all 가운데 하나여야 합니다", 2);
        }
        var a = new Args { Command = argv[0] };
        string[] ok = Allowed[a.Command];
        for (int i = 1; i < argv.Length; i++)
        {
            string tok = argv[i];
            string? inline = null;
            int eq = tok.IndexOf('=');
            if (tok.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inline = tok[(eq + 1)..];
                tok = tok[..eq];
            }
            if (!ok.Contains(tok))
            {
                throw new CliExit($"{Usage}\nbpcg {a.Command}: error: unrecognized arguments: {argv[i]}", 2);
            }
            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }
                if (i + 1 >= argv.Length)
                {
                    throw new CliExit($"{Usage}\nbpcg {a.Command}: error: argument {tok}: expected one argument", 2);
                }
                i++;
                return argv[i];
            }
            switch (tok)
            {
                case "--planet":
                    a.Planet = Value();
                    break;
                case "--profile":
                    a.Profile = Value();
                    break;
                case "--out":
                    a.Out = Value();
                    break;
                case "--seed":
                    string sv = Value();
                    a.Seed = long.TryParse(sv, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long sd)
                        ? sd
                        : throw new CliExit($"{Usage}\nbpcg {a.Command}: error: argument --seed: invalid int value: '{sv}'", 2);
                    break;
                case "--set":
                    a.Set.Add(Value());
                    break;
                case "--from":
                    a.FromDir = Value();
                    break;
                case "--hero":
                    a.Hero = Value();
                    break;
                case "--flat":
                    a.Flat = true;
                    break;
                case "--engine":
                    a.Engine = true;
                    break;
                case "--no-globe":
                    a.NoGlobe = true;
                    break;
            }
        }
        if (a.Command == "bake" && a.Hero is null)
        {
            throw new CliExit($"{Usage}\nbpcg bake: error: the following arguments are required: --hero", 2);
        }
        return a;
    }

    private static OrderedDictionary<string, object?> SetOverrides(IEnumerable<string>? items)
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
                throw new CliExit($"--set: {e.Message}");
            }
        }
        return o;
    }

    private static Config ConfigOf(Args args)
    {
        OrderedDictionary<string, object?> overrides = SetOverrides(args.Set);
        if (args.Seed is long seed)
        {
            overrides["planet.seed"] = seed;
        }
        Config b = Config.LoadConfig(args.Planet, args.Profile);
        if (overrides.Count == 0)
        {
            return b;
        }
        OrderedDictionary<string, object?> checkedOv;
        try
        {
            checkedOv = Config.CheckedOverrides(b, overrides);
        }
        catch (ArgumentException e)
        {
            throw new CliExit($"--set: {e.Message}");
        }
        foreach (KeyValuePair<string, object?> kv in checkedOv)
        {
            if (kv.Key != "planet.seed")
            {
                Say($"[설정] {kv.Key} = {ConfigValue.PyRepr(kv.Value)} (기본 {ConfigValue.PyRepr(b[kv.Key])})");
            }
        }
        return b.WithOverrides(checkedOv);
    }

    private static Config WithSeed(Config cfg, long? seed) =>
        seed is long s ? cfg.WithOverrides([new("planet.seed", s)]) : cfg;

    private static string OutDir(Args args) => args.Out ?? Path.Combine(Paths.Out, args.Planet);

    private static string Parent(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        return Path.GetDirectoryName(trimmed) is string d && d.Length > 0 ? d : ".";
    }

    private static string PlanetBundleDir(string path)
    {
        if (File.Exists(Path.Combine(path, "manifest.json")))
        {
            return path;
        }
        if (File.Exists(Path.Combine(path, PlanetDir, "manifest.json")))
        {
            return Path.Combine(path, PlanetDir);
        }
        throw new CliExit($"행성 묶음을 찾지 못했습니다: {path} (planet/manifest.json 이 없습니다)");
    }

    private static string HeroBundleDir(string path)
    {
        if (File.Exists(Path.Combine(path, "manifest.json")))
        {
            return path;
        }
        if (File.Exists(Path.Combine(path, HeroDir, "manifest.json")))
        {
            return Path.Combine(path, HeroDir);
        }
        throw new CliExit($"히어로 묶음을 찾지 못했습니다: {path} (manifest.json 이 없습니다)");
    }

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>행성을 만들어 runDir/planet 에 묶음·면 텍스처를 씁니다 (run_planet). 반환: 묶음 폴더.</summary>
    public static string RunPlanet(Config cfg, string runDir)
    {
        long t = Stopwatch.GetTimestamp();
        Say($"[행성] 시작: 프로필 {cfg.Sec("profile").Get("name")}, 시드 {cfg.I("planet.seed")}");
        PlanetState planet = Pipeline.GeneratePlanet(cfg, Say);
        string output = Path.Combine(runDir, PlanetDir);
        Bundle.SavePlanetState(output, planet, cfg);
        Textures.FaceTextures(planet, Path.Combine(output, TextureDir));
        Bundle.WriteJson(Path.Combine(output, "scorecard.json"), planet.Diag.GetValueOrDefault("scorecard") ?? new OrderedDictionary<string, object?>());
        Say($"[행성] 묶음을 썼습니다: {output} ({Seconds(t):F1} s)");
        return output;
    }

    /// <summary>히어로를 만들어 runDir/hero 에 묶음을 씁니다 (run_hero). planetDir 이 없거나 flat 이면 평면 히어로.</summary>
    public static string RunHero(Config cfg, string runDir, string? planetDir, bool flat)
    {
        long t = Stopwatch.GetTimestamp();
        PlanetState? planet = null;
        if (!flat)
        {
            if (planetDir is null)
            {
                throw new CliExit("히어로를 만들려면 --from <행성 묶음> 이나 --flat 이 필요합니다");
            }
            Say($"[히어로] 행성 묶음을 읽습니다: {planetDir}");
            planet = Bundle.LoadPlanetState(planetDir);
        }
        HeroState hero = Pipeline.GenerateHero(cfg, planet, Say);
        string output = Path.Combine(runDir, HeroDir);
        Bundle.SaveHeroState(output, hero, cfg);
        Bundle.WriteJson(Path.Combine(output, "scorecard.json"), hero.Diag.GetValueOrDefault("scorecard") ?? new OrderedDictionary<string, object?>());
        Say($"[히어로] 묶음을 썼습니다: {output} ({Seconds(t):F1} s)");
        return output;
    }

    /// <summary>회랑 굽기 설정 = 히어로 묶음 설정 + (없으면) 지금 설정 파일의 굽기 절 + bake --set (bake_config).</summary>
    public static Config BakeConfig(string heroDir, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = WithSeed(Bundle.ConfigFromManifest(Bundle.ReadManifest(heroDir)), seed);
        var missing = BakeSections.Where(s => !cfg.Contains(s)).ToList();
        if (missing.Count > 0)
        {
            string planet = Convert.ToString(cfg.Sec("planet").Get("name", "earth"), System.Globalization.CultureInfo.InvariantCulture) ?? "earth";
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
                Say($"[굽기] 묶음 설정에 [{string.Join(", ", add.Keys)}] 가 없어 지금 설정 파일({planet}.toml)의 값을 씁니다");
            }
        }
        OrderedDictionary<string, object?> overrides = SetOverrides(sets);
        if (overrides.Count > 0)
        {
            var bad = overrides.Keys.Where(k => !BakeSetPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList();
            if (bad.Count > 0)
            {
                throw new CliExit(
                    $"--set: bake 에서는 {string.Join(", ", BakeSetPrefixes)} 로 시작하는 키만 바꿀 수 있습니다: {string.Join(", ", bad)} (지형을 바꾸려면 bpcg all 에서 --set)");
            }
            OrderedDictionary<string, object?> checkedOv;
            try
            {
                checkedOv = Config.CheckedOverrides(cfg, overrides);
            }
            catch (ArgumentException e)
            {
                throw new CliExit($"--set: {e.Message}");
            }
            foreach (KeyValuePair<string, object?> kv in checkedOv)
            {
                Say($"[설정] {kv.Key} = {ConfigValue.PyRepr(kv.Value)} (묶음 {ConfigValue.PyRepr(cfg[kv.Key])})");
            }
            cfg = cfg.WithOverrides(checkedOv);
        }
        return cfg;
    }

    /// <summary>히어로 묶음에서 회랑을 굽습니다 (run_bake). engine 이면 engine/baked 에도 씁니다.</summary>
    public static OrderedDictionary<string, object?> RunBake(string heroDir, string outDir, bool engine, long? seed = null, IEnumerable<string>? sets = null)
    {
        Config cfg = BakeConfig(heroDir, seed, sets);
        Say($"[굽기] 히어로 묶음을 읽습니다: {heroDir}");
        HeroState hero = Bundle.LoadHeroState(heroDir);
        string? engineDir = engine ? Path.Combine(Paths.Root, "engine", "baked") : null;
        return Corridor.BakeCorridor(hero, cfg, outDir, engineDir, Say);
    }

    /// <summary>행성 묶음에서 지구본 파일을 굽습니다 (run_globe).</summary>
    public static string RunGlobe(string planetDir, string runDir, bool engine)
    {
        OrderedDictionary<string, object?> man = Bundle.ReadManifest(planetDir);
        string output = Path.Combine(runDir, GlobeDir);
        Globe.BakeGlobe(
            Bundle.LoadPlanetState(planetDir), Bundle.ConfigFromManifest(man), output, hero: Globe.HeroFromRun(runDir), log: Say,
            faceBasis: man.GetValueOrDefault("face_basis") as OrderedDictionary<string, object?>,
            engineDir: engine ? Path.Combine(Paths.Root, "engine", "baked", GlobeDir) : null);
        return output;
    }

    private static void DropEngineGlobe()
    {
        string old = Path.Combine(Paths.Root, "engine", "baked", GlobeDir);
        if (File.Exists(Path.Combine(old, "globe.json")))
        {
            Directory.Delete(old, true);
            Say($"[지구본] 행성이 없는 실행이라 엔진의 지난 지구본을 지웠습니다: {old}");
        }
    }

    private static int CmdPlanet(Args args)
    {
        Config cfg = ConfigOf(args);
        Pipeline.ApplyThreads(cfg);
        RunPlanet(cfg, OutDir(args));
        return 0;
    }

    private static int CmdHero(Args args)
    {
        if (args.Flat)
        {
            Config c = ConfigOf(args);
            Pipeline.ApplyThreads(c);
            RunHero(c, OutDir(args), null, flat: true);
            return 0;
        }
        if (args.FromDir is null)
        {
            throw new CliExit("hero 는 --from <행성 묶음 폴더> 나 --flat 중 하나가 필요합니다");
        }
        if (args.Set.Count > 0)
        {
            throw new CliExit("--set 은 hero --flat 에서만 씁니다 (--from 은 묶음 설정을 그대로 씀)");
        }
        string planetDir = PlanetBundleDir(args.FromDir);
        Config cfg = WithSeed(Bundle.ConfigFromManifest(Bundle.ReadManifest(planetDir)), args.Seed);
        string runDir = args.Out ?? Parent(planetDir);
        Pipeline.ApplyThreads(cfg);
        RunHero(cfg, runDir, planetDir, flat: false);
        return 0;
    }

    private static int CmdBake(Args args)
    {
        string heroDir = HeroBundleDir(args.Hero!);
        string outDir = args.Out ?? Path.Combine(Parent(heroDir), CorridorDir);
        RunBake(heroDir, outDir, args.Engine, args.Seed, args.Set.Count > 0 ? args.Set : null);
        string planetDir = Path.Combine(Parent(heroDir), PlanetDir);
        if (args.NoGlobe)
        {
            return 0;
        }
        if (File.Exists(Path.Combine(planetDir, "manifest.json")))
        {
            RunGlobe(planetDir, Parent(heroDir), args.Engine);
        }
        else if (args.Engine)
        {
            DropEngineGlobe();
        }
        return 0;
    }

    private static int CmdAll(Args args)
    {
        long t = Stopwatch.GetTimestamp();
        Config cfg = ConfigOf(args);
        Pipeline.ApplyThreads(cfg);
        string runDir = OutDir(args);
        string heroDir;
        string? planetDir = null;
        if (args.Flat)
        {
            heroDir = RunHero(cfg, runDir, null, flat: true);
        }
        else
        {
            planetDir = RunPlanet(cfg, runDir);
            cfg = Bundle.ConfigFromManifest(Bundle.ReadManifest(planetDir));
            try
            {
                heroDir = RunHero(cfg, runDir, planetDir, flat: false);
            }
            catch (ArgumentException e)
            {
                Say($"[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: {e.Message}");
                heroDir = RunHero(cfg, runDir, null, flat: true);
            }
        }
        RunBake(heroDir, Path.Combine(runDir, CorridorDir), args.Engine);
        if (!args.Flat)
        {
            RunGlobe(planetDir!, runDir, args.Engine);
        }
        else if (args.Engine)
        {
            DropEngineGlobe();
        }
        Say($"[전체] 끝: {Seconds(t):F1} s → {runDir}");
        return 0;
    }

    /// <summary>명령줄 입구. 반환: 종료 코드.</summary>
    public static int Main(string[] argv)
    {
        try
        {
            Args args = Parse(argv);
            return args.Command switch
            {
                "planet" => CmdPlanet(args),
                "hero" => CmdHero(args),
                "bake" => CmdBake(args),
                _ => CmdAll(args),
            };
        }
        catch (CliExit e)
        {
            Console.Error.WriteLine(e.Message);
            return e.Code;
        }
    }
}
