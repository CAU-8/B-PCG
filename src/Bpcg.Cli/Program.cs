using System;
using System.Collections.Generic;
using System.IO;
using Bpcg.Core;

namespace Bpcg.Cli;

/// <summary>명령줄 오류로 멈춤 (Python SystemExit(글)).</summary>
public sealed class CliExit(string message, int code = 1) : RunError(message, code);

/// <summary>
/// 명령줄 실행 bpcg (src/bpcg/cli.py 의 planet·hero·bake·all, docs/pipeline.md 13장). studio 는 옮기지 않습니다.
/// 실행 단위는 라이브러리의 <see cref="Runs"/> 에 있습니다. compare 는 <see cref="CompareCommand"/>, figures 는
/// <see cref="FiguresCommand"/> 가 받습니다.
/// </summary>
public static class Program
{
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

    private const string Usage = "usage: bpcg {planet,hero,bake,all,compare,figures} ...";

    /// <summary>bpcg --help 가 보여 주는 사용법. 옵션 이름은 <see cref="Allowed"/> 와 같아야 합니다 (시험이 봅니다).</summary>
    public const string Help = Usage + """


        행성 하나를 만들고, 그 안의 강 유역 하나(히어로 유역)를 25 m 칸으로 다시 풀고,
        걸어 다닐 띠(회랑)를 Godot 엔진이 읽는 파일로 굽습니다.

        명령
          all      행성 → 히어로 유역 → 회랑 → 지구본을 한 번에 만듭니다
          planet   행성만 만듭니다 (<out>/planet)
          hero     히어로 유역을 만듭니다. --from <행성을 만든 실행 폴더> 나 --flat 이 필요합니다
          bake     히어로 유역에서 회랑과 지구본을 굽습니다. --hero <히어로 묶음 폴더> 가 필요합니다
          compare  기존 지형 생성 방법과 같은 시드로 비교합니다 (bpcg compare methods|run|list|render)
          figures  결과 그림을 그립니다 (bpcg figures results|cubesphere)
          studio   브라우저에서 값을 바꿔 돌리고 지도로 봅니다 (uv run bpcg studio)

        옵션
          --planet <이름>    행성 설정 configs/planets/<이름>.toml (기본 earth)
          --profile <이름>   크기와 해상도 묶음 configs/profiles/<이름>.toml: tiny, laptop, lab (기본 laptop)
          --seed <정수>      출발 번호. 같은 시드면 같은 행성이 나옵니다
          --out <폴더>       결과를 쓸 실행 폴더 (기본 out/<행성 이름>)
          --set 키=값        설정 값 하나를 바꿉니다. 여러 번 쓸 수 있습니다 (예: --set landscape.theta=0.5)
          --from <폴더>      hero: 행성을 만든 실행 폴더. 이때 --set 은 hero. 로 시작하는 키만 받습니다
          --flat             hero, all: 행성 없이, 정해 둔 융기와 출구로 히어로 유역만 만듭니다
          --hero <폴더>      bake: 히어로 묶음 폴더. 이때 --set 은 detail., profile.corridor. 로 시작하는 키만 받습니다
          --engine           bake, all: engine/baked 에도 써서 Godot 가 바로 읽게 합니다
          --no-globe         bake: 지구본을 굽지 않습니다

        예
          uv run bpcg all --profile tiny
          uv run bpcg all --profile laptop --seed 15 --out out/s15
          uv run bpcg hero --from out/earth --set 'hero.site_lat_lon_deg=[8.39, -0.92]'
          uv run bpcg bake --hero out/earth/hero --engine
        """;

    private static bool WantsHelp(string[] argv) =>
        argv.Length > 0 && (argv[0] == "help" || Array.Exists(argv, a => a is "--help" or "-h"));

    /// <summary>argparse 와 같은 뜻으로 인자를 읽습니다. 틀리면 CliExit(코드 2).</summary>
    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0 || !Allowed.ContainsKey(argv[0]))
        {
            throw new CliExit($"{Usage}\nbpcg: error: 명령은 planet, hero, bake, all, compare, figures 가운데 하나여야 합니다", 2);
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

    private static Config ConfigOf(Args args)
    {
        OrderedDictionary<string, object?> overrides = Runs.SetOverrides(args.Set);
        if (args.Seed is long seed)
        {
            overrides["planet.seed"] = seed;
        }
        CheckConfigName("planets", args.Planet, "--planet", "행성 설정");
        CheckConfigName("profiles", args.Profile, "--profile", "프로필");
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

    // 이름이 틀리면 파일을 열다가 나는 예외 대신, 받은 이름과 쓸 수 있는 이름을 알려 주고 멈춥니다.
    private static void CheckConfigName(string kind, string name, string flag, string label)
    {
        string dir = Path.Combine(Paths.Configs, kind);
        string path = name.EndsWith(".toml", StringComparison.Ordinal) ? name : Path.Combine(dir, name + ".toml");
        if (File.Exists(path))
        {
            return;
        }
        string[] names = Directory.Exists(dir)
            ? Array.ConvertAll(Directory.GetFiles(dir, "*.toml"), f => Path.GetFileNameWithoutExtension(f))
            : [];
        Array.Sort(names, StringComparer.Ordinal);
        throw new CliExit($"{flag} {name}: {label} 파일이 없습니다 ({path}). 쓸 수 있는 이름은 {string.Join(", ", names)} 입니다");
    }

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
        if (File.Exists(Path.Combine(path, Runs.PlanetDir, "manifest.json")))
        {
            return Path.Combine(path, Runs.PlanetDir);
        }
        throw new CliExit($"행성 묶음을 찾지 못했습니다: {path} (planet/manifest.json 이 없습니다)");
    }

    private static string HeroBundleDir(string path)
    {
        if (File.Exists(Path.Combine(path, "manifest.json")))
        {
            return path;
        }
        if (File.Exists(Path.Combine(path, Runs.HeroDir, "manifest.json")))
        {
            return Path.Combine(path, Runs.HeroDir);
        }
        throw new CliExit($"히어로 묶음을 찾지 못했습니다: {path} (manifest.json 이 없습니다)");
    }

    /// <summary>bpcg bake --engine 이 쓰는 엔진 굽기 폴더 (engine/baked).</summary>
    private static string EngineBaked => Path.Combine(Paths.Root, "engine", "baked");

    private static int CmdPlanet(Args args)
    {
        Config cfg = ConfigOf(args);
        Pipeline.ApplyThreads(cfg);
        Runs.RunPlanet(cfg, OutDir(args), Say);
        return 0;
    }

    private static int CmdHero(Args args)
    {
        if (args.Flat)
        {
            Config c = ConfigOf(args);
            Pipeline.ApplyThreads(c);
            Runs.RunHero(c, OutDir(args), null, flat: true, Say);
            return 0;
        }
        if (args.FromDir is null)
        {
            throw new CliExit("hero 는 --from <행성 묶음 폴더> 나 --flat 중 하나가 필요합니다");
        }
        string planetDir = PlanetBundleDir(args.FromDir);
        Config cfg = Runs.HeroConfig(planetDir, Say, args.Seed, args.Set.Count > 0 ? args.Set : null);
        string runDir = args.Out ?? Parent(planetDir);
        Pipeline.ApplyThreads(cfg);
        Runs.RunHero(cfg, runDir, planetDir, flat: false, Say);
        return 0;
    }

    private static int CmdBake(Args args)
    {
        string heroDir = HeroBundleDir(args.Hero!);
        string outDir = args.Out ?? Path.Combine(Parent(heroDir), Runs.CorridorDir);
        Runs.RunBake(heroDir, outDir, args.Engine ? EngineBaked : null, Say, args.Seed, args.Set.Count > 0 ? args.Set : null);
        string planetDir = Path.Combine(Parent(heroDir), Runs.PlanetDir);
        if (args.NoGlobe)
        {
            return 0;
        }
        if (File.Exists(Path.Combine(planetDir, "manifest.json")))
        {
            Runs.RunGlobe(planetDir, Parent(heroDir), args.Engine ? Path.Combine(EngineBaked, Runs.GlobeDir) : null, Say);
        }
        else if (args.Engine)
        {
            Runs.DropEngineGlobe(EngineBaked, Say);
        }
        return 0;
    }

    private static int CmdAll(Args args)
    {
        Config cfg = ConfigOf(args);
        Pipeline.ApplyThreads(cfg);
        Runs.All(cfg, OutDir(args), args.Flat, args.Engine ? EngineBaked : null, Say);
        return 0;
    }

    /// <summary>명령줄 입구. 반환: 종료 코드.</summary>
    public static int Main(string[] argv)
    {
        try
        {
            if (argv.Length > 0 && argv[0] == "compare")
            {
                return CompareCommand.Dispatch(argv[1..]);
            }
            if (argv.Length > 0 && argv[0] == "figures")
            {
                return FiguresCommand.Dispatch(argv[1..]);
            }
            if (WantsHelp(argv))
            {
                Console.Out.WriteLine(Help);
                return 0;
            }
            Args args = Parse(argv);
            return args.Command switch
            {
                "planet" => CmdPlanet(args),
                "hero" => CmdHero(args),
                "bake" => CmdBake(args),
                _ => CmdAll(args),
            };
        }
        catch (RunError e)
        {
            Console.Error.WriteLine(e.Message);
            return e.Code;
        }
    }
}
