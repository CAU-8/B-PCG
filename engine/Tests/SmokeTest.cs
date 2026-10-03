using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine.Tests;

/// <summary>
/// 엔진 연기 검사(smoke test). 화면 없이(--headless) 돌려 회랑 장면의 핵심 경로가 살아 있는지 봅니다.
/// </summary>
/// <remarks>
/// 실행 (csharp/Bpcg.Engine 에서, 처음 한 번과 코드를 바꾼 뒤에는 dotnet build 와 --import 를 먼저):
/// <c>godot --headless --path . res://Tests/smoke.tscn -- [--baked-dir=&lt;폴더&gt;] [--expect-baked]</c>.
/// Godot 는 스크립트 오류가 있어도 종료 코드 0 으로 끝나므로, 성공하면 'BPCG_SMOKE_OK' 를 찍고 Quit(0),
/// 실패하면 'BPCG_SMOKE_FAIL: &lt;이유&gt;' 를 찍고 Quit(1) 합니다. 구운 묶음이 있으면 레이어 불러오기와
/// 보이기·숨기기, 단면, 프랙탈 디테일도 검사합니다. --expect-baked 를 주면 묶음이 없을 때 실패합니다.
/// </remarks>
public partial class SmokeTest : Node
{
    public const string SampleStem = "res://samples/sample";
    public const string MainScene = "res://scenes/main.tscn";
    public const string CrossSectionShader = "res://shaders/cross_section.gdshader";

    /// <summary>장면에 붙는 C# 스크립트 (읽히고 인스턴스를 만들 수 있어야 함).</summary>
    public static readonly string[] NodeScripts =
    [
        "res://Scripts/Main.cs", "res://Scripts/HeightmapTerrain.cs", "res://Scripts/Player.cs",
        "res://Scripts/BakedLayers.cs", "res://Scripts/Hud.cs", "res://Scripts/StartMenu.cs",
        "res://Scripts/GlobeMain.cs", "res://Scripts/GlobeView.cs", "res://Scripts/GlobeCamera.cs",
        "res://Scripts/GlobeHud.cs",
    ];

    /// <summary>셰이더 → 꼭 있어야 하는 uniform (컴파일에 실패하면 uniform 목록이 비어 있습니다).</summary>
    public static readonly Dictionary<string, string[]> ShaderUniforms = new()
    {
        ["res://shaders/cross_section.gdshader"] =
            ["plane_normal", "plane_offset_m", "strata_thickness_m", "section_rect", "hole_rect", "color_mode", "strata", "palette"],
        ["res://shaders/strata_section.gdshader"] =
            ["strata", "strata_top", "palette", "surface_height", "water_table", "section_rect"],
        ["res://shaders/cave.gdshader"] = ["palette", "use_palette", "plane_normal"],
        ["res://shaders/water.gdshader"] = ["water_color", "plane_normal"],
    };

    public const string ExpectBakedArg = "--expect-baked";

    /// <summary>노클립 검사: 이만큼 물리 프레임 동안 키를 누릅니다.</summary>
    public const int FlyFrames = 30;
    public const float HeightToleranceM = 1e-3f;

    /// <summary>물리 광선이 맞힌 높이와 표본 높이의 허용 차이 (m).</summary>
    public const float RayToleranceM = 0.05f;

    /// <summary>플레이어가 땅에 닿기를 기다리는 최대 물리 프레임 수.</summary>
    public const int MaxLandingFrames = 600;

    /// <summary>검사 전체 시간 제한 (ms). 넘으면 실패로 끝냅니다.</summary>
    public const ulong WatchdogMs = 60000;

    /// <summary>프랙탈 디테일 높이맵 이름 (굽기 폴더 안).</summary>
    public const string DetailName = "heightmap_detail";

    private bool _done;
    private ulong _startedMs;

    public override void _Ready()
    {
        _startedMs = Time.GetTicksMsec();
        GD.Print($"Godot {Godot.Engine.GetVersionInfo()["string"]} | 물리 엔진: {ProjectSettings.GetSetting("physics/3d/physics_engine")}");
        _ = RunGuarded();
    }

    public override void _Process(double delta)
    {
        if (!_done && Time.GetTicksMsec() - _startedMs > WatchdogMs)
        {
            Fail($"시간 제한 {WatchdogMs} ms 를 넘었습니다");
        }
    }

    private async Task RunGuarded()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Fail($"예외: {e}");
        }
    }

    private async Task Run()
    {
        await ProcessFrame();
        if (!CheckScripts() || !CheckShader() || !CheckHeightmap())
        {
            return;
        }
        Main? main = InstantiateMain();
        if (main is null)
        {
            return;
        }
        if (!await CheckPhysics(main) || !await CheckNoclip(main) || !await CheckFractalDetail(main) || !CheckLayers(main))
        {
            return;
        }
        Pass();
    }

    private SignalAwaiter ProcessFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private SignalAwaiter PhysicsFrame() => ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

    /// <summary>장면에 붙는 C# 스크립트가 읽히고 인스턴스를 만들 수 있는지 봅니다.</summary>
    private bool CheckScripts()
    {
        foreach (string path in NodeScripts)
        {
            if (GD.Load(path) is not CSharpScript script || !script.CanInstantiate())
            {
                return Fail($"스크립트를 읽지 못했습니다: {path}");
            }
        }
        return true;
    }

    /// <summary>셰이더가 컴파일되는지 봅니다. 컴파일에 실패하면 uniform 목록이 비어 있습니다.</summary>
    private bool CheckShader()
    {
        foreach ((string path, string[] uniforms) in ShaderUniforms)
        {
            if (GD.Load(path) is not Shader shader)
            {
                return Fail($"셰이더를 읽지 못했습니다: {path}");
            }
            var names = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
            foreach (string name in uniforms)
            {
                if (!names.Contains(name))
                {
                    return Fail($"셰이더 uniform '{name}' 가 없습니다 (컴파일 오류?): {path}");
                }
            }
        }
        return true;
    }

    /// <summary>표본 높이맵을 HeightmapLoader 로 읽어 메시와 충돌 모양을 확인합니다.</summary>
    private bool CheckHeightmap()
    {
        OrderedDictionary<string, object?>? meta = Json.ReadObject(SampleStem + ".json");
        if (meta is null)
        {
            return Fail($"표본 설명 파일을 읽지 못했습니다: {SampleStem}.json");
        }
        HeightmapLoader hm = HeightmapLoader.LoadStem(SampleStem);
        if (!hm.IsValid())
        {
            return Fail("HeightmapLoader 가 표본을 읽지 못했습니다: " + hm.ErrorMessage);
        }
        int w = Json.I(meta["width"]);
        int h = Json.I(meta["height"]);
        if (hm.Width != w || hm.Height != h || hm.Heights.Length != w * h)
        {
            return Fail($"높이맵 크기가 다릅니다: {hm.Width} x {hm.Height}, 표본 {hm.Heights.Length}개");
        }

        ArrayMesh mesh = hm.BuildMesh();
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (vertices.Length != w * h)
        {
            return Fail($"꼭짓점 수 {vertices.Length} 가 width * height = {w * h} 와 다릅니다");
        }
        if (normals.Length != w * h)
        {
            return Fail($"법선 수 {normals.Length} 가 꼭짓점 수와 다릅니다");
        }
        if (indices.Length != (w - 1) * (h - 1) * 6)
        {
            return Fail($"인덱스 수 {indices.Length} 가 맞지 않습니다");
        }
        float lo = vertices.Min(v => v.Y);
        float hi = vertices.Max(v => v.Y);
        if (Math.Abs(lo - Json.D(meta["min"])) > HeightToleranceM || Math.Abs(hi - Json.D(meta["max"])) > HeightToleranceM)
        {
            return Fail($"메시 높이 범위 {lo} ~ {hi} 가 json 의 {Json.S(meta["min"])} ~ {Json.S(meta["max"])} 와 다릅니다");
        }
        if (normals.Any(n => n.Y <= 0.0f))
        {
            return Fail("아래를 향한 법선이 있습니다");
        }
        // Plane(a, b, c) 는 시계 방향 점 순서로 법선을 정합니다. 앞면이 위를 봐야 합니다.
        var first = new Plane(vertices[indices[0]], vertices[indices[1]], vertices[indices[2]]);
        if (first.Normal.Y <= 0.0f)
        {
            return Fail("삼각형 감김 방향이 반대입니다 (앞면이 아래를 봄)");
        }

        HeightMapShape3D shape = hm.BuildShape();
        if (shape.MapWidth != w || shape.MapDepth != h || shape.MapData.Length != w * h)
        {
            return Fail($"충돌 모양 크기가 다릅니다: {shape.MapWidth} x {shape.MapDepth}");
        }
        if (Math.Abs(shape.GetMinHeight() - Json.D(meta["min"])) > HeightToleranceM
            || Math.Abs(shape.GetMaxHeight() - Json.D(meta["max"])) > HeightToleranceM)
        {
            return Fail($"충돌 모양 높이 범위 {shape.GetMinHeight()} ~ {shape.GetMaxHeight()} 가 json 과 다릅니다");
        }

        int row = (h - 1) / 3;
        int col = (w - 1) / 4;
        Vector3 probe = hm.Origin + new Vector3(col * hm.SpacingM, 0.0f, row * hm.SpacingM);
        float expected = hm.Origin.Y + hm.Heights[(row * w) + col];
        if (Math.Abs(hm.HeightAt(probe) - expected) > HeightToleranceM)
        {
            return Fail($"HeightAt 이 표본 값과 다릅니다: {hm.HeightAt(probe)}, 기대 {expected}");
        }
        GD.Print($"높이맵: {w} x {h}, 간격 {hm.SpacingM:F1} m, 높이 {lo:F2} ~ {hi:F2} m");
        return true;
    }

    /// <summary>회랑 장면을 만들어 트리에 붙이고 구성이 맞는지 봅니다. 실패하면 null.</summary>
    private Main? InstantiateMain()
    {
        if (GD.Load(MainScene) is not PackedScene packed || packed.Instantiate() is not Main main)
        {
            Fail($"회랑 장면을 만들지 못했습니다: {MainScene}");
            return null;
        }
        GetTree().Root.AddChild(main);

        HeightmapTerrain? terrain = main.GetNodeOrNull<HeightmapTerrain>("Terrain");
        if (terrain?.Heightmap is null || !terrain.Heightmap.IsValid())
        {
            Fail("Terrain 노드가 없거나 높이맵을 읽지 못했습니다");
            return null;
        }
        if (terrain.GetNodeOrNull("Mesh") is not MeshInstance3D)
        {
            Fail("Terrain 아래 Mesh 가 없습니다");
            return null;
        }
        if (terrain.GetNodeOrNull("Body/Shape") is not CollisionShape3D collision || collision.Shape is not HeightMapShape3D)
        {
            Fail("Terrain 아래 Body/Shape (HeightMapShape3D) 가 없습니다");
            return null;
        }
        if (terrain.Material is not ShaderMaterial material || material.Shader?.ResourcePath != CrossSectionShader)
        {
            Fail("Terrain 재질이 단면 셰이더가 아닙니다");
            return null;
        }
        if (main.GetNodeOrNull("Player/Head/Camera") is not Camera3D camera || camera.Far < 10000.0f)
        {
            Fail("플레이어 카메라가 없거나 far 가 10000 m 보다 짧습니다");
            return null;
        }
        if (main.GetNodeOrNull("WorldEnvironment") is not WorldEnvironment envNode || envNode.Environment?.Sky is null)
        {
            Fail("WorldEnvironment 나 하늘(Sky)이 없습니다");
            return null;
        }
        if (!envNode.Environment.FogEnabled)
        {
            Fail("안개가 꺼져 있습니다");
            return null;
        }
        if (main.GetNodeOrNull("Sun") is not DirectionalLight3D)
        {
            Fail("DirectionalLight3D (Sun) 가 없습니다");
            return null;
        }
        if (main.GetNodeOrNull("Baked") is not BakedLayers)
        {
            Fail("Baked (BakedLayers) 노드가 없습니다");
            return null;
        }
        if (main.GetNodeOrNull("Hud") is not Hud)
        {
            Fail("Hud 노드가 없습니다");
            return null;
        }
        if (!ResourceLoader.Exists(Main.GlobeScene) || !ResourceLoader.Exists(Main.StartScene))
        {
            Fail($"M·N 키로 가는 장면이 없습니다: {Main.GlobeScene}, {Main.StartScene}");
            return null;
        }
        if (main.GetNodeOrNull("Player/Head/Camera/Lamp") is not Light3D)
        {
            Fail("손전등 (Player/Head/Camera/Lamp) 이 없습니다");
            return null;
        }
        GD.Print($"회랑 장면: 지형 {terrain.UsedStem}");
        return main;
    }

    private Godot.Collections.Array<Rid> RayExclude(Main main)
    {
        // 주변 25 m 지형의 충돌면은 회랑 경계 안쪽 한 칸까지 겹치므로 광선에서 뺍니다.
        var exclude = new Godot.Collections.Array<Rid> { main.Player.GetRid() };
        if (main.GetNodeOrNull("Baked/Surround/Body") is StaticBody3D surroundBody)
        {
            exclude.Add(surroundBody.GetRid());
        }
        return exclude;
    }

    /// <summary>물리 광선이 높이맵과 같은 자리를 맞히는지, 플레이어가 땅에 내려서는지 봅니다.</summary>
    private async Task<bool> CheckPhysics(Main main)
    {
        HeightmapTerrain terrain = main.Terrain;
        Player player = main.Player;
        HeightmapLoader hm = terrain.Heightmap!;
        // 이 검사는 걷기에서 합니다. 시작은 노클립입니다.
        if (!player.Noclip)
        {
            return Fail("플레이어가 노클립으로 시작하지 않았습니다");
        }
        player.SetNoclip(false);
        await PhysicsFrame();
        await PhysicsFrame();
        Godot.Collections.Array<Rid> exclude = RayExclude(main);

        PhysicsDirectSpaceState3D space = terrain.GetWorld3D().DirectSpaceState;
        float worst = 0.0f;
        int lastRow = hm.Height - 1;
        int lastCol = hm.Width - 1;
        var extent = new Vector3(lastCol * hm.SpacingM, 0.0f, lastRow * hm.SpacingM);
        Vector2I[] samples = [new(0, 0), new(lastRow / 3, lastCol / 4), new(lastRow / 2, lastCol / 2), new(lastRow, lastCol)];
        foreach (Vector2I rc in samples)
        {
            Vector3 local = hm.Origin + new Vector3(rc.Y * hm.SpacingM, 0.0f, rc.X * hm.SpacingM);
            // 가장자리 표본은 모양 경계에 걸리므로 안쪽으로 1 cm 옮겨 쏩니다.
            local.X = Math.Clamp(local.X, hm.Origin.X + 0.01f, hm.Origin.X + extent.X - 0.01f);
            local.Z = Math.Clamp(local.Z, hm.Origin.Z + 0.01f, hm.Origin.Z + extent.Z - 0.01f);
            Vector3 top = terrain.ToGlobal(new Vector3(local.X, hm.Origin.Y + hm.MaxHeightM + 100.0f, local.Z));
            Vector3 bottom = terrain.ToGlobal(new Vector3(local.X, hm.Origin.Y + hm.MinHeightM - 100.0f, local.Z));
            var query = PhysicsRayQueryParameters3D.Create(top, bottom, 0xFFFFFFFF, exclude);
            Godot.Collections.Dictionary hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                return Fail($"물리 광선이 지형을 맞히지 못했습니다: 행 {rc.X}, 열 {rc.Y}");
            }
            float expected = terrain.ToGlobal(new Vector3(local.X, terrain.HeightAt(local), local.Z)).Y;
            float y = hit["position"].AsVector3().Y;
            float err = Math.Abs(y - expected);
            worst = Math.Max(worst, err);
            if (err > RayToleranceM)
            {
                return Fail($"물리 광선 높이 {y} 가 높이맵 {expected} 와 {err} m 다릅니다 (행 {rc.X}, 열 {rc.Y})");
            }
        }

        int frames = 0;
        while (!player.IsOnFloor() && frames < MaxLandingFrames)
        {
            await PhysicsFrame();
            frames++;
        }
        if (!player.IsOnFloor())
        {
            return Fail($"플레이어가 {MaxLandingFrames} 물리 프레임 안에 땅에 닿지 않았습니다 (y = {player.GlobalPosition.Y})");
        }
        Vector3 feet = terrain.ToLocal(player.GlobalPosition);
        float ground = terrain.ToGlobal(new Vector3(feet.X, terrain.HeightAt(feet), feet.Z)).Y;
        if (Math.Abs(player.GlobalPosition.Y - ground) > 0.2f)
        {
            return Fail($"플레이어 발 높이 {player.GlobalPosition.Y} 가 지면 {ground} 와 다릅니다");
        }
        GD.Print($"물리: 광선 오차 최대 {worst:F4} m, 플레이어 착지 {frames} 프레임");
        return true;
    }

    /// <summary>노클립: 충돌을 끄고, 중력 없이 바라보는 방향과 위로 납니다.</summary>
    private async Task<bool> CheckNoclip(Main main)
    {
        Player player = main.Player;
        player.SetNoclip(true);
        if (!player.Shape.Disabled)
        {
            return Fail("노클립인데 충돌 모양이 켜져 있습니다");
        }
        Vector3 hr = player.Head.Rotation;
        hr.X = 0.0f;
        player.Head.Rotation = hr;
        await PhysicsFrame();
        Vector3 start = player.GlobalPosition;
        Vector3 forward = -player.GlobalBasis.Z;
        Input.ActionPress("move_forward");
        for (int i = 0; i < FlyFrames; i++)
        {
            await PhysicsFrame();
        }
        Input.ActionRelease("move_forward");
        Vector3 moved = player.GlobalPosition - start;
        float expected = player.FlySpeedMS * FlyFrames / Godot.Engine.PhysicsTicksPerSecond;
        if (Math.Abs(moved.Dot(forward) - expected) > 0.25f * expected || Math.Abs(moved.Y) > 1e-3f)
        {
            return Fail($"노클립 앞으로 날기: 움직임 {moved}, 기대 앞으로 {expected:F2} m, 높이 변화 0");
        }
        start = player.GlobalPosition;
        Input.ActionPress("fly_up");
        for (int i = 0; i < FlyFrames; i++)
        {
            await PhysicsFrame();
        }
        Input.ActionRelease("fly_up");
        float rise = player.GlobalPosition.Y - start.Y;
        if (Math.Abs(rise - expected) > 0.25f * expected)
        {
            return Fail($"노클립 위로 날기: {rise:F2} m 올랐습니다 (기대 {expected:F2} m)");
        }
        // 땅속에서 걷기로 바꾸면 지면 위로 올라옵니다.
        HeightmapTerrain terrain = main.Terrain;
        Vector3 c = terrain.Heightmap!.Center();
        player.GlobalPosition = terrain.ToGlobal(c - new Vector3(0.0f, 30.0f, 0.0f));
        player.SetNoclip(false);
        float ground = terrain.ToGlobal(c).Y;
        if (player.GlobalPosition.Y < ground)
        {
            return Fail($"땅속에서 걷기로 바꿨는데 지면 아래에 있습니다: {player.GlobalPosition.Y:F2} < {ground:F2}");
        }
        player.SetNoclip(true);
        GD.Print($"노클립: 앞 {moved.Dot(forward):F2} m, 위 {rise:F2} m ({FlyFrames} 프레임)");
        return true;
    }

    /// <summary>구운 레이어: 있으면 각 레이어의 보이기·숨기기, 만 보기, 단면을 검사합니다.</summary>
    private bool CheckLayers(Main main)
    {
        BakedLayers baked = main.Baked;
        Hud hud = main.Hud;
        HeightmapTerrain terrain = main.Terrain;
        if (!baked.Loaded)
        {
            if (OS.GetCmdlineUserArgs().Contains(ExpectBakedArg))
            {
                return Fail($"구운 묶음을 기대했지만 불러오지 못했습니다: {BakedPaths.Dir()}");
            }
            GD.Print("레이어: 구운 묶음이 없어 표본 지형만 검사했습니다");
            return true;
        }
        List<string> ids = baked.LayerIds();
        foreach (string need in new[] { "terrain", "surround", "water", "water_table", "section" })
        {
            if (!ids.Contains(need))
            {
                return Fail($"레이어 '{need}' 가 없습니다 (있는 것: {string.Join(", ", ids)})");
            }
        }
        OrderedDictionary<string, object?> man = baked.Manifest;
        OrderedDictionary<string, object?> meta = Json.Obj(man["file_meta"])!;
        List<object?> files = Json.Arr(man["files"]) ?? [];
        if (files.Contains("caves.glb") || Json.Obj(man["files"])?.ContainsKey("caves.glb") == true)
        {
            if (!ids.Contains("caves"))
            {
                return Fail("caves.glb 가 있는데 동굴 레이어가 없습니다");
            }
            int faces = Json.I(Json.Obj(meta["caves"])!["faces"]);
            if (baked.CaveFaces != faces)
            {
                return Fail($"동굴 삼각형 {baked.CaveFaces} 개가 manifest 의 {faces} 와 다릅니다");
            }
        }
        var water = baked.GetNode<MeshInstance3D>("Water");
        int nWet = Json.Get(Json.Obj(meta["water"]), "n_wet") is object nw ? Json.I(nw) : 0;
        if ((nWet > 0) != (water.Mesh.GetSurfaceCount() > 0))
        {
            return Fail($"물 표본 {nWet} 개인데 수면 메시 면 수가 {water.Mesh.GetSurfaceCount()} 입니다");
        }
        int nEntrances = Json.Get(Json.Obj(man["caves"]), "n_entrances") is object ne ? Json.I(ne) : 0;
        if (baked.EntrancesInside.Count != nEntrances)
        {
            return Fail($"동굴 입구 {baked.EntrancesInside.Count} 곳이 manifest 의 {nEntrances} 와 다릅니다");
        }
        List<object?> st = Json.Arr(Json.Obj(meta["strata"])!["shape"])!;
        if (baked.Strata is null || baked.Strata.Rows != Json.I(st[0]) || baked.Strata.Layers != Json.I(st[1])
            || baked.Strata.Cols != Json.I(st[2]))
        {
            return Fail("재질 부피를 읽지 못했거나 크기가 다릅니다");
        }
        // 지표 10 m 아래 표본은 재질 부피의 값이어야 합니다 (255 공기, 254 물 포함).
        Vector3 c = terrain.Heightmap!.Center();
        Vector3 probe = terrain.ToGlobal(c - new Vector3(0.0f, 10.0f, 0.0f));
        int rock = baked.Strata.IdAt(probe);
        if (rock < 0 || rock > StrataVolume.AirId)
        {
            return Fail($"지표 10 m 아래 재질 번호가 {rock} 입니다");
        }

        // 보이기·숨기기: 레이어마다 끄고 켭니다. 지형 버튼도 같이 바뀌어야 합니다.
        foreach (string id in ids)
        {
            bool before = baked.IsLayerVisible(id);
            baked.ToggleLayer(id);
            if (baked.IsLayerVisible(id) == before)
            {
                return Fail($"레이어 '{id}' 가 바뀌지 않았습니다");
            }
            Button? button = hud.LayerButton(id);
            if (button is null || button.ButtonPressed != baked.IsLayerVisible(id))
            {
                return Fail($"레이어 '{id}' 버튼 상태가 레이어와 다릅니다");
            }
            baked.ToggleLayer(id);
        }
        if (!terrain.IsMeshVisible())
        {
            return Fail("지형을 끄고 켰는데 보이지 않습니다");
        }
        string soloId = ids.Contains("caves") ? "caves" : "water";
        baked.Solo(soloId);
        foreach (string id in ids)
        {
            if (baked.CanSolo(id) && baked.IsLayerVisible(id) != (id == soloId))
            {
                return Fail($"만 보기 뒤 '{id}' 보이기가 {baked.IsLayerVisible(id)} 입니다");
            }
        }
        baked.ShowAll();
        if (!(terrain.IsMeshVisible() && baked.IsLayerVisible("surround")))
        {
            return Fail("모두 보기 뒤에 지형이 숨어 있습니다");
        }

        // 단면: 켜면 판이 보이고 지형 재질의 자르는 면이 바뀝니다.
        var m = (ShaderMaterial)terrain.Material!;
        Vector3 cg = terrain.ToGlobal(c);
        baked.SetSection(true, cg, Vector3.Back);
        var section = baked.GetNode<MeshInstance3D>("Section");
        float offset = m.GetShaderParameter("plane_offset_m").AsSingle();
        if (!section.Visible || Math.Abs(offset - Vector3.Back.Dot(cg)) > 1e-3f)
        {
            return Fail($"단면을 켰는데 판이 안 보이거나 자르는 면이 다릅니다 ({offset})");
        }
        baked.MoveSection(5.0f);
        offset = m.GetShaderParameter("plane_offset_m").AsSingle();
        if (Math.Abs(offset - (Vector3.Back.Dot(cg) - 5.0f)) > 1e-3f)
        {
            return Fail($"단면을 5 m 밀었는데 자르는 면이 {offset} 입니다");
        }
        baked.SetSection(false);
        if (section.Visible || m.GetShaderParameter("plane_offset_m").AsSingle() < 1.0e8f)
        {
            return Fail("단면을 껐는데 판이 보이거나 자르는 면이 남아 있습니다");
        }
        GD.Print($"레이어: {string.Join(", ", ids)}, 동굴 삼각형 {baked.CaveFaces} ({baked.CaveSource}), "
            + $"입구 {baked.EntrancesInside.Count}, 재질 부피 {baked.Strata.Cols} × {baked.Strata.Layers} × {baked.Strata.Rows}");
        return true;
    }

    /// <summary>
    /// 프랙탈 디테일: heightmap_detail 이 있으면 레이어가 맨 끝에 있고 처음에 켜져 있으며, 숫자 키와 패널 버튼으로
    /// 끄고 켤 때 회랑 지형(높이, 충돌)·단면 판의 지표·입구 구멍·버튼이 같이 바뀌는지 봅니다.
    /// </summary>
    private async Task<bool> CheckFractalDetail(Main main)
    {
        BakedLayers baked = main.Baked;
        if (!baked.Loaded)
        {
            return true;
        }
        Hud hud = main.Hud;
        HeightmapTerrain terrain = main.Terrain;
        Player player = main.Player;
        List<string> ids = baked.LayerIds();
        bool hasFile = HeightmapLoader.Exists(BakedPaths.Dir().PathJoin(DetailName));
        if (ids.Contains("fractal_detail") != hasFile)
        {
            return Fail($"heightmap_detail 파일 있음 = {hasFile} 인데 프랙탈 디테일 레이어 있음 = {ids.Contains("fractal_detail")}");
        }
        if (!hasFile)
        {
            GD.Print("프랙탈 디테일: 굽기 파일이 없어 레이어도 없습니다");
            return true;
        }
        if (ids[^1] != "fractal_detail" || baked.CanSolo("fractal_detail"))
        {
            return Fail($"프랙탈 디테일이 맨 끝 선택 레이어가 아닙니다: {string.Join(", ", ids)}");
        }
        if (!baked.IsLayerVisible("fractal_detail"))
        {
            return Fail("프랙탈 디테일 파일이 있는데 처음에 꺼져 있습니다");
        }
        if (!terrain.UsedStem.EndsWith(DetailName, StringComparison.Ordinal))
        {
            return Fail($"프랙탈 디테일이 켜졌는데 지형이 {terrain.UsedStem} 입니다");
        }
        Button? button = hud.LayerButton("fractal_detail");
        if (button is null || !button.ButtonPressed)
        {
            return Fail("프랙탈 디테일 버튼이 없거나 눌려 있지 않습니다");
        }
        HeightmapLoader detail = terrain.Heightmap!;
        HeightmapLoader b = HeightmapLoader.LoadStem(BakedPaths.Dir().PathJoin("heightmap"));
        if (!b.IsValid() || b.Heights.Length != detail.Heights.Length)
        {
            return Fail("기본 높이맵을 읽지 못했거나 디테일과 격자가 다릅니다");
        }
        // 두 높이맵이 가장 많이 다른 안쪽 표본에서 높이와 충돌면이 바뀌는지 봅니다.
        Vector2I worst = Vector2I.Zero;
        float worstM = 0.0f;
        for (int row = 1; row < detail.Height - 1; row++)
        {
            for (int col = 1; col < detail.Width - 1; col++)
            {
                int i = (row * detail.Width) + col;
                float d = Math.Abs(detail.Heights[i] - b.Heights[i]);
                if (d > worstM)
                {
                    worst = new Vector2I(col, row);
                    worstM = d;
                }
            }
        }
        if (worstM < 1e-3f)
        {
            return Fail("heightmap_detail 이 heightmap 과 같습니다");
        }
        Vector3 probe = detail.Origin + (new Vector3(worst.X, 0.0f, worst.Y) * detail.SpacingM);
        var section = baked.GetNodeOrNull<MeshInstance3D>("Section");
        var sectionM = section?.MaterialOverride as ShaderMaterial;
        GodotObject? detailSurface = sectionM?.GetShaderParameter("surface_height").AsGodotObject();
        var m = (ShaderMaterial)terrain.Material!;
        GodotObject? detailMouth = m.GetShaderParameter("cave_mouth").AsGodotObject();
        bool hasMouthDetail = HeightmapLoader.Exists(BakedPaths.Dir().PathJoin("cave_mouth_detail"));

        // 끄기: 숫자 키 (패널 순서 번호)
        Key key = Key.Key1 + ids.IndexOf("fractal_detail");
        ulong t = Time.GetTicksMsec();
        main._UnhandledInput(KeyEvent(key));
        ulong offMs = Time.GetTicksMsec() - t;
        if (baked.IsLayerVisible("fractal_detail") || button.ButtonPressed)
        {
            return Fail($"{OS.GetKeycodeString(key)} 키로 프랙탈 디테일이 꺼지지 않았거나 버튼이 눌려 있습니다");
        }
        if (terrain.UsedStem != BakedPaths.Dir().PathJoin("heightmap"))
        {
            return Fail($"프랙탈 디테일을 껐는데 지형이 {terrain.UsedStem} 입니다");
        }
        if (!terrain.IsMeshVisible())
        {
            return Fail("프랙탈 디테일을 끄니 지형 메시가 숨었습니다");
        }
        if (!await ExpectGround(terrain, main, probe, b, "끈 뒤"))
        {
            return false;
        }
        if (sectionM is not null && sectionM.GetShaderParameter("surface_height").AsGodotObject() == detailSurface)
        {
            return Fail("프랙탈 디테일을 껐는데 단면 판의 지표 높이 텍스처가 그대로입니다");
        }
        if (hasMouthDetail && m.GetShaderParameter("cave_mouth").AsGodotObject() == detailMouth)
        {
            return Fail("프랙탈 디테일을 껐는데 동굴 입구 텍스처가 그대로입니다");
        }

        // 켜기: 패널 버튼. 지형을 숨긴 채 바꾸면 숨은 채여야 합니다. 단면을 켠 채 바꾸면 단면 판이 새 지표의
        // 가장 높은 곳까지 덮어야 합니다.
        baked.SetLayerVisible("terrain", false);
        baked.SetSection(true, terrain.ToGlobal(probe), Vector3.Back);
        t = Time.GetTicksMsec();
        button.ButtonPressed = true;
        ulong onMs = Time.GetTicksMsec() - t;
        if (section is not null)
        {
            float topY = section.GlobalPosition.Y + (0.5f * ((QuadMesh)section.Mesh).Size.Y);
            float maxY = terrain.ToGlobal(new Vector3(0.0f, detail.Origin.Y + detail.MaxHeightM, 0.0f)).Y;
            if (topY < maxY)
            {
                return Fail($"프랙탈 디테일을 켰는데 단면 판 위 끝 {topY:F2} 가 지표 최고 {maxY:F2} 보다 낮습니다");
            }
        }
        baked.SetSection(false);
        if (!baked.IsLayerVisible("fractal_detail") || !terrain.UsedStem.EndsWith(DetailName, StringComparison.Ordinal))
        {
            return Fail($"패널 버튼으로 프랙탈 디테일이 켜지지 않았습니다 (지형 {terrain.UsedStem})");
        }
        if (terrain.IsMeshVisible() || baked.IsLayerVisible("terrain"))
        {
            return Fail("지형을 숨긴 채 프랙탈 디테일을 켰는데 지형 메시가 보입니다");
        }
        baked.SetLayerVisible("terrain", true);
        if (terrain.Heightmap != detail)
        {
            return Fail("프랙탈 디테일을 다시 켰는데 전에 만든 높이맵을 다시 쓰지 않았습니다");
        }
        if (!await ExpectGround(terrain, main, probe, detail, "다시 켠 뒤"))
        {
            return false;
        }
        if (sectionM is not null && sectionM.GetShaderParameter("surface_height").AsGodotObject() != detailSurface)
        {
            return Fail("프랙탈 디테일을 다시 켰는데 단면 판의 지표 높이 텍스처가 돌아오지 않았습니다");
        }
        if (m.GetShaderParameter("cave_mouth").AsGodotObject() != detailMouth)
        {
            return Fail("프랙탈 디테일을 다시 켰는데 동굴 입구 텍스처가 돌아오지 않았습니다");
        }

        // 걷는 중에 바꾸면 새 지면 위로 올라와야 합니다.
        player.SetNoclip(false);
        for (int i = 0; i < 2; i++)
        {
            Vector3 feet = terrain.ToGlobal(new Vector3(probe.X, terrain.HeightAt(probe) - 3.0f, probe.Z));
            player.GlobalPosition = feet;
            baked.ToggleLayer("fractal_detail");
            float ground = main.GroundY(player.GlobalPosition);
            if (!(player.GlobalPosition.Y >= ground))
            {
                player.SetNoclip(true);
                return Fail($"걷는 중에 프랙탈 디테일을 바꿨는데 땅속에 있습니다: {player.GlobalPosition.Y:F2} < {ground:F2}");
            }
        }
        player.SetNoclip(true);
        if (!baked.IsLayerVisible("fractal_detail"))
        {
            return Fail("프랙탈 디테일을 두 번 바꿨는데 켜져 있지 않습니다");
        }
        GD.Print($"프랙탈 디테일: 켜짐 (처음), {OS.GetKeycodeString(key)} 키, 높이 차 최대 {worstM:F2} m, "
            + $"끄기 {offMs} ms, 다시 켜기 {onMs} ms");
        return true;
    }

    /// <summary>지형의 높이(HeightAt)와 충돌면(물리 광선)이 local 에서 높이맵 hm 의 값과 같은지 봅니다.</summary>
    private async Task<bool> ExpectGround(HeightmapTerrain terrain, Main main, Vector3 local, HeightmapLoader hm, string when)
    {
        float want = hm.HeightAt(local);
        if (Math.Abs(terrain.HeightAt(local) - want) > HeightToleranceM)
        {
            return Fail($"프랙탈 디테일을 {when} 지형 높이 {terrain.HeightAt(local)} 가 기대 {want} 와 다릅니다");
        }
        // 새 충돌체가 물리 공간에 들어가도록 기다립니다.
        await PhysicsFrame();
        await PhysicsFrame();
        Vector3 top = terrain.ToGlobal(new Vector3(local.X, hm.Origin.Y + hm.MaxHeightM + 100.0f, local.Z));
        Vector3 bottom = terrain.ToGlobal(new Vector3(local.X, hm.Origin.Y + hm.MinHeightM - 100.0f, local.Z));
        var query = PhysicsRayQueryParameters3D.Create(top, bottom, 0xFFFFFFFF, RayExclude(main));
        Godot.Collections.Dictionary hit = terrain.GetWorld3D().DirectSpaceState.IntersectRay(query);
        float expected = terrain.ToGlobal(new Vector3(local.X, want, local.Z)).Y;
        if (hit.Count == 0 || Math.Abs(hit["position"].AsVector3().Y - expected) > RayToleranceM)
        {
            string got = hit.Count == 0 ? "빗나감" : $"{hit["position"].AsVector3().Y}";
            return Fail($"프랙탈 디테일을 {when} 물리 광선이 {got}, 기대 높이 {expected}");
        }
        return true;
    }

    private static InputEventKey KeyEvent(Key key) => new() { PhysicalKeycode = key, Keycode = key, Pressed = true };

    private void Pass()
    {
        if (_done)
        {
            return;
        }
        _done = true;
        GD.Print("BPCG_SMOKE_OK");
        GetTree().Quit(0);
    }

    private bool Fail(string reason)
    {
        if (!_done)
        {
            _done = true;
            GD.Print("BPCG_SMOKE_FAIL: " + reason);
            GetTree().Quit(1);
        }
        return false;
    }
}
