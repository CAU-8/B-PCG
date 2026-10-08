using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Figures;
using Bpcg.IO;

namespace Bpcg.Cli;

/// <summary>
/// bpcg figures {results,cubesphere}: 생성 결과 그림(analysis/figures/render_results.py 를 옮김)과
/// 큐브스피어 데모 그림(analysis/demos/plot_cubesphere.py 를 옮김).
/// </summary>
public static class FiguresCommand
{
    public const string Usage = "usage: bpcg figures {results,cubesphere} ...";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["results"] = ["--out", "--etopo"],
        ["cubesphere"] = ["--n", "--R", "--out"],
    };

    /// <summary>figures 의 인자 (argparse 결과).</summary>
    public sealed class Args
    {
        public string Command { get; set; } = "";

        /// <summary>results: bpcg all 출력 폴더.</summary>
        public string? Run { get; set; }

        public string? Out { get; set; }

        public string? Etopo { get; set; }

        public int N { get; set; } = 16;

        public double R { get; set; } = 15.0;
    }

    private static CliExit Error(string sub, string message) => new($"{Usage}\nbpcg figures {sub}: error: {message}", 2);

    /// <summary>argv 는 figures 다음부터. 틀리면 CliExit(코드 2).</summary>
    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0 || !Allowed.ContainsKey(argv[0]))
        {
            throw new CliExit($"{Usage}\nbpcg figures: error: 명령은 results, cubesphere 가운데 하나여야 합니다", 2);
        }
        var a = new Args { Command = argv[0] };
        string sub = a.Command;
        string[] ok = Allowed[sub];
        for (int i = 1; i < argv.Length; i++)
        {
            string tok = argv[i];
            if (!tok.StartsWith("--", StringComparison.Ordinal))
            {
                if (sub == "results" && a.Run is null)
                {
                    a.Run = tok;
                    continue;
                }
                throw Error(sub, $"unrecognized arguments: {tok}");
            }
            string? inline = null;
            int eq = tok.IndexOf('=');
            if (eq > 0)
            {
                inline = tok[(eq + 1)..];
                tok = tok[..eq];
            }
            if (!ok.Contains(tok))
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
                case "--out":
                    a.Out = Value();
                    break;
                case "--etopo":
                    a.Etopo = Value();
                    break;
                case "--n":
                    string nv = Value();
                    a.N = int.TryParse(nv, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) && n >= 1
                        ? n
                        : throw Error(sub, $"argument --n: invalid int value: '{nv}'");
                    break;
                default: // --R
                    string rv = Value();
                    a.R = double.TryParse(rv, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0
                        ? r
                        : throw Error(sub, $"argument --R: invalid float value: '{rv}'");
                    break;
            }
        }
        if (sub == "results" && a.Run is null)
        {
            throw Error(sub, "the following arguments are required: run");
        }
        return a;
    }

    /// <summary>argv 는 figures 다음부터. 반환: 종료 코드.</summary>
    public static int Dispatch(string[] argv)
    {
        Args a = Parse(argv);
        if (a.Command == "results")
        {
            string run = a.Run!;
            if (!Directory.Exists(run))
            {
                throw new RunError($"결과 폴더가 없습니다: {run}");
            }
            try
            {
                string outDir = ResultFigures.Render(run, a.Out, a.Etopo, path => Say($"  {path}"));
                Say($"그림을 썼습니다: {outDir}");
            }
            catch (FileNotFoundException e)
            {
                throw new RunError(e.Message);
            }
            return 0;
        }
        string path = a.Out ?? Path.Combine(Paths.Out, $"cubesphere_n{a.N}.png");
        (int nCells, double areaSum) = CubesphereFigure.Render(a.N, a.R, path);
        Say($"생성된 총 셀 개수: {nCells}");
        Say($"전체 면적 합: {PyFormat.Fixed(areaSum, 6)} (이론값 4πR²: {PyFormat.Fixed(4 * Math.PI * a.R * a.R, 6)})");
        Say($"그림을 썼습니다: {path}");
        return 0;
    }

    private static void Say(string msg)
    {
        Console.WriteLine(msg);
        Console.Out.Flush();
    }
}
