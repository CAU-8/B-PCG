using System.Collections.Generic;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 높이맵 한 장을 읽어 메시(MeshInstance3D)와 충돌체(StaticBody3D)를 자식으로 만듭니다.
/// </summary>
/// <remarks>
/// 굽기 폴더가 없으면 HeightmapStem 대신 FallbackStem (samples/ 의 작은 표본)을 씁니다.
/// res://baked/ 로 시작하는 경로는 <see cref="BakedPaths.Resolve"/> 로 지금 굽기 폴더로 바뀝니다.
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

    /// <summary>이 사각형(국소 X, Z) 안은 충돌면을 아래로 내립니다. 넓이 0 이면 끔.</summary>
    [Export]
    public Rect2 CollisionHole { get; set; }

    /// <summary>읽은 높이맵. 읽기에 실패하면 IsValid() 가 false 입니다.</summary>
    public HeightmapLoader? Heightmap { get; private set; }

    /// <summary>실제로 읽은 높이맵 경로.</summary>
    public string UsedStem { get; private set; } = "";

    private sealed record Built(HeightmapLoader Heightmap, ArrayMesh Mesh, HeightMapShape3D Shape);

    /// <summary>경로 → 그 경로로 마지막에 만든 높이맵·메시·충돌 모양. Rebuild(true) 가 다시 씁니다.</summary>
    private readonly Dictionary<string, Built> _built = [];

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
            Rect2 hole = CollisionHole;
            if (hole.HasArea())
            {
                // 경계 바로 안쪽 한 칸은 남겨 고운 지형과 이어지게 합니다.
                hole = hole.Grow(-hm.SpacingM);
            }
            built = new Built(hm, hm.BuildMesh(SkirtM), hm.BuildShape(hole));
            _built[stem] = built;
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
        var shape = new CollisionShape3D { Name = "Shape", Shape = built.Shape, Transform = Heightmap.ShapeTransform() };
        body.AddChild(shape);
        AddChild(body);
        return true;
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
