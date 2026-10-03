using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 엔진이 쓰는 폴더: 설정 폴더(configs/)를 찾아 <see cref="Paths"/> 에 알려 주고, 생성 결과를 user://runs 아래에 둡니다.
/// </summary>
/// <remarks>
/// 설정 폴더는 환경 변수 BPCG_CONFIGS → 프로젝트 폴더에서 위로 올라가며 처음 만나는 configs/planets 가 있는 폴더 순서로 찾습니다.
/// TODO(port): 내보낸 게임에는 저장소가 없으므로 configs 를 게임에 넣어 user:// 로 풀어 쓰는 방법을 정해야 합니다.
/// </remarks>
public static class EnginePaths
{
    /// <summary>생성 결과를 두는 곳 (실행마다 &lt;날짜-시각&gt;-&lt;프로필&gt;-s&lt;시드&gt; 폴더).</summary>
    public const string RunsDir = "user://runs";

    /// <summary>찾은 설정 폴더 (못 찾으면 null).</summary>
    public static string? ConfigsDir { get; private set; }

    /// <summary>설정 폴더를 찾지 못한 까닭 (찾았으면 빈 글).</summary>
    public static string Error { get; private set; } = "";

    private static bool Configured;

    /// <summary>설정 폴더를 찾아 Bpcg 라이브러리의 Paths 에 넣습니다. 찾았으면 true. 여러 번 불러도 됩니다.</summary>
    public static bool Configure()
    {
        if (Configured)
        {
            return ConfigsDir is not null;
        }
        Configured = true;
        string? env = System.Environment.GetEnvironmentVariable("BPCG_CONFIGS");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "planets")))
        {
            ConfigsDir = Path.GetFullPath(env);
        }
        else
        {
            var dir = new DirectoryInfo(ProjectSettings.GlobalizePath("res://"));
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "configs", "planets")))
            {
                dir = dir.Parent;
            }
            if (dir is not null)
            {
                ConfigsDir = Path.Combine(dir.FullName, "configs");
            }
        }
        if (ConfigsDir is null)
        {
            Error = $"설정 폴더(configs/planets)를 찾지 못했습니다: {ProjectSettings.GlobalizePath("res://")} 와 그 위 폴더에 없습니다. "
                + "환경 변수 BPCG_CONFIGS 로 알려 주세요";
            return false;
        }
        string root = Path.GetDirectoryName(ConfigsDir) ?? ConfigsDir;
        Paths.Configure(root: root, configs: ConfigsDir);
        return true;
    }

    /// <summary>생성 결과 폴더의 운영체제 경로.</summary>
    public static string RunsRoot() => ProjectSettings.GlobalizePath(RunsDir);

    /// <summary>configs/&lt;sub&gt;/*.toml 의 이름 (확장자 뺀 것, 가나다순).</summary>
    public static List<string> ConfigNames(string sub)
    {
        if (ConfigsDir is null || !Directory.Exists(Path.Combine(ConfigsDir, sub)))
        {
            return [];
        }
        return Directory.GetFiles(Path.Combine(ConfigsDir, sub), "*.toml")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>설정 파일 첫 줄의 설명 주석 ("# …"). 없으면 빈 글.</summary>
    public static string ConfigNote(string sub, string name)
    {
        if (ConfigsDir is null)
        {
            return "";
        }
        string path = Path.Combine(ConfigsDir, sub, name + ".toml");
        if (!File.Exists(path))
        {
            return "";
        }
        string first = File.ReadLines(path).FirstOrDefault() ?? "";
        return first.StartsWith('#') ? first.TrimStart('#').Trim() : "";
    }

    /// <summary>지난 실행 하나 (회랑 manifest 가 있는 실행 폴더).</summary>
    public sealed record RunEntry(string Name, string Dir, string Profile, string Seed, bool HasGlobe, DateTime Time)
    {
        public string CorridorDir => Path.Combine(Dir, Runs.CorridorDir);
    }

    /// <summary>user://runs 아래의 지난 실행 (새것 먼저).</summary>
    public static List<RunEntry> ListRuns()
    {
        string root = RunsRoot();
        var runs = new List<RunEntry>();
        if (!Directory.Exists(root))
        {
            return runs;
        }
        foreach (string dir in Directory.GetDirectories(root))
        {
            string man = Path.Combine(dir, Runs.CorridorDir, "manifest.json");
            if (!File.Exists(man))
            {
                continue;
            }
            OrderedDictionary<string, object?>? m = Json.ReadObject(man);
            runs.Add(new RunEntry(
                Path.GetFileName(dir), dir, Json.Str(m, "profile", "?"), Json.Str(m, "seed", "?"),
                File.Exists(Path.Combine(dir, Runs.GlobeDir, "globe.json")), File.GetLastWriteTime(man)));
        }
        return runs.OrderByDescending(r => r.Time).ToList();
    }
}
