using System;

namespace Bpcg.Cli;

/// <summary>
/// 명령줄 실행 bpcg (src/bpcg/cli.py 의 planet·hero·bake·all). 단계 2 끝(cli)에 옮깁니다.
/// 지금은 판만 알려 줍니다.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine($"bpcg {Package.Version} (C#). planet·hero·bake·all 명령은 아직 옮기지 않았습니다.");
        return args.Length == 0 ? 0 : 2;
    }
}
