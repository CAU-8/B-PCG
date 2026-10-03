using System;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 구운 파일 폴더(회랑 폴더)를 정합니다.
/// </summary>
/// <remarks>
/// 순서는 명령줄 끝의 <c>-- --baked-dir=&lt;폴더&gt;</c>(검사·직접 실행) → 이번 실행에서 생성 화면이 고른 폴더
/// (<see cref="RuntimeDir"/>, 장면을 바꿔도 남음) → <c>res://baked</c> 입니다. 절대 경로도 됩니다.
/// 지구본은 <c>&lt;폴더&gt;/globe</c>, 없으면 실행 폴더의 <c>&lt;폴더&gt;/../globe</c> 에서 찾습니다(<see cref="GlobeData.DefaultDir"/>).
/// </remarks>
public static class BakedPaths
{
    public const string DefaultDir = "res://baked";
    public const string ArgPrefix = "--baked-dir=";

    /// <summary>생성 화면이 굽기를 마치거나 지난 결과를 고르면 넣는 회랑 폴더 (끝에 / 없음). 저장하지 않습니다.</summary>
    public static string? RuntimeDir { get; set; }

    /// <summary>명령줄로 받은 굽기 폴더 (없으면 null).</summary>
    public static string? CommandLineDir()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(ArgPrefix, StringComparison.Ordinal))
            {
                return arg[ArgPrefix.Length..].TrimEnd('/');
            }
        }
        return null;
    }

    /// <summary>구운 파일 폴더 (끝에 / 없음).</summary>
    public static string Dir()
    {
        if (CommandLineDir() is string cmd)
        {
            return cmd;
        }
        if (!string.IsNullOrEmpty(RuntimeDir))
        {
            return RuntimeDir.TrimEnd('/');
        }
        return DefaultDir;
    }

    /// <summary>res://baked/ 로 시작하는 경로를 Dir() 아래 경로로 바꿉니다. 다른 경로는 그대로 둡니다.</summary>
    public static string Resolve(string path)
    {
        string prefix = DefaultDir + "/";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Dir().PathJoin(path[prefix.Length..]);
        }
        return path;
    }
}
