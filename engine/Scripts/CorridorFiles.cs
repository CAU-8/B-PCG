using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 회랑을 불러올 때 읽을 경로와 회랑 지형 설정. 주 스레드에서 만들어 다른 스레드로 넘깁니다.
/// </summary>
/// <remarks>
/// <see cref="BakedPaths"/> 는 명령줄과 처음 화면의 선택을 보므로 주 스레드에서만 읽습니다.
/// </remarks>
public sealed record CorridorRequest(string Dir, string BaseStem, string DetailStem, string FallbackStem, float SkirtM, Rect2 Hole)
{
    /// <summary>지금 굽기 폴더와 회랑 지형 노드의 설정으로 만듭니다 (주 스레드).</summary>
    public static CorridorRequest For(HeightmapTerrain terrain) => new(
        BakedPaths.Dir(), BakedPaths.Resolve(terrain.HeightmapStem), BakedPaths.Resolve(BakedLayers.DetailStem),
        terrain.FallbackStem, terrain.SkirtM, terrain.CollisionHole);
}

/// <summary>다른 스레드가 회랑 파일을 읽는 동안 지금 몇 번째 단계(0부터, <see cref="CorridorFiles.Stages"/>)인지.</summary>
public sealed class LoadProgress
{
    private int _stage;

    public int Stage => Volatile.Read(ref _stage);

    public void Enter(int stage) => Volatile.Write(ref _stage, stage);
}

/// <summary>
/// 회랑 묶음(manifest.json 이 있는 폴더)의 파일을 주 스레드 밖에서 모두 읽어 둔 결과.
/// </summary>
/// <remarks>
/// <see cref="Read"/> 는 Godot 노드와 그래픽 카드 자원을 만들지 않습니다. 파일을 읽고, 지형 메시 배열과 충돌 조각을
/// 계산하고, 동굴 메시(caves.glb)를 GltfDocument 로 해석해 둡니다. 노드는 <see cref="BakedLayers.Build"/> 가
/// 주 스레드에서 한 단계씩 만듭니다.
/// 동굴 메시는 편집기가 가져온(import) 사본을 쓰지 않고 늘 파일에서 읽습니다. 다시 구운 뒤 가져오기를 돌리지 않으면
/// 가져온 사본이 옛 동굴일 수 있기 때문입니다.
/// 파일이 없거나 읽지 못해도 멈추지 않고 <see cref="Problems"/> 에 한 줄을 더합니다. 회랑 화면의 '불러오기 문제' 상자가
/// 이 줄을 보여 줍니다. 목차(manifest.json)의 files 에 있는데 없는 파일도 문제로 셉니다.
/// </remarks>
public sealed class CorridorFiles
{
    /// <summary>읽는 단계의 화면 이름. <see cref="Read"/> 가 이 순서로 지나갑니다.</summary>
    public static readonly string[] Stages =
    [
        "목차 파일(manifest.json) 읽는 중",
        "회랑 지형(걷는 땅) 읽고 메시 계산하는 중",
        "땅속 암석 부피(strata.u8) 읽는 중",
        "주변 지형(유역 전체) 읽고 메시 계산하는 중",
        "호수·강 수면과 지하수면 읽는 중",
        "동굴 입구 구멍 자리 읽는 중",
        "동굴 메시(caves.glb) 읽는 중",
        "동굴 입구 목록(entrances.json) 읽는 중",
    ];

    public const string CorridorFormat = "bpcg-corridor";

    private HashSet<string> _listed = [];

    private CorridorFiles(string dir) => Dir = dir;

    /// <summary>읽은 굽기 폴더 (끝에 / 없음).</summary>
    public string Dir { get; }

    /// <summary>회랑 목차. 없거나 읽지 못하면 null 이고, 그때는 회랑 지형(또는 표본)만 만듭니다.</summary>
    public OrderedDictionary<string, object?>? Manifest { get; private set; }

    /// <summary>엔진 Y + YOffsetM = 해발 고도 (m).</summary>
    public float YOffsetM { get; private set; }

    /// <summary>회랑 사각형 (X, Z).</summary>
    public Rect2 CorridorRect { get; private set; }

    /// <summary>회랑 지형: 프랙탈 디테일, 기본 높이맵, 또는 표본. 아무것도 읽지 못하면 IsValid 가 false.</summary>
    public PreparedTerrain? Terrain { get; private set; }

    /// <summary>회랑 지형이 구운 높이맵(기본 또는 디테일)인지. 표본이면 false.</summary>
    public bool TerrainIsBaked { get; private set; }

    /// <summary>회랑 지형이 프랙탈 디테일 높이맵인지.</summary>
    public bool UsesDetail { get; private set; }

    /// <summary>디테일을 쓸 때 기본 높이맵 (메시 없이 높이만). 지층 깊이 보정과 디테일 끄기에 씁니다.</summary>
    public HeightmapLoader? BaseHeightmap { get; private set; }

    /// <summary>재질 부피 (텍스처는 아직 없음, 주 스레드에서 CreateTextures).</summary>
    public StrataVolume? Strata { get; private set; }

    public PreparedTerrain? Surround { get; private set; }

    /// <summary>호수·강 수면 높이맵과 그 메시 배열.</summary>
    public HeightmapLoader? Water { get; private set; }

    public MeshArrays? WaterMesh { get; private set; }

    public HeightmapLoader? WaterTable { get; private set; }

    public HeightmapLoader? WaterTableLower { get; private set; }

    /// <summary>동굴 입구 구멍 파일 이름 → 높이맵.</summary>
    public Dictionary<string, HeightmapLoader> Mouths { get; } = [];

    /// <summary>해석해 둔 동굴 메시. 장면은 같은 문서로 주 스레드에서 만듭니다 (GenerateScene).</summary>
    public GltfDocument? CaveDocument { get; private set; }

    public GltfState? CaveState { get; private set; }

    public string CavePath { get; private set; } = "";

    public List<Vector3> EntrancesInside { get; } = [];

    public List<Vector3> EntrancesOutside { get; } = [];

    /// <summary>화면에 띄울 불러오기 문제 (한 줄에 하나: 무엇이 틀렸는지, 받은 값, 고치는 법).</summary>
    public List<string> Problems { get; } = [];

    /// <summary>단계별로 걸린 시간 (ms).</summary>
    public OrderedDictionary<string, long> ReadMs { get; } = new();

    /// <summary>
    /// 회랑 파일을 모두 읽습니다. 주 스레드 밖에서 부릅니다. 예외가 나도 던지지 않고 문제 한 줄로 남깁니다
    /// (그때까지 읽은 결과는 그대로 씁니다).
    /// </summary>
    public static CorridorFiles Read(CorridorRequest request, LoadProgress progress)
    {
        var files = new CorridorFiles(request.Dir);
        try
        {
            files.ReadAll(request, progress);
        }
        catch (Exception e)
        {
            files.Problems.Add($"회랑을 불러오다 예외가 나서 멈췄습니다 ({Stages[progress.Stage]}): {e.GetType().Name}: {e.Message}. "
                + "콘솔 기록에 전체 내용이 있습니다");
            GD.Print($"회랑 불러오기 예외: {e}");
        }
        return files;
    }

    /// <summary>읽기가 예외로 끝났을 때 쓰는 빈 결과 (문제 한 줄만 있음). 회랑 지형도 없습니다.</summary>
    public static CorridorFiles Failed(string dir, Exception? e)
    {
        var files = new CorridorFiles(dir);
        files.Problems.Add($"회랑 파일을 읽지 못했습니다: {e?.GetType().Name}: {e?.Message}. 콘솔 기록에 전체 내용이 있습니다");
        GD.Print($"회랑 읽기 예외: {e}");
        return files;
    }

    /// <summary>다시 굽는 명령 (문제 글 끝에 붙임).</summary>
    public string RebakeHint()
    {
        if (Dir.StartsWith("res://", StringComparison.Ordinal))
        {
            return "다시 구우세요: uv run bpcg bake --engine --hero <실행 폴더>/hero";
        }
        string dir = Dir.TrimEnd('/');
        string run = Path.GetFileName(dir) == "corridor" ? Path.GetDirectoryName(dir) ?? "<실행 폴더>" : "<실행 폴더>";
        return $"다시 구우세요: uv run bpcg bake --hero {run}/hero";
    }

    private void ReadAll(CorridorRequest request, LoadProgress progress)
    {
        long t = Stopwatch.GetTimestamp();
        progress.Enter(0);
        string? manifestProblem = ReadManifest();
        Lap("manifest", ref t);

        progress.Enter(1);
        ReadTerrain(request, manifestProblem is null);
        Lap("terrain", ref t);
        if (manifestProblem is not null)
        {
            string without = TerrainIsBaked ? "지형만 보여 줍니다 (물, 동굴, 지층 단면 없음)" : "작은 표본 지형만 보여 줍니다";
            Problems.Insert(0, manifestProblem.Replace("{without}", without, StringComparison.Ordinal));
            return;
        }

        progress.Enter(2);
        StrataVolume strata = StrataVolume.ReadDir(Dir);
        if (strata.IsValid())
        {
            Strata = strata;
        }
        else
        {
            Problem($"땅속 암석 부피를 읽지 못해 지질도 색(G)과 지층 단면(X) 없이 엽니다: {strata.ErrorMessage}. {RebakeHint()}");
        }
        Lap("strata", ref t);

        progress.Enter(3);
        if (ReadOptional("surround25", "주변 지형") is HeightmapLoader surround)
        {
            Surround = HeightmapTerrain.Prepare(surround, 0.0f, CorridorRect);
        }
        Lap("surround", ref t);

        progress.Enter(4);
        Water = ReadOptional("water", "호수·강 수면");
        if (Water is not null)
        {
            WaterMesh = BakedLayers.WetQuadArrays(Water);
        }
        WaterTable = ReadOptional("water_table", "지하수면");
        WaterTableLower = ReadOptional("water_table_lower", "아래 지하수면");
        Lap("water", ref t);

        progress.Enter(5);
        foreach (string name in new[] { BakedLayers.CaveMouthName, BakedLayers.CaveMouthDetailName })
        {
            if (ReadOptional(name, "동굴 입구 구멍 자리") is HeightmapLoader mouth)
            {
                Mouths[name] = mouth;
            }
        }
        Lap("cave_mouth", ref t);

        progress.Enter(6);
        ReadCaves();
        Lap("caves", ref t);

        progress.Enter(7);
        ReadEntrances();
        Lap("entrances", ref t);
    }

    /// <summary>목차를 읽습니다. 쓸 수 있으면 null, 아니면 문제 글 ({without} 자리에 '무엇만 보여 주는지'를 넣음).</summary>
    private string? ReadManifest()
    {
        string path = Dir.PathJoin("manifest.json");
        if (!Files.Exists(path))
        {
            return $"구운 회랑의 목차 파일이 없어 {{without}} (찾은 곳: {path}). "
                + "N으로 처음 화면에 가서 행성을 만들거나, --baked-dir 에 실행 폴더 안의 corridor 폴더를 절대 경로로 주세요";
        }
        OrderedDictionary<string, object?>? parsed = Json.ReadObject(path);
        if (parsed is null)
        {
            return $"회랑 목차 파일을 JSON 객체로 읽지 못해 {{without}}: {path}. {RebakeHint()}";
        }
        string format = Json.S(Json.Get(parsed, "format"));
        if (format != CorridorFormat)
        {
            return $"회랑 목차 파일의 format 이 {CorridorFormat} 가 아니라서 {{without}} (받은 값: {format}): {path}. "
                + "실행 폴더 안의 corridor 폴더를 골랐는지 확인하세요";
        }
        try
        {
            YOffsetM = Json.F(Json.Obj(parsed["frame"])!["y_offset_m"]);
            OrderedDictionary<string, object?> re = Json.Obj(Json.Obj(parsed["corridor"])!["rect_engine_m"])!;
            float xMin = Json.F(re["x_min"]);
            float zMin = Json.F(re["z_min"]);
            CorridorRect = new Rect2(xMin, zMin, Json.F(re["x_max"]) - xMin, Json.F(re["z_max"]) - zMin);
        }
        catch (Exception e) when (e is KeyNotFoundException or NullReferenceException or InvalidCastException or FormatException)
        {
            return $"회랑 목차 파일에 frame.y_offset_m 이나 corridor.rect_engine_m 이 없어 {{without}} (예전 굽기?): {path}. {RebakeHint()}";
        }
        Manifest = parsed;
        _listed = (Json.Arr(Json.Get(parsed, "files")) ?? []).Select(Json.S).ToHashSet();
        return null;
    }

    /// <summary>
    /// 회랑 지형을 하나만 고릅니다: 목차가 있고 프랙탈 디테일 높이맵이 있으면 디테일, 아니면 기본 높이맵,
    /// 기본 높이맵이 없으면 표본. 디테일을 쓰면 기본 높이맵은 높이만 읽고 메시는 만들지 않습니다.
    /// </summary>
    private void ReadTerrain(CorridorRequest request, bool baked)
    {
        bool hasBase = HeightmapLoader.Exists(request.BaseStem);
        if (baked && hasBase && HeightmapLoader.Exists(request.DetailStem))
        {
            PreparedTerrain detail = HeightmapTerrain.Prepare(request.DetailStem, request.SkirtM, request.Hole);
            if (detail.IsValid)
            {
                Terrain = detail;
                TerrainIsBaked = true;
                UsesDetail = true;
                BaseHeightmap = HeightmapLoader.LoadStem(request.BaseStem);
                if (!BaseHeightmap.IsValid())
                {
                    Problem($"기본 회랑 지형을 읽지 못해 프랙탈 디테일을 끌 수 없습니다: {BaseHeightmap.ErrorMessage}. {RebakeHint()}");
                }
                return;
            }
            Problem($"프랙탈 디테일 지형을 읽지 못해 기본 지형으로 엽니다: {detail.Heightmap.ErrorMessage}. {RebakeHint()}");
        }
        if (hasBase)
        {
            Terrain = HeightmapTerrain.Prepare(request.BaseStem, request.SkirtM, request.Hole);
            TerrainIsBaked = Terrain.IsValid;
            if (!Terrain.IsValid)
            {
                Problem($"회랑 지형을 읽지 못해 걷는 땅이 없습니다: {Terrain.Heightmap.ErrorMessage}. {RebakeHint()}");
            }
            return;
        }
        if (baked)
        {
            Problem($"회랑 지형 파일이 없어 작은 표본 지형을 대신 보여 줍니다: {request.BaseStem}.json·.bin. {RebakeHint()}");
        }
        if (request.FallbackStem.Length > 0)
        {
            Terrain = HeightmapTerrain.Prepare(request.FallbackStem, request.SkirtM, request.Hole);
            if (!Terrain.IsValid)
            {
                Problem($"표본 지형도 읽지 못했습니다: {Terrain.Heightmap.ErrorMessage}. engine/samples 폴더가 있는지 확인하세요");
            }
        }
    }

    /// <summary>
    /// 있으면 읽는 높이맵 레이어. 파일이 없으면 null 이고, 목차에 있는데 없거나 읽지 못하면 문제 한 줄을 남깁니다.
    /// </summary>
    private HeightmapLoader? ReadOptional(string name, string label)
    {
        string stem = Dir.PathJoin(name);
        if (!HeightmapLoader.Exists(stem))
        {
            if (Listed(name + ".json") || Listed(name + ".bin"))
            {
                Problem($"{label} 파일이 없어 이 레이어 없이 엽니다: {stem}.json·.bin (목차 manifest.json 에는 있음). {RebakeHint()}");
            }
            return null;
        }
        HeightmapLoader hm = HeightmapLoader.LoadStem(stem);
        if (!hm.IsValid())
        {
            Problem($"{label} 파일을 읽지 못해 이 레이어 없이 엽니다: {hm.ErrorMessage}. {RebakeHint()}");
            return null;
        }
        return hm;
    }

    private void ReadCaves()
    {
        CavePath = Dir.PathJoin("caves.glb");
        if (!Files.Exists(CavePath))
        {
            if (Listed("caves.glb"))
            {
                Problem($"동굴 메시 파일이 없어 동굴 레이어 없이 엽니다: {CavePath} (목차 manifest.json 에는 있음). {RebakeHint()}");
            }
            return;
        }
        var document = new GltfDocument();
        var state = new GltfState();
        Error err = document.AppendFromFile(CavePath, state);
        if (err != Error.Ok)
        {
            Problem($"동굴 메시를 읽지 못해 동굴 레이어 없이 엽니다: {CavePath} (Godot 오류 {err}). {RebakeHint()}");
            return;
        }
        CaveDocument = document;
        CaveState = state;
    }

    private void ReadEntrances()
    {
        string path = Dir.PathJoin("entrances.json");
        if (!Files.Exists(path))
        {
            if (Listed("entrances.json"))
            {
                Problem($"동굴 입구 목록 파일이 없어 T 키로 입구에 갈 수 없습니다: {path} (목차 manifest.json 에는 있음). {RebakeHint()}");
            }
            return;
        }
        OrderedDictionary<string, object?>? parsed = Json.ReadObject(path);
        List<object?>? inside = Json.Arr(Json.Get(parsed, "inside") ?? new List<object?>());
        List<object?>? outside = Json.Arr(Json.Get(parsed, "outside") ?? new List<object?>());
        if (parsed is null || inside is null || outside is null)
        {
            Problem($"동굴 입구 목록의 inside·outside 를 배열로 읽지 못해 T 키로 입구에 갈 수 없습니다 (예전 굽기?): {path}. {RebakeHint()}");
            return;
        }
        try
        {
            for (int i = 0; i < Math.Min(inside.Count, outside.Count); i++)
            {
                EntrancesInside.Add(Vec3(inside[i]));
                EntrancesOutside.Add(Vec3(outside[i]));
            }
        }
        catch (Exception e) when (e is ArgumentException or NullReferenceException or InvalidCastException or FormatException)
        {
            EntrancesInside.Clear();
            EntrancesOutside.Clear();
            Problem($"동굴 입구 좌표가 [x, y, z] 숫자 세 개가 아니라서 T 키로 입구에 갈 수 없습니다: {path}. {RebakeHint()}");
        }
    }

    private static Vector3 Vec3(object? v)
    {
        List<object?> a = Json.Arr(v)!;
        return new Vector3(Json.F(a[0]), Json.F(a[1]), Json.F(a[2]));
    }

    private bool Listed(string fileName) => _listed.Contains(fileName);

    private void Problem(string text) => Problems.Add(text);

    private void Lap(string key, ref long start)
    {
        long now = Stopwatch.GetTimestamp();
        ReadMs[key] = (long)Stopwatch.GetElapsedTime(start, now).TotalMilliseconds;
        start = now;
    }
}
