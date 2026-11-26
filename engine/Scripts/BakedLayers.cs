using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 구운 회랑 묶음(manifest.json)의 레이어를 불러오고, 보이기·숨기기를 맡습니다.
/// </summary>
/// <remarks>
/// 파일은 <see cref="CorridorFiles.Read"/> 가 주 스레드 밖에서 먼저 읽고, <see cref="Build"/> 가 주 스레드에서 한 단계씩
/// 노드를 만듭니다 (단계 사이에 프레임을 그려 불러오기 화면이 움직이게). 회랑 지표는 Terrain 노드에 넣고, 여기서는
/// 나머지를 자식으로 만듭니다.
/// Surround: 히어로 전체 25 m 지형 (surround25). 회랑 자리는 비워 고운 지형에 맡깁니다.
/// Water: 호수·강 수면 (water). WaterTable: 지하수면 (water_table), 처음에는 숨기고 처음 켤 때 메시를 만듭니다.
/// Caves: 동굴 메시 (caves.glb) 와 걷기용 충돌체 (<see cref="CaveCollision"/>). Section: 단면 판, 재질 부피(strata.u8)를
/// 3D 텍스처로 읽어 자르는 면에 칠합니다.
/// 레이어 이름은 <see cref="Layers"/> 의 키입니다. "entrance_holes" 는 지형에 동굴 입구 구멍을 뚫는 선택입니다
/// (cave_mouth: 지표 점의 동굴 거리, 음수인 곳을 그리지 않고 충돌면에도 구멍을 냄). "fractal_detail" 은 회랑 지형을 프랙탈 디테일
/// 높이맵(heightmap_detail)으로 바꾸는 선택입니다. 보기용 지표이고 솔버 결과가 아닙니다. 파일이 있으면 처음부터 디테일
/// 지형 하나만 만들고, 기본 지형은 다 불러온 뒤 다른 스레드에서 미리 준비해 두었다가 처음 끌 때 만듭니다.
/// </remarks>
public partial class BakedLayers : Node3D
{
    /// <summary>레이어가 바뀌면 (보이기, 단면) 알립니다.</summary>
    [Signal]
    public delegate void LayersChangedEventHandler();

    /// <summary>회랑 지형의 높이맵을 바꿨을 때 (프랙탈 디테일) 알립니다. Main 이 걷는 플레이어를 올립니다.</summary>
    [Signal]
    public delegate void TerrainRebuiltEventHandler();

    public const string TerrainShaderPath = "res://shaders/cross_section.gdshader";
    public const string SectionShaderPath = "res://shaders/strata_section.gdshader";
    public const string CaveShaderPath = "res://shaders/cave.gdshader";
    public const string WaterShaderPath = "res://shaders/water.gdshader";

    /// <summary>레이어 순서와 화면 이름. Solo 가 false 면 '만 보기' 대상이 아닌 선택입니다.</summary>
    public sealed record LayerInfo(string Id, string Label, bool Solo, string Hint = "");

    public static readonly LayerInfo[] Layers =
    [
        new("terrain", "회랑 지형 (걷는 땅)", true,
            "걸어 다니는 땅입니다. 자연색은 땅속 상자(4 × 4 × 2 m) 가운데 맨 위 상자의 색입니다\n"
            + "(흙은 풀색, 강이 쌓은 모래·자갈은 모래색, 드러난 암석은 그 암석 색).\n"
            + "높이 간격은 프로필의 corridor.voxel_m 입니다 (laptop 2 m, tiny 8 m)."),
        new("surround", "주변 지형 (유역 전체)", true,
            "히어로 유역 전체의 땅입니다. 회랑 자리는 비워 두어 더 고운 회랑 지형이 보입니다.\n"
            + "높이 간격은 프로필의 hero.spacing_m 입니다 (laptop 25 m, tiny 100 m)."),
        new("water", "호수·강 수면", true, "호수와 강의 물 높이를 반투명한 면으로 그립니다."),
        new("water_table", "지하수면", true,
            "땅속에서 물이 차 있는 높이(지하수면)를 반투명 하늘색 면으로 그립니다. 처음 켤 때 만듭니다."),
        new("caves", "동굴", true,
            "동굴 벽입니다. 벽의 앞면이 동굴 안쪽을 봐서, 땅속이나 단면에서 보면 동굴 속이 들여다보입니다.\n"
            + "벽 색은 벽 뒤 암석의 색입니다."),
        new("section", "지층 단면", true,
            "단면 칼(X)이 자른 면에 땅속 암석을 칠합니다.\n"
            + "물에 찬 땅은 파랗고, 지하수면은 하늘색 선입니다. 셰일 같은 막는 층 위에 물이 얹혀 있으면\n"
            + "선이 두 개입니다(위: 얹힌 물, 아래: 녹는 암석 안의 물). 단면 부피(약 300 m)보다 깊은 지하수면은 보이지 않습니다.\n"
            + "깊이 10 m마다 가는 선, 50 m마다 굵은 선을 긋습니다."),
        new("entrance_holes", "동굴 입구 구멍", false,
            "땅 표면이 동굴의 빈 곳과 겹치는 자리에 구멍을 뚫어 동굴 입구를 보여 줍니다."),
        new("fractal_detail", "프랙탈 디테일", false,
            "히어로 유역 지도는 칸이 커서 50 m보다 짧은 잔 굴곡을 그리지 못합니다.\n"
            + "그 잔 굴곡을 굽기에서 이어 붙인 지표입니다. 보기용 꾸밈이고 솔버(산과 강 모양을 푸는 계산)의 결과가 아닙니다.\n"
            + "끄면 원래 회랑 지표로 돌아갑니다."),
    ];

    private static readonly Dictionary<string, LayerInfo> LayerById = Layers.ToDictionary(l => l.Id);

    /// <summary>프랙탈 디테일을 더한 회랑 지표 높이맵과 그 지표의 동굴 입구 파일 (BakedPaths.Resolve 로 바뀜).</summary>
    public const string DetailStem = "res://baked/heightmap_detail";
    public const string CaveMouthName = "cave_mouth";
    public const string CaveMouthDetailName = "cave_mouth_detail";

    /// <summary>굽기의 '물 없음' 값은 -10000 m 입니다. 이보다 낮으면 물이 없는 표본입니다.</summary>
    public const float WaterNoneBelowM = -9999.0f;

    public static readonly Color WaterColor = new(0.10f, 0.30f, 0.42f, 0.78f);
    public static readonly Color WaterTableColor = new(0.20f, 0.75f, 0.95f, 0.35f);

    /// <summary>단면 판의 위아래 여유 (m).</summary>
    public const float SectionMarginM = 20.0f;

    /// <summary>자르는 면을 끌 때의 거리 (아무것도 버리지 않음).</summary>
    public const float NoCutOffsetM = 1.0e9f;

    /// <summary>주 스레드에서 만드는 단계: 시간 기록 열쇠와 화면 이름. <see cref="Build"/> 가 이 순서로 지나갑니다.</summary>
    public static readonly (string Key, string Label)[] BuildStages =
    [
        ("terrain", "회랑 지형 메시와 충돌면 만드는 중"),
        ("strata", "암석 색 텍스처 만드는 중"),
        ("surround", "주변 지형 메시와 충돌면 만드는 중"),
        ("water_caves", "물과 동굴 만드는 중"),
        ("cave_collision", "동굴 충돌면 만드는 중"),
        ("section", "지층 단면과 동굴 입구 준비하는 중"),
    ];

    /// <summary>manifest 를 읽고 레이어를 만들었는지.</summary>
    public bool Loaded { get; private set; }

    /// <summary>화면에 띄울 불러오기 문제 (한 줄에 하나). 없으면 빈 목록.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>읽은 폴더와 manifest.</summary>
    public string BakedDir { get; private set; } = "";

    public OrderedDictionary<string, object?> Manifest { get; private set; } = new();

    /// <summary>엔진 Y + YOffsetM = 해발 고도 (m).</summary>
    public float YOffsetM { get; private set; }

    /// <summary>회랑 사각형 (X, Z).</summary>
    public Rect2 CorridorRect { get; private set; }

    /// <summary>재질 부피 (없으면 null).</summary>
    public StrataVolume? Strata { get; private set; }

    /// <summary>지하수면 높이맵 (없으면 null).</summary>
    public HeightmapLoader? WaterTable { get; private set; }

    /// <summary>아래 지하수면 높이맵: 막는 층 아래 녹는 암석의 물 높이 (옛 굽기에는 없어 null).</summary>
    public HeightmapLoader? WaterTableLower { get; private set; }

    /// <summary>동굴 입구: 통로 한가운데 끝과 산비탈 밖 끝 (엔진 좌표).</summary>
    public List<Vector3> EntrancesInside { get; } = [];

    public List<Vector3> EntrancesOutside { get; } = [];

    /// <summary>
    /// 동굴 메시 삼각형 수와 불러온 방법. 늘 "gltf_runtime" 입니다: 편집기가 가져온(import) 사본은 다시 구운 뒤
    /// 가져오기를 돌리지 않으면 옛 동굴일 수 있어서, 실행 중에 파일을 직접 읽습니다.
    /// </summary>
    public int CaveFaces { get; private set; }

    public string CaveSource { get; private set; } = "";

    /// <summary>동굴 충돌체 (없으면 null) 와 그 조각 수·삼각형 수.</summary>
    public StaticBody3D? CaveBody { get; private set; }

    public int CaveCollisionTiles { get; private set; }

    public int CaveCollisionFaces { get; private set; }

    /// <summary>회랑 가장자리 땅속 벽 (동굴이 없으면 null). 회랑 밖으로 잘린 통로 끝에서 땅속으로 떨어지지 않게 막습니다.</summary>
    public StaticBody3D? CaveEdgeWall { get; private set; }

    /// <summary>단면이 켜져 있는지, 자르는 면의 한 점과 법선 (카메라 쪽).</summary>
    public bool SectionOn { get; private set; }

    public Vector3 SectionPoint { get; private set; } = Vector3.Zero;

    public Vector3 SectionNormal { get; private set; } = Vector3.Back;

    /// <summary>레이어를 켤 때 단면을 어디에 둘지 정하는 함수: () → (점, 법선). Main 이 넣습니다.</summary>
    public Func<(Vector3 Point, Vector3 Normal)>? SectionAnchor { get; set; }

    /// <summary>단계별로 걸린 시간 (ms).</summary>
    public OrderedDictionary<string, long> LoadMs { get; } = new();

    private HeightmapTerrain _terrain = null!;
    private HeightmapTerrain? _surround;
    private MeshInstance3D? _water;
    private MeshInstance3D? _waterTable;
    private Node3D? _caves;
    private MeshInstance3D? _section;
    private float _sectionYCenter;

    /// <summary>기본(디테일 없는) 회랑 높이맵. 디테일을 켤 때 높이 차를 재려고 불러오는 동안 한 번 읽습니다.</summary>
    private HeightmapLoader? _baseHeightmap;

    /// <summary>단면(자르는 면)을 받는 재질.</summary>
    private readonly List<ShaderMaterial> _cutMaterials = [];
    private ShaderMaterial? _terrainMaterial;
    private readonly Dictionary<string, bool> _visible = [];
    private bool _entranceHoles = true;

    /// <summary>회랑 지형의 기본 높이맵 경로 (Build 를 시작할 때의 Terrain.HeightmapStem).</summary>
    private string _baseStem = "";

    /// <summary>높이맵 경로 → 셰이더용 높이 텍스처. 프랙탈 디테일을 켜고 끌 때 다시 만들지 않습니다.</summary>
    private readonly Dictionary<string, ImageTexture> _textures = [];

    /// <summary>동굴 입구 파일 이름 → 읽은 높이맵.</summary>
    private readonly Dictionary<string, HeightmapLoader> _mouths = [];

    /// <summary>
    /// 다른 스레드가 읽어 둔 회랑 파일로 지형과 레이어를 만듭니다 (주 스레드). MoveNext 한 번에 일 한 조각(지형 메시 덩어리
    /// 하나, 충돌 조각 하나, 텍스처 묶음 하나 등)을 하고, 다음 조각이 속한 단계의 번호(<see cref="BuildStages"/>)를
    /// 돌려줍니다. Main 이 프레임마다 한 번씩 불러, 조각 사이에 불러오기 화면을 그립니다. 끝나면 회랑 지형이 있고, 구운
    /// 묶음이 있으면 Loaded 가 true 입니다.
    /// </summary>
    public IEnumerable<int> Build(CorridorFiles files, HeightmapTerrain terrain)
    {
        _terrain = terrain;
        _baseStem = terrain.HeightmapStem;
        _terrainMaterial = terrain.Material as ShaderMaterial;
        if (_terrainMaterial is not null)
        {
            _cutMaterials.Add(_terrainMaterial);
        }
        _visible["terrain"] = true;
        BakedDir = files.Dir;
        Problems.AddRange(files.Problems);
        foreach ((string key, long ms) in files.ReadMs)
        {
            LoadMs["read_" + key] = ms;
        }

        // 지형을 만들 때 동굴 입구 자리의 충돌면을 바로 비우도록, 입구 높이맵을 먼저 넣어 둡니다.
        foreach ((string name, HeightmapLoader mouth) in files.Mouths)
        {
            _mouths[name] = mouth;
        }
        terrain.HoleMouth = MouthForSurface;

        yield return 0;
        if (files.Terrain is not null)
        {
            if (files.UsesDetail)
            {
                terrain.HeightmapStem = DetailStem;
            }
            foreach (int _ in terrain.ApplySteps(files.Terrain))
            {
                yield return 0;
            }
        }
        if (files.Manifest is null)
        {
            yield break;
        }
        Manifest = files.Manifest;
        YOffsetM = files.YOffsetM;
        CorridorRect = files.CorridorRect;
        _baseHeightmap = files.BaseHeightmap;

        yield return 1;
        Strata = files.Strata;
        Strata?.CreateTextures();
        if (Strata is not null && !Strata.IsValid())
        {
            Problems.Add($"땅속 암석 부피의 텍스처를 만들지 못해 지질도 색(G)과 지층 단면(X) 없이 엽니다: {Strata.ErrorMessage}");
            Strata = null;
        }
        if (_terrainMaterial is not null)
        {
            _terrainMaterial.SetShaderParameter("color_mode", 1);
            _terrainMaterial.SetShaderParameter("y_offset_m", YOffsetM);
            _terrainMaterial.SetShaderParameter("open_entrances", _entranceHoles);
            if (Strata is not null && files.TerrainIsBaked)
            {
                Strata.ApplyTo(_terrainMaterial);
            }
            ApplyCaveMouth(files.UsesDetail ? CaveMouthDetailName : CaveMouthName);
        }

        yield return 2;
        foreach (int _ in BuildSurround(files.Surround))
        {
            yield return 2;
        }

        yield return 3;
        BuildWater(files.Water, files.WaterMesh);
        if (files.WaterTable is not null)
        {
            WaterTable = files.WaterTable;
            _visible["water_table"] = false;
        }
        WaterTableLower = files.WaterTableLower;
        yield return 3;
        BuildCaves(files);

        yield return 4;
        foreach (int _ in BuildCaveCollision())
        {
            yield return 4;
        }

        yield return 5;
        BuildSection();
        EntrancesInside.AddRange(files.EntrancesInside);
        EntrancesOutside.AddRange(files.EntrancesOutside);
        SetSection(false);
        yield return 5;
        SetupFractalDetail(files);
        Loaded = true;
    }

    /// <summary>불러오기 단계별 시간을 한 줄로 찍습니다 (Main 이 다 불러온 뒤 부름).</summary>
    public void PrintLoadTimes()
    {
        string times = string.Join(", ", LoadMs.Select(kv => $"\"{kv.Key}\": {kv.Value}"));
        GD.Print($"구운 레이어: {string.Join(", ", LayerIds())}, 시간 {{ {times} }} ms");
    }

    /// <summary>지금 있는 레이어 이름 (Layers 순서).</summary>
    public List<string> LayerIds() => Layers.Where(l => _visible.ContainsKey(l.Id)).Select(l => l.Id).ToList();

    public string LayerLabel(string id) => LayerById[id].Label;

    public bool CanSolo(string id) => LayerById[id].Solo;

    /// <summary>패널 버튼에 띄울 설명 (없으면 빈 글).</summary>
    public string LayerHint(string id) => LayerById[id].Hint;

    public bool IsLayerVisible(string id) => _visible.GetValueOrDefault(id, false);

    /// <summary>레이어 하나를 켜거나 끕니다. 없는 레이어면 아무것도 하지 않습니다.</summary>
    public void SetLayerVisible(string id, bool on)
    {
        if (!_visible.ContainsKey(id))
        {
            return;
        }
        switch (id)
        {
            case "terrain":
                _terrain.SetMeshVisible(on);
                break;
            case "surround":
                _surround?.SetMeshVisible(on);
                break;
            case "water":
                _water!.Visible = on;
                break;
            case "water_table":
                if (on && _waterTable is null)
                {
                    BuildWaterTable();
                }
                if (_waterTable is not null)
                {
                    _waterTable.Visible = on;
                }
                break;
            case "caves":
                _caves!.Visible = on;
                break;
            case "section":
                if (on && !SectionOn)
                {
                    if (SectionAnchor is not null)
                    {
                        (Vector3 point, Vector3 normal) = SectionAnchor();
                        SetSection(true, point, normal);
                        return;
                    }
                    Vector2 c = CorridorRect.GetCenter();
                    SetSection(true, new Vector3(c.X, 0.0f, c.Y), Vector3.Back);
                    return;
                }
                if (!on)
                {
                    SetSection(false);
                    return;
                }
                break;
            case "entrance_holes":
                _entranceHoles = on;
                _terrainMaterial?.SetShaderParameter("open_entrances", on);
                _terrain.SetHolesOpen(on);
                break;
            case "fractal_detail":
                on = UseDetailSurface(on);
                break;
        }
        _visible[id] = on;
        EmitSignal(SignalName.LayersChanged);
    }

    public void ToggleLayer(string id) => SetLayerVisible(id, !IsLayerVisible(id));

    /// <summary>이 레이어만 보이고 '만 보기' 대상인 나머지는 숨깁니다.</summary>
    public void Solo(string id)
    {
        foreach (string other in LayerIds())
        {
            if (CanSolo(other))
            {
                SetLayerVisible(other, other == id);
            }
        }
    }

    /// <summary>'만 보기' 대상 레이어를 모두 켭니다 (지하수면과 단면은 처음처럼 끔).</summary>
    public void ShowAll()
    {
        foreach (string id in LayerIds())
        {
            if (CanSolo(id))
            {
                SetLayerVisible(id, id != "water_table" && id != "section");
            }
        }
    }

    /// <summary>자르는 면을 켜거나 끕니다. normal 은 버릴 쪽(카메라 쪽)을 향합니다 (수평으로 맞춤).</summary>
    public void SetSection(bool on, Vector3 point = default, Vector3? normal = null)
    {
        Vector3 nIn = normal ?? Vector3.Back;
        var n = new Vector3(nIn.X, 0.0f, nIn.Z);
        n = n.Length() > 1e-6f ? n.Normalized() : Vector3.Back;
        SectionOn = on;
        SectionPoint = point;
        SectionNormal = n;
        var rect4 = new Vector4(CorridorRect.Position.X, CorridorRect.Position.Y, CorridorRect.End.X, CorridorRect.End.Y);
        foreach (ShaderMaterial m in _cutMaterials)
        {
            m.SetShaderParameter("plane_normal", n);
            m.SetShaderParameter("plane_offset_m", on ? n.Dot(point) : NoCutOffsetM);
            m.SetShaderParameter("section_rect", rect4);
        }
        if (_section is not null)
        {
            _section.Visible = on;
            if (on)
            {
                _section.GlobalTransform = new Transform3D(
                    Basis.LookingAt(-n, Vector3.Up), new Vector3(point.X, _sectionYCenter, point.Z));
            }
        }
        if (_visible.ContainsKey("section"))
        {
            _visible["section"] = on;
        }
        EmitSignal(SignalName.LayersChanged);
    }

    /// <summary>자르는 면을 법선 반대쪽(멀어지는 쪽)으로 distanceM 만큼 옮깁니다.</summary>
    public void MoveSection(float distanceM)
    {
        if (SectionOn)
        {
            SetSection(true, SectionPoint - (SectionNormal * distanceM), SectionNormal);
        }
    }

    /// <summary>지하수면 높이 (엔진 Y). 없거나 밖이면 NaN.</summary>
    public float WaterTableY(Vector3 p) => WaterTable is not null ? WaterTable.HeightAt(p) : float.NaN;

    private ShaderMaterial NewCutMaterial(string shaderPath)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>(shaderPath) };
        _cutMaterials.Add(m);
        return m;
    }

    /// <summary>
    /// 동굴 입구 구멍 파일 (cave_mouth 또는 cave_mouth_detail) 을 지형 재질에 넣습니다.
    /// 그 파일이 없으면 cave_mouth 를 씁니다. 둘 다 없으면 '입구 구멍' 선택이 없습니다.
    /// </summary>
    private void ApplyCaveMouth(string fileName)
    {
        HeightmapLoader mouth = LoadMouth(fileName);
        if (!mouth.IsValid() && fileName != CaveMouthName)
        {
            mouth = LoadMouth(CaveMouthName);
        }
        if (!mouth.IsValid() || _terrainMaterial is null)
        {
            return;
        }
        _terrainMaterial.SetShaderParameter("cave_mouth", CachedTexture(mouth));
        _terrainMaterial.SetShaderParameter("mouth_origin", new Vector2(mouth.Origin.X, mouth.Origin.Z));
        _terrainMaterial.SetShaderParameter("mouth_spacing_m", mouth.SpacingM);
        _terrainMaterial.SetShaderParameter("mouth_size", new Vector2(mouth.Width, mouth.Height));
        _visible["entrance_holes"] = _entranceHoles;
    }

    /// <summary>
    /// 회랑 지표 높이맵 경로 → 그 지표의 동굴 입구 높이맵 (지형 충돌면의 구멍, HeightmapTerrain.HoleMouth).
    /// 셰이더가 그리지 않는 자리(ApplyCaveMouth)와 같은 파일을 고릅니다. 파일이 없으면 null.
    /// 구멍을 열지 닫을지는 '동굴 입구 구멍' 레이어가 정합니다 (HeightmapTerrain.SetHolesOpen).
    /// </summary>
    private HeightmapLoader? MouthForSurface(string stem)
    {
        string? name = stem == BakedPaths.Resolve(DetailStem) ? CaveMouthDetailName
            : stem == BakedPaths.Resolve(_baseStem) ? CaveMouthName : null;
        // 지형 재질이 없으면 그림에 구멍을 낼 수 없으므로 충돌면도 막아 둡니다.
        if (name is null || _terrainMaterial is null)
        {
            return null;
        }
        HeightmapLoader mouth = LoadMouth(name);
        if (!mouth.IsValid() && name != CaveMouthName)
        {
            mouth = LoadMouth(CaveMouthName);
        }
        return mouth.IsValid() ? mouth : null;
    }

    /// <summary>
    /// 동굴 메시에 걷기용 충돌체를 붙이고(충돌 조각 하나마다 한 번 돌려줌), 회랑 가장자리 땅속에 벽을 세우고, 주변 지형
    /// 충돌면의 회랑 안 겹침 띠에서 동굴 자리를 비웁니다 (CaveCollision). 동굴이 없으면 아무것도 하지 않습니다.
    /// </summary>
    private IEnumerable<int> BuildCaveCollision()
    {
        if (_caves is null)
        {
            yield break;
        }
        var result = new CaveCollision.Result();
        foreach (int step in CaveCollision.BuildSteps(_caves, result))
        {
            yield return step;
        }
        (CaveBody, CaveCollisionTiles, CaveCollisionFaces) = (result.Body, result.Tiles, result.Faces);
        if (CaveBody is null)
        {
            yield break;
        }
        if (_terrain.Heightmap is HeightmapLoader surface && surface.IsValid())
        {
            float bottom = (_caves.Transform * new Vector3(0.0f, result.MinY, 0.0f)).Y - 1.0f;
            CaveEdgeWall = CaveCollision.BuildEdgeWall(this, CorridorRect, surface, bottom);
        }
        yield return 0;
        _surround?.ClearCollisionAt(CaveFootprint(result.Centers));
    }

    /// <summary>
    /// 동굴이 차지하는 자리 (엔진 좌표 점): 동굴 충돌 삼각형마다 세 꼭짓점의 가운데(centers, 동굴 노드 기준)와, 입구
    /// 높이맵(cave_mouth, cave_mouth_detail)이 음수인 표본. 주변 지형 충돌면이 회랑 안 겹침 띠에서 이 자리를 비웁니다
    /// (HeightmapTerrain.ClearCollisionAt).
    /// </summary>
    private List<Vector3> CaveFootprint(IReadOnlyList<Vector3> centers)
    {
        var points = new List<Vector3>(centers.Count);
        if (_caves is not null)
        {
            Transform3D xf = _caves.Transform;
            foreach (Vector3 c in centers)
            {
                points.Add(xf * c);
            }
        }
        foreach (string name in new[] { CaveMouthName, CaveMouthDetailName })
        {
            HeightmapLoader mouth = LoadMouth(name);
            for (int i = 0; mouth.IsValid() && i < mouth.Heights.Length; i++)
            {
                if (mouth.Heights[i] < 0.0f)
                {
                    points.Add(mouth.SamplePosition(i));
                }
            }
        }
        return points;
    }

    private HeightmapLoader LoadMouth(string fileName)
    {
        if (!_mouths.TryGetValue(fileName, out HeightmapLoader? mouth))
        {
            mouth = HeightmapLoader.LoadStem(BakedDir.PathJoin(fileName));
            _mouths[fileName] = mouth;
        }
        return mouth;
    }

    /// <summary>
    /// 회랑 지형이 프랙탈 디테일이면 지층 깊이를 디테일 지표에서 재게 맞추고, 켜진 선택 레이어로 둡니다. 끌 때 쓸 기본
    /// 지형은 다른 스레드에서 미리 준비합니다. 기본 높이맵을 읽지 못했으면 끌 수 없으므로 레이어를 두지 않습니다.
    /// </summary>
    private void SetupFractalDetail(CorridorFiles files)
    {
        if (!files.UsesDetail)
        {
            return;
        }
        ApplySurfaceOffset(true);
        if (_baseHeightmap is null || !_baseHeightmap.IsValid())
        {
            return;
        }
        _visible["fractal_detail"] = true;
        _terrain.PrepareInBackground(BakedPaths.Resolve(_baseStem), _baseHeightmap);
    }

    /// <summary>
    /// 회랑 지형을 프랙탈 디테일 높이맵(on) 또는 기본 높이맵으로 바꿉니다. 지형 재질과 메시가 보이는지는
    /// 그대로 두고, 동굴 입구 구멍과 단면 판의 지표 높이를 새 높이맵에 맞춥니다. 반환: 실제로 켜졌는지.
    /// </summary>
    private bool UseDetailSurface(bool on)
    {
        string stem = on ? DetailStem : _baseStem;
        if (_terrain.UsedStem != BakedPaths.Resolve(stem))
        {
            bool meshOn = _terrain.IsMeshVisible();
            _terrain.HeightmapStem = stem;
            if (!_terrain.Rebuild(true) || _terrain.UsedStem != BakedPaths.Resolve(stem))
            {
                GD.PushError($"프랙탈 디테일 지형을 읽지 못해 기본 지형으로 돌아갑니다: {stem}");
                on = false;
                _terrain.HeightmapStem = _baseStem;
                _terrain.Rebuild(true);
            }
            _terrain.SetMeshVisible(meshOn);
            EmitSignal(SignalName.TerrainRebuilt);
        }
        if (_terrainMaterial is not null)
        {
            ApplyCaveMouth(on ? CaveMouthDetailName : CaveMouthName);
        }
        ApplySectionSurface();
        ApplySurfaceOffset(on);
        return on;
    }

    /// <summary>
    /// 프랙탈 디테일을 켜면 지형 색칠과 단면이 재질 부피의 층 깊이를 실제로 그린 지표에서 재도록 두 높이맵
    /// (지금, 기본)을 넘깁니다 (strata.gdshaderinc 의 surface_offset). 끄면 차이 0.
    /// </summary>
    private void ApplySurfaceOffset(bool on)
    {
        HeightmapLoader? now = _terrain.Heightmap;
        if (on && _baseHeightmap is null)
        {
            _baseHeightmap = HeightmapLoader.LoadStem(BakedPaths.Resolve(_baseStem));
        }
        bool ok = on && now is not null && _baseHeightmap is not null && _baseHeightmap.IsValid()
            && _baseHeightmap.Width == now.Width && _baseHeightmap.Height == now.Height;
        var mats = new List<ShaderMaterial>();
        if (_terrainMaterial is not null)
        {
            mats.Add(_terrainMaterial);
        }
        if (_section is not null)
        {
            mats.Add((ShaderMaterial)_section.MaterialOverride);
        }
        foreach (ShaderMaterial m in mats)
        {
            m.SetShaderParameter("has_surface_offset", ok);
            if (ok)
            {
                m.SetShaderParameter("surface_now", CachedTexture(now!));
                m.SetShaderParameter("surface_base", CachedTexture(_baseHeightmap!));
                m.SetShaderParameter("offset_origin", now!.Origin);
                m.SetShaderParameter("offset_spacing_m", now.SpacingM);
                m.SetShaderParameter("offset_size", new Vector2(now.Width, now.Height));
            }
        }
    }

    /// <summary>주변 지형을 만듭니다. 메시 덩어리나 충돌 조각 하나마다 한 번 돌려줍니다 (<see cref="HeightmapTerrain.ApplySteps"/>).</summary>
    private IEnumerable<int> BuildSurround(PreparedTerrain? prepared)
    {
        if (prepared is null)
        {
            yield break;
        }
        var m = new ShaderMaterial { Shader = GD.Load<Shader>(TerrainShaderPath) };
        m.SetShaderParameter("color_mode", 1);
        m.SetShaderParameter("y_offset_m", YOffsetM);
        m.SetShaderParameter("hole_rect", new Vector4(
            CorridorRect.Position.X, CorridorRect.Position.Y, CorridorRect.End.X, CorridorRect.End.Y));
        _surround = new HeightmapTerrain
        {
            Name = "Surround",
            HeightmapStem = prepared.Stem,
            FallbackStem = "",
            Material = m,
            CollisionHole = CorridorRect,
            BuildOnReady = false,
        };
        AddChild(_surround);
        foreach (int step in _surround.ApplySteps(prepared))
        {
            yield return step;
        }
        if (prepared.IsValid)
        {
            _visible["surround"] = true;
        }
    }

    private void BuildWater(HeightmapLoader? hm, MeshArrays? wet)
    {
        if (hm is null || wet is null)
        {
            return;
        }
        _water = new MeshInstance3D
        {
            Name = "Water",
            Mesh = wet.ToArrayMesh(),
            Position = hm.Origin,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        ShaderMaterial m = NewCutMaterial(WaterShaderPath);
        m.SetShaderParameter("water_color", WaterColor);
        _water.MaterialOverride = m;
        AddChild(_water);
        _visible["water"] = true;
    }

    /// <summary>
    /// 물이 있는 표본이 하나라도 닿는 칸마다 사각형을 만듭니다. 물이 없는 꼭짓점은 같은 칸의 물 높이 평균을 씁니다
    /// (그 자리는 지표가 더 높아 가려집니다). 물이 없으면 빈 배열. Godot 자원을 만들지 않으므로 주 스레드 밖에서 불러도 됩니다.
    /// </summary>
    public static MeshArrays WetQuadArrays(HeightmapLoader hm)
    {
        int w = hm.Width;
        int h = hm.Height;
        float[] hs = hm.Heights;
        // GDScript 사전처럼 처음 넣은 순서를 지킵니다.
        var quads = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < hs.Length; i++)
        {
            if (hs[i] <= WaterNoneBelowM)
            {
                continue;
            }
            int row = i / w;
            int col = i % w;
            for (int dr = -1; dr <= 0; dr++)
            {
                for (int dc = -1; dc <= 0; dc++)
                {
                    int r = row + dr;
                    int c = col + dc;
                    if (r >= 0 && c >= 0 && r < h - 1 && c < w - 1 && seen.Add((r * w) + c))
                    {
                        quads.Add((r * w) + c);
                    }
                }
            }
        }
        var verts = new List<Vector3>();
        var indices = new List<int>();
        float s = hm.SpacingM;
        foreach (int q in quads)
        {
            int row = q / w;
            int col = q % w;
            int[] ids = [q, q + 1, q + w, q + w + 1];
            double sum = 0.0;
            int wet = 0;
            foreach (int id in ids)
            {
                if (hs[id] > WaterNoneBelowM)
                {
                    sum += hs[id];
                    wet++;
                }
            }
            float fill = (float)(sum / wet);
            int b = verts.Count;
            for (int k = 0; k < 4; k++)
            {
                float y = hs[ids[k]] > WaterNoneBelowM ? hs[ids[k]] : fill;
                verts.Add(new Vector3((col + (k % 2)) * s, y, (row + (k / 2)) * s));
            }
            // 위에서 볼 때 시계 방향 (지형과 같음): 북서, 북동, 남서 / 북동, 남동, 남서
            indices.AddRange([b, b + 1, b + 2, b + 1, b + 3, b + 2]);
        }
        var normals = new Vector3[verts.Count];
        Array.Fill(normals, Vector3.Up);
        return new MeshArrays { Vertices = verts.ToArray(), Normals = normals, Indices = indices.ToArray() };
    }

    private void BuildWaterTable()
    {
        if (WaterTable is null)
        {
            return;
        }
        _waterTable = new MeshInstance3D
        {
            Name = "WaterTable",
            Mesh = WaterTable.BuildMesh(),
            Position = WaterTable.Origin,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        ShaderMaterial m = NewCutMaterial(WaterShaderPath);
        m.SetShaderParameter("water_color", WaterTableColor);
        m.SetShaderParameter("roughness", 0.3f);
        _waterTable.MaterialOverride = m;
        AddChild(_waterTable);
        SetSection(SectionOn, SectionPoint, SectionNormal);
    }

    /// <summary>
    /// 다른 스레드가 해석해 둔 동굴 메시(caves.glb)로 장면을 만듭니다. 장면은 해석한 문서로 만들어야
    /// 메시가 그릴 수 있는 꼴(ArrayMesh)로 바뀝니다.
    /// </summary>
    private void BuildCaves(CorridorFiles files)
    {
        if (files.CaveDocument is null || files.CaveState is null)
        {
            return;
        }
        Node? root = files.CaveDocument.GenerateScene(files.CaveState);
        if (root is not Node3D root3d)
        {
            root?.Free();
            Problems.Add($"동굴 메시에서 장면을 만들지 못해 동굴 레이어 없이 엽니다: {files.CavePath}. {files.RebakeHint()}");
            return;
        }
        CaveSource = "gltf_runtime";
        ShaderMaterial m = NewCutMaterial(CaveShaderPath);
        if (Strata is not null)
        {
            m.SetShaderParameter("palette", Strata.Palette!);
            m.SetShaderParameter("use_palette", true);
        }
        foreach (Node node in root3d.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)node;
            mi.MaterialOverride = m;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                CaveFaces += (mi.Mesh is ArrayMesh am
                    ? am.SurfaceGetArrayIndexLen(s)
                    : mi.Mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array().Length) / 3;
            }
        }
        _caves = root3d;
        _caves.Name = "Caves";
        AddChild(_caves);
        _visible["caves"] = true;
    }

    private void BuildSection()
    {
        HeightmapLoader? surface = _terrain.Heightmap;
        if (Strata is null || surface is null || !surface.IsValid())
        {
            return;
        }
        var m = new ShaderMaterial { Shader = GD.Load<Shader>(SectionShaderPath) };
        Strata.ApplyTo(m);
        // 지하수면이 없으면 아주 깊게 두어 선과 물들이기가 생기지 않게 합니다.
        var deep = new HeightmapLoader { Width = 1, Height = 1, Heights = [-1.0e6f] };
        bool hasTable = WaterTable is not null && WaterTable.Width == surface.Width && WaterTable.Height == surface.Height;
        m.SetShaderParameter("water_table", HeightTexture(hasTable ? WaterTable! : deep));
        // 아래 지하수면이 없는 옛 굽기는 지하수면과 같다고 봅니다.
        bool hasLower = WaterTableLower is not null && WaterTableLower.Width == surface.Width
            && WaterTableLower.Height == surface.Height;
        m.SetShaderParameter("water_table_lower", HeightTexture(hasLower ? WaterTableLower! : (hasTable ? WaterTable! : deep)));
        _section = new MeshInstance3D
        {
            Name = "Section",
            Mesh = new QuadMesh(),
            MaterialOverride = m,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_section);
        _visible["section"] = false;
        ApplySectionSurface();
    }

    /// <summary>
    /// 단면 판의 지표 높이 (지표 위를 버리고 깊이 선을 긋는 기준) 와 판의 위아래 범위를 지금 회랑 지형에 맞춥니다.
    /// 프랙탈 디테일은 지표를 기본보다 높일 수 있습니다.
    /// </summary>
    private void ApplySectionSurface()
    {
        if (_section is null || Strata is null)
        {
            return;
        }
        HeightmapLoader surface = _terrain.Heightmap!;
        float yLo = surface.Origin.Y + Strata.Top.MinHeightM - Strata.DepthM() - SectionMarginM;
        float yHi = surface.Origin.Y + surface.MaxHeightM + SectionMarginM;
        _sectionYCenter = 0.5f * (yLo + yHi);
        ((QuadMesh)_section.Mesh).Size = new Vector2(2.0f * CorridorRect.Size.Length(), yHi - yLo);
        if (SectionOn)
        {
            Vector3 p = _section.GlobalPosition;
            p.Y = _sectionYCenter;
            _section.GlobalPosition = p;
        }
        var m = (ShaderMaterial)_section.MaterialOverride;
        m.SetShaderParameter("surface_height", CachedTexture(surface));
        m.SetShaderParameter("surface_origin", surface.Origin);
        m.SetShaderParameter("surface_spacing_m", surface.SpacingM);
        m.SetShaderParameter("surface_size", new Vector2(surface.Width, surface.Height));
    }

    /// <summary>높이맵 텍스처를 경로마다 한 번만 만듭니다.</summary>
    private ImageTexture CachedTexture(HeightmapLoader hm)
    {
        if (!_textures.TryGetValue(hm.Stem, out ImageTexture? tex))
        {
            tex = HeightTexture(hm);
            _textures[hm.Stem] = tex;
        }
        return tex;
    }

    private static ImageTexture HeightTexture(HeightmapLoader hm) =>
        ImageTexture.CreateFromImage(Image.CreateFromData(
            hm.Width, hm.Height, false, Image.Format.Rf, Files.FloatBytes(hm.Heights)));
}
