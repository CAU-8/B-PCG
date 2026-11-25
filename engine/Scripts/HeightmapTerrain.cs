using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 높이맵 한 장을 읽어 메시(MeshInstance3D)와 충돌체(StaticBody3D)를 자식으로 만듭니다.
/// </summary>
/// <remarks>
/// 굽기 폴더가 없으면 HeightmapStem 대신 FallbackStem (samples/ 의 작은 표본)을 씁니다.
/// res://baked/ 로 시작하는 경로는 <see cref="BakedPaths.Resolve"/> 로 지금 굽기 폴더로 바뀝니다.
/// <see cref="HoleMouth"/> 를 넣으면 동굴 입구 자리의 충돌면에 구멍을 뚫습니다 (Body/Shape 를 바꾸고 Body/HolePatch 를 붙임).
/// 구멍을 닫을 때(<see cref="SetHolesOpen"/>)는 Body/HolePlug 를 붙입니다.
/// </remarks>
public partial class HeightmapTerrain : Node3D
{
    /// <summary>먼저 찾을 높이맵 (확장자 뺀 경로).</summary>
    [Export]
    public string HeightmapStem { get; set; } = "res://baked/heightmap";

    /// <summary>HeightmapStem 이 없을 때 쓸 높이맵. 비우면 대신 쓰지 않습니다.</summary>
    [Export]
    public string FallbackStem { get; set; } = "res://samples/sample";

    /// <summary>지형 메시에 씌울 재질. 비워 두면 기본 재질을 씁니다.</summary>
    [Export]
    public Material? Material { get; set; }

    /// <summary>가장자리 치마 깊이 (m). 0 이면 없음. 옆 지형과의 틈을 가립니다.</summary>
    [Export]
    public float SkirtM { get; set; }

    /// <summary>
    /// 이 사각형(국소 X, Z) 자리는 충돌면을 비워 더 고운 지형에 맡깁니다. 넓이 0 이면 끔. 두 지형이 이어지도록 경계 안쪽
    /// 한 칸 띠는 남깁니다 (<see cref="HeightmapLoader.BuildRectHoleShapes"/>).
    /// </summary>
    [Export]
    public Rect2 CollisionHole { get; set; }

    /// <summary>
    /// CollisionHole 안 겹침 띠에서도 비울 자리 (국소 좌표 점, x·z 만 씀). 이 점이 든 삼각형은 CollisionHole 안 부분을 비웁니다.
    /// 동굴 충돌면과 입구 구멍 자리를 넣어, 주변 지형의 보이지 않는 면이 동굴을 막지 않게 합니다. 트리에 붙이기 전에 넣습니다.
    /// </summary>
    public IReadOnlyCollection<Vector3> CollisionClear { get; set; } = [];

    /// <summary>읽은 높이맵. 읽기에 실패하면 IsValid() 가 false 입니다.</summary>
    public HeightmapLoader? Heightmap { get; private set; }

    /// <summary>실제로 읽은 높이맵 경로.</summary>
    public string UsedStem { get; private set; } = "";

    /// <summary>
    /// 높이맵 경로 → 그 지표의 동굴 입구 높이맵 (cave_mouth: 지표 점에서 동굴까지의 거리, 음수면 동굴 빈 곳 안).
    /// 돌려준 높이맵이 음수인 자리에 충돌면 구멍을 뚫어, 걷는 플레이어가 입구로 동굴에 들어갈 수 있게 합니다.
    /// null 이거나 null 을 돌려주면 구멍이 없는 충돌면입니다. BakedLayers 가 넣고, 넣은 뒤 <see cref="RefreshHoles"/> 를 부릅니다.
    /// </summary>
    public Func<string, HeightmapLoader?>? HoleMouth { get; set; }

    /// <summary>
    /// 동굴 입구 구멍을 열어 둘지. false 면 구멍 자리에 마개(<see cref="HeightmapLoader.HoleShapes.Plug"/>)를 붙여 막습니다.
    /// 큰 높이맵 충돌 모양은 그대로 두고 작은 마개만 붙였다 떼므로 바로 바뀝니다. <see cref="SetHolesOpen"/> 으로 바꿉니다.
    /// </summary>
    public bool HolesOpen { get; private set; } = true;

    /// <summary>지금 열려 있는 충돌면 구멍 (구멍이 없거나 닫혀 있으면 null).</summary>
    public HeightmapLoader.HoleShapes? Holes { get; private set; }

    private sealed record Built(HeightmapLoader Heightmap, ArrayMesh Mesh, HeightMapShape3D Shape, ConcavePolygonShape3D? RectPatch);

    /// <summary>경로 → 그 경로로 마지막에 만든 높이맵·메시·충돌 모양. Rebuild(true) 가 다시 씁니다.</summary>
    private readonly Dictionary<string, Built> _built = [];

    /// <summary>(높이맵 경로, 입구 높이맵 경로) → 구멍을 뚫은 충돌 모양. 입구 구멍·프랙탈 디테일을 켜고 끌 때 다시 만들지 않습니다.</summary>
    private readonly Dictionary<(string Stem, string Mouth), HeightmapLoader.HoleShapes> _holes = [];

    public override void _Ready() => Rebuild();

    /// <summary>
    /// 높이맵을 다시 읽고 자식 노드를 새로 만듭니다. 성공하면 true. reuse 가 true 면 같은 경로로 전에 만든
    /// 높이맵·메시·충돌 모양을 다시 써서 읽기와 메시 만들기를 건너뜁니다 (프랙탈 디테일).
    /// </summary>
    public bool Rebuild(bool reuse = false)
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        string stem = BakedPaths.Resolve(HeightmapStem);
        if (!HeightmapLoader.Exists(stem))
        {
            if (FallbackStem.Length == 0)
            {
                GD.Print($"지형: {stem} 가 없습니다");
                return false;
            }
            GD.Print($"지형: {stem} 가 없어 표본 {FallbackStem} 를 씁니다");
            stem = FallbackStem;
        }
        Built? built = reuse && _built.TryGetValue(stem, out Built? b) ? b : null;
        if (built is null)
        {
            HeightmapLoader hm = HeightmapLoader.LoadStem(stem);
            if (!hm.IsValid())
            {
                GD.PushError("지형을 읽지 못했습니다: " + hm.ErrorMessage);
                Heightmap = hm;
                return false;
            }
            HeightMapShape3D collision;
            ConcavePolygonShape3D? rectPatch = null;
            if (CollisionHole.HasArea())
            {
                (collision, rectPatch) = hm.BuildRectHoleShapes(CollisionHole, CollisionClear);
            }
            else
            {
                collision = hm.BuildShape();
            }
            built = new Built(hm, hm.BuildMesh(SkirtM), collision, rectPatch);
            _built[stem] = built;
            foreach ((string Stem, string Mouth) key in _holes.Keys.Where(k => k.Stem == stem).ToList())
            {
                _holes.Remove(key);
            }
        }
        Heightmap = built.Heightmap;
        UsedStem = stem;

        var meshInstance = new MeshInstance3D { Name = "Mesh", Mesh = built.Mesh, Position = Heightmap.Origin };
        if (Material is not null)
        {
            meshInstance.MaterialOverride = Material;
        }
        AddChild(meshInstance);

        var body = new StaticBody3D { Name = "Body" };
        var shape = new CollisionShape3D { Name = "Shape", Transform = Heightmap.ShapeTransform() };
        body.AddChild(shape);
        if (built.RectPatch is not null)
        {
            body.AddChild(new CollisionShape3D { Name = "RectPatch", Shape = built.RectPatch });
        }
        // 트리에 붙이기 전에 구멍까지 정해, 물리 엔진이 쓰지 않을 막힌 충돌면을 따로 만들지 않게 합니다.
        ApplyHoles(body, shape);
        AddChild(body);
        return true;
    }

    /// <summary><see cref="HoleMouth"/> 를 넣거나 바꾼 뒤 지금 충돌면에 구멍을 뚫거나 없앱니다.</summary>
    public void RefreshHoles()
    {
        if (GetNodeOrNull<StaticBody3D>("Body") is StaticBody3D body && body.GetNodeOrNull<CollisionShape3D>("Shape") is CollisionShape3D shape)
        {
            ApplyHoles(body, shape);
        }
    }

    /// <summary>동굴 입구 구멍을 열거나(마개를 뗌) 닫습니다(마개를 붙임).</summary>
    public void SetHolesOpen(bool open)
    {
        HolesOpen = open;
        RefreshHoles();
    }

    /// <summary>
    /// 이 노드 기준 국소 좌표 (x, z) 가 충돌면 구멍 가까이 (비운 표본을 꼭짓점으로 갖는 칸 안) 인지. 구멍이 없으면 false.
    /// 이런 칸의 충돌면은 높이맵과 다를 수 있습니다 (구멍이거나 덧판).
    /// </summary>
    public bool NearHole(Vector3 localPos)
    {
        if (Holes is null || Heightmap is null || !Heightmap.IsValid())
        {
            return false;
        }
        HeightmapLoader hm = Heightmap;
        int col = Mathf.FloorToInt((localPos.X - hm.Origin.X) / hm.SpacingM);
        int row = Mathf.FloorToInt((localPos.Z - hm.Origin.Z) / hm.SpacingM);
        for (int r = row; r <= row + 1; r++)
        {
            for (int c = col; c <= col + 1; c++)
            {
                if (r >= 0 && c >= 0 && r < hm.Height && c < hm.Width && Holes.IsOpen((r * hm.Width) + c))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 지금 높이맵과 HoleMouth 의 답에 맞게 Body/Shape 의 모양과 Body/HolePatch(덧판), Body/HolePlug(마개)를 정합니다.
    /// </summary>
    private void ApplyHoles(StaticBody3D body, CollisionShape3D shape)
    {
        HeightmapLoader.HoleShapes? holes = null;
        HeightmapLoader? mouth = UsedStem.Length > 0 ? HoleMouth?.Invoke(UsedStem) : null;
        if (mouth is not null && mouth.IsValid() && Heightmap is not null && Heightmap.IsValid())
        {
            (string, string) key = (UsedStem, mouth.Stem);
            if (!_holes.TryGetValue(key, out holes))
            {
                holes = Heightmap.BuildHoleShapes(mouth);
                _holes[key] = holes;
            }
        }
        Shape3D want = holes is not null ? holes.Shape : _built[UsedStem].Shape;
        if (shape.Shape != want)
        {
            shape.Shape = want;
        }
        SetChildShape(body, "HolePatch", holes?.Patch);
        SetChildShape(body, "HolePlug", holes is not null && !HolesOpen ? holes.Plug : null);
        Holes = holes is not null && HolesOpen && holes.Open.Length > 0 ? holes : null;
    }

    /// <summary>body 아래 name 이라는 충돌 모양 노드를 shape 로 맞춥니다 (null 이면 노드를 뗌).</summary>
    private static void SetChildShape(StaticBody3D body, string name, Shape3D? shape)
    {
        CollisionShape3D? node = body.GetNodeOrNull<CollisionShape3D>(name);
        if (shape is null)
        {
            if (node is not null)
            {
                body.RemoveChild(node);
                node.QueueFree();
            }
        }
        else if (node is null)
        {
            body.AddChild(new CollisionShape3D { Name = name, Shape = shape });
        }
        else if (node.Shape != shape)
        {
            node.Shape = shape;
        }
    }

    /// <summary>지형이 보이는지 (메시만 숨기고 충돌은 남깁니다).</summary>
    public void SetMeshVisible(bool on)
    {
        if (GetNodeOrNull("Mesh") is MeshInstance3D mesh)
        {
            mesh.Visible = on;
        }
    }

    public bool IsMeshVisible() => GetNodeOrNull("Mesh") is MeshInstance3D mesh && mesh.Visible;

    /// <summary>이 노드 기준 국소 좌표 (x, z) 의 지면 높이 (m). 지형 밖이면 NaN.</summary>
    public float HeightAt(Vector3 localPos) => Heightmap is null ? float.NaN : Heightmap.HeightAt(localPos);

    /// <summary>지형 가운데 지면 위 clearanceM 높이의 전역 좌표. 플레이어를 세울 때 씁니다.</summary>
    public Vector3 SpawnPoint(float clearanceM = 2.0f)
    {
        if (Heightmap is null || !Heightmap.IsValid())
        {
            return GlobalPosition + (Vector3.Up * clearanceM);
        }
        return ToGlobal(Heightmap.Center() + (Vector3.Up * clearanceM));
    }
}
