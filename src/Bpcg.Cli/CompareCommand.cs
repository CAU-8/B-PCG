using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Compare;
using Bpcg.Core;

namespace Bpcg.Cli;

/// <summary>
/// bpcg compare {methods,run,list,render}: 기존 지형 생성 방법론과 같은 시드로 비교합니다 (docs/compare.md).
/// </summary>
public static class CompareCommand
{
    public const string Usage = "usage: bpcg compare {methods,run,list,render} ...";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["methods"] = ["--json"],
        ["run"] = ["--config", "--out", "--seeds", "--only", "--param", "--force"],
        ["list"] = ["--set", "--seed", "--json"],
        ["render"] = ["--set", "--seed", "--method", "--scale", "--stage"],
    };

    private static readonly HashSet<string> Flags = ["--json", "--force"];

    /// <summary>compare 의 인자 (argparse 결과).</summary>
    public sealed class Args
    {
        public string Command { get; set; } = "";

        public string Config { get; set; } = Path.Combine(Paths.Configs, "compare", "default.toml");

        public string? Out { get; set; }

        public List<long>? Seeds { get; set; }

        public List<string>? Only { get; set; }

        public List<string> Param { get; } = [];

        public bool Force { get; set; }

        public bool Json { get; set; }

        public string Set { get; set; } = "default";

        public long? Seed { get; set; }

        public string? Method { get; set; }

        public string Scale { get; set; } = "self";

        public int? Stage { get; set; }
    }

    private static CliExit Error(string sub, string message) => new($"{Usage}\nbpcg compare {sub}: error: {message}", 2);

    private static long ParseLong(string sub, string opt, string v) =>
        long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long x)
            ? x
            : throw Error(sub, $"argument {opt}: invalid int value: '{v}'");

    /// <summary>argv 는 compare 다음부터. 틀리면 CliExit(코드 2).</summary>
    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0 || !Allowed.ContainsKey(argv[0]))
        {
            throw new CliExit($"{Usage}\nbpcg compare: error: 명령은 methods, run, list, render 가운데 하나여야 합니다", 2);
        }
        var a = new Args { Command = argv[0] };
        string sub = a.Command;
        string[] ok = Allowed[sub];
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
            if (!ok.Contains(tok) || (inline is not null && Flags.Contains(tok)))
            {
                throw Error(sub, $"unrecognized arguments: {argv[i]}");
            }
            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }
                if (i + 1 >= argv.Length)
                {
                    throw Error(sub, $"argument {tok}: expected one argument");
                }
                i++;
                return argv[i];
            }
            switch (tok)
            {
                case "--json":
                    a.Json = true;
                    break;
                case "--force":
                    a.Force = true;
                    break;
                case "--config":
                    a.Config = Value();
                    break;
                case "--out":
                    a.Out = Value();
                    break;
                case "--seeds":
                    a.Seeds = [.. Value().Split(',').Select(s => ParseLong(sub, tok, s.Trim()))];
                    break;
                case "--only":
                    a.Only = [.. Value().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)];
                    break;
                case "--param":
                    a.Param.Add(Value());
                    break;
                case "--set":
                    a.Set = Value();
                    break;
                case "--seed":
                    a.Seed = ParseLong(sub, tok, Value());
                    break;
                case "--method":
                    a.Method = Value();
                    break;
                case "--scale":
                    string sc = Value();
                    a.Scale = sc is "self" or "group" ? sc : throw Error(sub, $"argument --scale: invalid choice: '{sc}' (choose from 'self', 'group')");
                    break;
                case "--stage":
                    a.Stage = (int)ParseLong(sub, tok, Value());
                    break;
            }
        }
        if (sub == "render" && (a.Seed is null || a.Method is null))
        {
            throw Error(sub, "the following arguments are required: --seed, --method");
        }
        return a;
    }

    private static void Say(string msg)
    {
        Console.WriteLine(msg);
        Console.Out.Flush();
    }

    /// <summary>--set 이름이나 경로 → 비교 묶음 폴더 (compare.json 이 있어야 함).</summary>
    public static string SetDir(string name)
    {
        bool isPath = Path.IsPathRooted(name) || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal);
        string d = isPath ? name : Path.Combine(CompareRunner.CompareOut, name);
        return File.Exists(Path.Combine(d, "compare.json")) ? d : throw new CliExit($"비교 결과가 없습니다: {d} (먼저 bpcg compare run)");
    }

    private static string Num(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("G6", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static int Methods(Args a)
    {
        if (a.Json)
        {
            var list = MethodRegistry.AllMethods().Select(m => (object?)m.Describe()).ToList();
            Say(CompareRunner.ToJson(new OrderedDictionary<string, object?> { ["methods"] = list, ["families"] = MethodRegistry.Families }));
            return 0;
        }
        foreach (CompareMethod m in MethodRegistry.AllMethods())
        {
            Say($"{m.Name,-16} [{MethodRegistry.Families[m.Family]}] {m.Label} — {m.Summary}");
            Say($"{"",-16} 근거: {m.Ref}");
            foreach (Param p in m.Params)
            {
                string rng = p.Choices is { Length: > 0 }
                    ? $" {{{string.Join(", ", p.Choices)}}}"
                    : (p.Lo is not null || p.Hi is not null ? $" [{Num(p.Lo)}, {Num(p.Hi)}]" : "");
                string unit = p.Unit.Length > 0 ? " " + p.Unit : "";
                Say($"{"",-18}{p.Name} = {Num(p.Default)}{unit}{rng}  {p.Help}");
            }
        }
        return 0;
    }

    private static int Run(Args a)
    {
        try
        {
            CompareConfig cfg = CompareRunner.LoadConfigFile(a.Config);
            var items = new List<KeyValuePair<string, object?>>();
            foreach (string item in a.Param)
            {
                (string key, object? value) = Config.ParseAssignment(item);
                items.Add(new(key, value));
            }
            CompareRunner.ApplyParamOverrides(cfg, items);
            CompareRunner.Run(cfg, a.Out, a.Seeds, a.Only, a.Force, Say);
        }
        catch (ArgumentException e)
        {
            throw new CliExit($"[비교] {e.Message}");
        }
        return 0;
    }

    private static int List(Args a)
    {
        OrderedDictionary<string, object?> data = CompareRunner.LoadSet(SetDir(a.Set));
        if (a.Json)
        {
            Say(CompareRunner.ToJson(data));
            return 0;
        }
        var defs = ((List<object?>)data["metrics"]!).Cast<OrderedDictionary<string, object?>>().ToList();
        var cols = new List<string> { "seconds" };
        cols.AddRange(defs.Select(d => (string)d["name"]!));
        var heads = new List<string> { "시간 s" };
        heads.AddRange(defs.Select(d => (string)d["label"]! + (d["unit"] is string u && u.Length > 0 ? " " + u : "")));
        foreach (OrderedDictionary<string, object?> g in ((List<object?>)data["groups"]!).Cast<OrderedDictionary<string, object?>>())
        {
            long seed = (long)g["seed"]!;
            if (a.Seed is long only && only != seed)
            {
                continue;
            }
            Say($"\n== 시드 {seed}");
            Say("id".PadRight(16) + string.Join(" | ", heads));
            foreach (OrderedDictionary<string, object?> e in ((List<object?>)g["entries"]!).Cast<OrderedDictionary<string, object?>>())
            {
                var metrics = e["metrics"] as OrderedDictionary<string, object?> ?? [];
                IEnumerable<string> vals = cols.Select(c =>
                {
                    object? v = c == "seconds" ? e["seconds"] : metrics.GetValueOrDefault(c);
                    return v is null ? "-" : Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("G3", CultureInfo.InvariantCulture);
                });
                Say(((string)e["id"]!).PadRight(16) + string.Join(" | ", vals));
            }
        }
        return 0;
    }

    private static int Render(Args a)
    {
        string setDir = SetDir(a.Set);
        long seed = a.Seed!.Value;
        try
        {
            string d = CompareRunner.EntryDir(setDir, seed, a.Method!);
            OrderedDictionary<string, object?> data = CompareRunner.LoadSet(setDir);
            var grid = (OrderedDictionary<string, object?>)((OrderedDictionary<string, object?>)data["config"]!)["grid"]!;
            double dx = Convert.ToDouble(grid["dx"], CultureInfo.InvariantCulture);
            if (a.Stage is int stage)
            {
                Say(Path.GetFullPath(CompareRender.RenderStage(d, stage, dx)));
                return 0;
            }
            double? vmin = null;
            double? vmax = null;
            if (a.Scale == "group")
            {
                var g = ((List<object?>)data["groups"]!).Cast<OrderedDictionary<string, object?>>().First(x => (long)x["seed"]! == seed);
                var entries = ((List<object?>)g["entries"]!).Cast<OrderedDictionary<string, object?>>().ToList();
                vmin = entries.Min(e => Convert.ToDouble(e["z_min"], CultureInfo.InvariantCulture));
                vmax = entries.Max(e => Convert.ToDouble(e["z_max"], CultureInfo.InvariantCulture));
            }
            Say(Path.GetFullPath(CompareRender.RenderElevation(d, dx, vmin, vmax)));
        }
        catch (ArgumentException e)
        {
            throw new CliExit($"[비교] {e.Message}");
        }
        return 0;
    }

    /// <summary>bpcg compare … (argv 는 compare 다음부터). 반환: 종료 코드.</summary>
    public static int Dispatch(string[] argv)
    {
        Args a = Parse(argv);
        return a.Command switch
        {
            "methods" => Methods(a),
            "run" => Run(a),
            "list" => List(a),
            _ => Render(a),
        };
    }
}
