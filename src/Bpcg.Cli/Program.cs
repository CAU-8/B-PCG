using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.IO;

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

        /// <summary>--planet 을 명령줄에 적었는지 (hero --from 이 묶음의 값과 비교할 때 씀).</summary>
        public bool PlanetGiven { get; set; }

        /// <summary>--profile 을 명령줄에 적었는지.</summary>
        public bool ProfileGiven { get; set; }

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

    // 값을 받지 않는 옵션 (있으면 켜짐).
    private static readonly HashSet<string> Flags = ["--flat", "--engine", "--no-globe"];

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
            if (inline is not null && Flags.Contains(tok))
            {
                // --flat=false 를 --flat 으로 읽으면 끄려던 옵션이 켜집니다. 값을 붙인 꼴은 받지 않습니다.
                throw new CliExit(
                    $"{Usage}\nbpcg {a.Command}: error: argument {tok}: 값을 받지 않는 옵션입니다 (받은 값 '{inline}'). "
                    + $"켜려면 {tok} 만 쓰고, 끄려면 옵션을 빼세요", 2);
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
                    a.PlanetGiven = true;
                    break;
                case "--profile":
                    a.Profile = Value();
                    a.ProfileGiven = true;
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

    // --planet 에 TOML 경로를 준 경우에는 파일 이름(확장자 뺌)을 실행 폴더 이름으로 씁니다.
    // 경로를 그대로 붙이면 Path.Combine 이 앞의 out 폴더를 버려, TOML 파일 자체가 실행 폴더가 됩니다.
    private static string OutDir(Args args) => args.Out ?? Path.Combine(Paths.Out, PlanetRunName(args.Planet));

    internal static string PlanetRunName(string planet) =>
        planet.EndsWith(".toml", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(planet) : planet;

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
        CheckFromBundle(args, planetDir);
        Config cfg = Runs.HeroConfig(planetDir, Say, args.Seed, args.Set.Count > 0 ? args.Set : null);
        string runDir = args.Out ?? Parent(planetDir);
        Pipeline.ApplyThreads(cfg);
        Runs.RunHero(cfg, runDir, planetDir, flat: false, Say);
        return 0;
    }

    // hero --from 은 행성 묶음의 설정으로 히어로를 만듭니다. 명령줄의 --planet·--profile·--seed 가 묶음과 다르면, 묶음 값이
    // 조용히 이기거나(행성·프로필) 행성과 다른 시드로 히어로만 다시 흔들게(시드) 됩니다. 그래서 다르면 멈추고 알립니다.
    private static void CheckFromBundle(Args args, string planetDir)
    {
        Config bundle = Bpcg.Bake.Bundle.ConfigFromManifest(Bpcg.Bake.Bundle.ReadManifest(planetDir));
        var problems = new List<string>();
        string bundlePlanet = bundle.Sec("planet").Get("name") as string ?? "";
        string bundleProfile = bundle.Sec("profile").Get("name") as string ?? "";
        if (args.PlanetGiven && PlanetRunName(args.Planet) != bundlePlanet)
        {
            problems.Add($"--planet {args.Planet} (묶음은 {bundlePlanet})");
        }
        if (args.ProfileGiven && Path.GetFileNameWithoutExtension(args.Profile) != bundleProfile)
        {
            problems.Add($"--profile {args.Profile} (묶음은 {bundleProfile})");
        }
        long bundleSeed = bundle.I("planet.seed");
        if (args.Seed is long seed && seed != bundleSeed)
        {
            problems.Add($"--seed {seed} (묶음은 {bundleSeed})");
        }
        if (problems.Count > 0)
        {
            throw new CliExit(
                $"hero --from 은 행성 묶음({planetDir})의 행성·프로필·시드로 히어로를 만듭니다. 명령줄 값이 묶음과 다릅니다: "
                + string.Join(", ", problems) + ". 이 옵션을 빼거나 묶음과 같은 값을 주세요. 다른 행성·프로필·시드로 만들려면 bpcg all 을 쓰세요");
        }
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

    // 윈도우 콘솔은 기본 코드 페이지(한국어 윈도우는 CP949)로 글자를 씁니다. 스튜디오와 시험은 기록 줄을
    // UTF-8 로 읽으므로, 그대로 두면 한글 기록 줄이 깨져 진행률과 경고를 읽지 못합니다. 그래서 UTF-8 로 맞춥니다.
    private static void UseUtf8Output()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 콘솔이 인코딩 바꾸기를 받지 않으면 원래 인코딩으로 씁니다.
        }
    }

    // 설정 값이나 입력 파일이 틀려서 나는 예외. 스택 추적 대신 메시지 한 줄과 난 곳을 보여 줍니다.
    // 그 밖의 예외(널 참조, 배열 범위 등)는 코드 결함이므로 스택 추적이 그대로 나가게 둡니다.
    private static bool IsInputError(Exception e) =>
        e is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException
            or FormatException or TomlDecodeException or JsonException or IOException or UnauthorizedAccessException;

    // 예외가 난 함수 이름 (예: Bpcg.Landscape.Solver.ReadLawParams). 알 수 없으면 예외 형 이름.
    private static string Where(Exception e) =>
        e.TargetSite is { DeclaringType: { } t } m ? $"{t.FullName}.{m.Name}" : e.GetType().Name;

    /// <summary>명령줄 입구. 반환: 종료 코드.</summary>
    public static int Main(string[] argv)
    {
        UseUtf8Output();
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
        catch (Exception e) when (IsInputError(e))
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine($"  (오류가 난 곳: {Where(e)})");
            return 1;
        }
    }
}
