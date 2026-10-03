using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Bpcg.Core;

/// <summary>
/// 저장소 안의 경로 (src/bpcg/core/paths.py). 코드에 절대 경로를 쓰지 않고 여기서만 찾습니다.
/// </summary>
/// <remarks>
/// 저장소 맨 위는 <c>pyproject.toml</c> 과 <c>src/bpcg</c> 가 함께 있는 첫 부모 폴더입니다(Python 과 같은 기준).
/// 찾는 순서는 <see cref="Configure"/> 로 준 값 → 이 소스 파일의 경로 → 실행 파일 폴더 → 현재 폴더입니다.
/// 단계 4(Godot 안, 내보낸 게임)에서는 <see cref="Configure"/> 로 직접 정합니다.
/// 환경 변수 BPCG_DATA·BPCG_OUT 은 처음 읽을 때 한 번 읽고, 빈 문자열은 "." 입니다.
/// </remarks>
public static class Paths
{
    private static string? CachedRoot;
    private static string? CachedConfigs;
    private static string? CachedOut;
    private static string? CachedData;

    /// <summary>저장소 맨 위 (ROOT).</summary>
    public static string Root => CachedRoot ??= FindRoot();

    /// <summary>데이터 폴더 (DATA, 기본 &lt;저장소&gt;/data, BPCG_DATA 로 바꿈).</summary>
    public static string Data => CachedData ??= EnvOr("BPCG_DATA", Path.Combine(Root, "data"));

    /// <summary>생성 결과 폴더 (OUT, 기본 &lt;저장소&gt;/out, BPCG_OUT 으로 바꿈).</summary>
    public static string Out => CachedOut ??= EnvOr("BPCG_OUT", Path.Combine(Root, "out"));

    /// <summary>설정 폴더 (CONFIGS = &lt;저장소&gt;/configs).</summary>
    public static string Configs => CachedConfigs ??= Path.Combine(Root, "configs");

    /// <summary>스크립트로 다시 받을 수 있는 자료 (PILOT).</summary>
    public static string Pilot => Path.Combine(Data, "pilot");

    /// <summary>계정이 필요하거나 손으로 받은 자료 (EXTERNAL).</summary>
    public static string External => Path.Combine(Data, "external");

    /// <summary>분석 스크립트가 만든 중간 결과 (DERIVED).</summary>
    public static string Derived => Path.Combine(Data, "derived");

    /// <summary>지워도 되는 임시 파일 (CACHE).</summary>
    public static string Cache => Path.Combine(Data, "cache");

    /// <summary>경로를 직접 정합니다 (Godot 안·시험). null 인 값은 기본 규칙을 따릅니다.</summary>
    public static void Configure(string? root = null, string? configs = null, string? @out = null, string? data = null)
    {
        CachedRoot = root;
        CachedConfigs = configs;
        CachedOut = @out;
        CachedData = data;
    }

    private static string EnvOr(string name, string fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (v is null)
        {
            return fallback;
        }
        return v.Length == 0 ? "." : v; // Python Path("") 는 "."
    }

    private static string FindRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (string? start in new[] { Path.GetDirectoryName(sourcePath), AppContext.BaseDirectory })
        {
            string? found = SearchUp(start);
            if (found is not null)
            {
                return found;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? SearchUp(string? start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "bpcg")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
