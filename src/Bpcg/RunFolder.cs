using System;
using System.Collections.Generic;
using System.IO;
using Bpcg.Bake;
using Bpcg.IO;

namespace Bpcg;

/// <summary>
/// 실행 폴더(한 번의 실행이 planet/, hero/, corridor/, globe/ 를 쓰는 폴더) 안의 단계 폴더가 서로 이어지는지 다룹니다
/// (docs/pipeline.md 11장 '실행 폴더').
/// </summary>
/// <remarks>
/// 아래 단계의 목차는 만들 때 읽은 위 단계 묶음의 표지를 <c>parent</c> 로 적습니다. 표지는 묶음의 종류, 설정 해시, 시드입니다.
/// 히어로 목차는 행성의 표지를, 회랑 목차는 히어로의 표지를, globe.json 은 행성의 표지(<c>parent</c>)와 함께 구운 히어로의 표지
/// (<c>hero_bundle</c>)를 적습니다. 히어로의 표지에는 그 히어로의 <c>parent</c> 도 들어가, 평면 히어로와 행성에서 만든 히어로가
/// 설정이 같아도 서로 다른 표지를 받습니다.
/// </remarks>
public static class RunFolder
{
    /// <summary>회랑 목차(corridor/manifest.json)의 형식 이름 (<see cref="Corridor.BakeCorridor"/> 가 씀).</summary>
    public const string CorridorFormat = "bpcg-corridor";

    /// <summary>
    /// 묶음 목차에서 그 묶음의 표지를 만듭니다: {kind, config_digest, seed}, 히어로는 parent 도.
    /// 히어로 목차에 parent 가 없으면(이 키를 적기 전에 만든 묶음) 어느 행성에서 만들었는지 알 수 없어 null 을 돌려줍니다.
    /// </summary>
    public static OrderedDictionary<string, object?>? BundleId(OrderedDictionary<string, object?> manifest)
    {
        string? kind = manifest.GetValueOrDefault("kind") as string;
        var id = new OrderedDictionary<string, object?>
        {
            ["kind"] = kind,
            ["config_digest"] = manifest.GetValueOrDefault("config_digest"),
            ["seed"] = manifest.GetValueOrDefault("seed"),
        };
        if (kind == "hero")
        {
            if (!manifest.TryGetValue("parent", out object? parent))
            {
                return null;
            }
            id["parent"] = parent;
        }
        return id;
    }

    /// <summary>두 표지가 같은지 (키 순서와 상관없이 정렬한 JSON 글자로 비교). 둘 중 하나라도 표지가 아니면 false.</summary>
    public static bool SameBundle(object? a, object? b) =>
        a is OrderedDictionary<string, object?> x && b is OrderedDictionary<string, object?> y
        && PyJson.DumpsSorted(x) == PyJson.DumpsSorted(y);

    /// <summary>
    /// 표지를 기록 줄과 오류 글에 쓰는 말로 바꿉니다. 예: "시드 0, 설정 해시 1a2b3c4d5e6f",
    /// 히어로면 "시드 0, 설정 해시 …, 행성 [시드 0, 설정 해시 …]에서 만듦" 또는 "…, 행성 없이 만든 평면 히어로".
    /// </summary>
    public static string Describe(object? id)
    {
        if (id is not OrderedDictionary<string, object?> d)
        {
            return "기록 없음";
        }
        string text = $"시드 {d.GetValueOrDefault("seed") ?? "없음"}, 설정 해시 {d.GetValueOrDefault("config_digest") ?? "없음"}";
        if (!d.TryGetValue("parent", out object? parent))
        {
            return text;
        }
        return parent is null ? $"{text}, 행성 없이 만든 평면 히어로" : $"{text}, 행성 [{Describe(parent)}]에서 만듦";
    }

    // 단계 폴더마다, 이 생성기가 쓴 폴더인지 알아보는 목차 파일과 그 안의 형식 이름.
    private static (string File, string Format) IndexOf(string stage) => stage switch
    {
        Runs.PlanetDir or Runs.HeroDir => (Bundle.ManifestFile, Bundle.Format),
        Runs.CorridorDir => (Bundle.ManifestFile, CorridorFormat),
        Runs.GlobeDir => (Globe.GlobeJson, Globe.FormatName),
        _ => throw new ArgumentException($"단계 폴더 이름은 {Runs.PlanetDir}, {Runs.HeroDir}, {Runs.CorridorDir}, {Runs.GlobeDir} 가운데 하나여야 합니다: {stage}"),
    };

    /// <summary>
    /// 단계 폴더 하나의 목차를 읽습니다. 반환: 목차 내용. 목차가 없거나, 읽지 못하거나, 이 생성기의 형식이 아니면 null.
    /// </summary>
    public static OrderedDictionary<string, object?>? ReadIndex(string stageDir, string stage)
    {
        (string file, string format) = IndexOf(stage);
        string path = Path.Combine(stageDir, file);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return PyJson.Loads(File.ReadAllText(path)) is OrderedDictionary<string, object?> m && Equals(m.GetValueOrDefault("format"), format)
                ? m
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 실행 폴더 runDir 의 단계 폴더 가운데 stages 를 지우고, 지운 폴더를 기록 줄 하나로 알립니다. 반환: 지운 단계 이름.
    /// 목차가 이 생성기의 형식인 폴더만 지웁니다. 목차가 없거나 형식이 다른 폴더(쓰다 멈춘 폴더, 사용자가 둔 같은 이름의 폴더)는 그대로 둡니다.
    /// </summary>
    public static List<string> DropStages(string runDir, IEnumerable<string> stages, string why, Action<string> log)
    {
        var dropped = new List<string>();
        foreach (string stage in stages)
        {
            string dir = Path.Combine(runDir, stage);
            if (ReadIndex(dir, stage) is not null)
            {
                Directory.Delete(dir, true);
                dropped.Add(stage);
            }
        }
        if (dropped.Count > 0)
        {
            log($"[실행 폴더] 묵은 단계 폴더 {dropped.Count}개를 지웠습니다: {string.Join(", ", dropped)} ({why}) → {runDir}");
        }
        return dropped;
    }

    /// <summary>
    /// 히어로를 runDir 에 쓰기 전에, 그 폴더에 있는 행성 묶음이 이 히어로를 만들 행성인지 봅니다.
    /// planetId 는 히어로를 만들 행성의 표지이고, 평면 히어로면 null 입니다. 폴더에 행성 묶음이 없거나 같은 행성이면 그냥 돌아갑니다.
    /// 다르면 같은 폴더에 서로 다른 실행의 묶음이 섞이므로, 무엇이 다른지와 고치는 법을 적은 RunError 로 멈춥니다.
    /// </summary>
    public static void RequireHeroFitsRun(string runDir, OrderedDictionary<string, object?>? planetId)
    {
        string planetDir = Path.Combine(runDir, Runs.PlanetDir);
        OrderedDictionary<string, object?>? here = ReadIndex(planetDir, Runs.PlanetDir);
        if (here is null)
        {
            return;
        }
        OrderedDictionary<string, object?>? hereId = BundleId(here);
        if (planetId is not null && SameBundle(hereId, planetId))
        {
            return;
        }
        throw new RunError(planetId is null
            ? $"실행 폴더 {runDir} 에 행성 묶음이 있습니다({Describe(hereId)}). 평면 히어로는 행성 없이 만들므로, 이 폴더에 쓰면 "
                + "행성과 히어로가 서로 다른 실행에서 나온 묶음이 됩니다. 다른 --out 폴더를 주거나, 이 행성이 필요 없으면 "
                + $"{planetDir} 를 지운 뒤 다시 돌리세요"
            : $"실행 폴더 {runDir} 의 행성 묶음({Describe(hereId)})이 히어로를 만들 행성({Describe(planetId)})과 다릅니다. "
                + "이 폴더에 쓰면 행성과 히어로가 서로 다른 실행에서 나온 묶음이 됩니다. --out 을 빼서 히어로를 그 행성 옆에 쓰거나, "
                + "행성 묶음이 없는 다른 --out 폴더를 주세요");
    }

    /// <summary>
    /// 히어로 묶음이 이 행성 묶음에서 만든 묶음인지 봅니다. 반환: 맞으면 null, 다르면 무엇이 다른지 적은 글.
    /// 히어로 목차에 parent 가 없으면(이 키를 적기 전에 만든 묶음) 시드만 비교합니다.
    /// </summary>
    public static string? HeroPlanetMismatch(OrderedDictionary<string, object?> planet, OrderedDictionary<string, object?> hero)
    {
        OrderedDictionary<string, object?>? planetId = BundleId(planet);
        if (!hero.TryGetValue("parent", out object? parent))
        {
            object? ps = planet.GetValueOrDefault("seed");
            object? hs = hero.GetValueOrDefault("seed");
            return Equals(ps, hs) ? null : $"시드가 다릅니다(행성 {ps ?? "없음"}, 히어로 {hs ?? "없음"})";
        }
        if (parent is null)
        {
            return "히어로는 행성 없이 만든 평면 히어로입니다";
        }
        return SameBundle(parent, planetId) ? null : $"히어로는 다른 행성({Describe(parent)})에서 만들었습니다";
    }

    /// <summary>
    /// 이번 굽기가 지구본을 굽지 않을 때(--no-globe, 행성 없음) 쓰는 정리: globeDir 의 지난 지구본이 이번 히어로(heroId)와 함께
    /// 구운 지구본이 아니면 지웁니다. 지난 지구본에 함께 구운 히어로의 기록(hero_bundle)이 없으면(그 기록을 적기 전에 구운 지구본)
    /// 맞는지 알 수 없어 지웁니다. 남겨 두면 지구본 장면이 지금 회랑과 상관없는 행성이나 다른 히어로 자리를 보여 줍니다. 반환: 지웠는지.
    /// </summary>
    public static bool DropGlobeUnlessFor(string globeDir, OrderedDictionary<string, object?>? heroId, Action<string> log)
    {
        OrderedDictionary<string, object?>? globe = ReadIndex(globeDir, Runs.GlobeDir);
        if (globe is null)
        {
            return false;
        }
        object? baked = globe.GetValueOrDefault("hero_bundle");
        if (heroId is not null && SameBundle(baked, heroId))
        {
            return false;
        }
        Directory.Delete(globeDir, true);
        log($"[지구본] 이번 히어로와 맞지 않는 지난 지구본을 지웠습니다: 함께 구운 히어로({Describe(baked)}), "
            + $"이번 히어로({Describe(heroId)}) → {globeDir}");
        return true;
    }
}
