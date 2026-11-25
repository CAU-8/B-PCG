using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 높이맵 한 장을 읽어 메시(MeshInstance3D)와 충돌체(StaticBody3D)를 자식으로 만듭니다.
/// </summary>
/// <remarks>
/// 굽기 폴더가 없으면 HeightmapStem 대신 FallbackStem (samples/ 의 작은 표본)을 씁니다.
/// res://baked/ 로 시작하는 경로는 <see cref="BakedPaths.Resolve"/> 로 지금 굽기 폴더로 바뀝니다.
/// 만드는 일은 두 단계입니다. <see cref="Prepare(string, float, Rect2)"/> 가 파일을 읽고 메시 덩어리 배열과 충돌 조각을
/// 계산하고 (주 스레드 밖에서 불러도 됨), <see cref="ApplySteps"/> 가 그 결과로 노드를 만듭니다 (주 스레드, 메시 덩어리나
/// 충돌 조각 하나씩). 메시 Mesh 는 덩어리마다 면(surface) 하나이고, 충돌체 Body 아래에는 정사각 조각 Shape0, Shape1, … 이
/// 있습니다 (<see cref="HeightmapLoader.BuildMeshChunks"/>, <see cref="HeightmapLoader.BuildShapeTiles"/>).
/// <see cref="HoleMouth"/> 를 넣으면 동굴 입구 자리의 충돌면에 구멍을 뚫습니다 (구멍 닿은 조각을 바꾸고 Body/HolePatch 를 붙임).
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
    /// 한 칸 띠는 남깁니다. 그 띠에서도 동굴 자리는 <see cref="ClearCollisionAt"/> 로 비웁니다.
    /// </summary>
    [Export]
    public Rect2 CollisionHole { get; set; }

    /// <summary>
    /// 트리에 들어갈 때(_Ready) 바로 읽어 만들지. 회랑 장면의 지형은 false 이고, 불러오기 화면을 띄운 채 다른 스레드에서
    /// 준비한 결과로 <see cref="ApplySteps"/> 를 한 단계씩 돌립니다 (<see cref="BakedLayers.Build"/>).
    /// </summary>
    [Export]
    public bool BuildOnReady { get; set; } = true;

    /// <summary>읽은 높이맵. 읽기에 실패하면 IsValid() 가 false 입니다.</summary>
    public HeightmapLoader? Heightmap { get; private set; }

    /// <summary>실제로 읽은 높이맵 경로.</summary>
    public string UsedStem { get; private set; } = "";

    /// <summary>메시와 충돌면을 새로 만든 횟수. 전에 만든 경로를 다시 쓰면 세지 않습니다 (검사용).</summary>
    public int BuildCount { get; private set; }

    /// <summary>
    /// 높이맵 경로 → 그 지표의 동굴 입구 높이맵 (cave_mouth: 지표 점에서 동굴까지의 거리, 음수면 동굴 빈 곳 안).
    /// 돌려준 높이맵이 음수인 자리에 충돌면 구멍을 뚫어, 걷는 플레이어가 입구로 동굴에 들어갈 수 있게 합니다.
    /// null 이거나 null 을 돌려주면 구멍이 없는 충돌면입니다. BakedLayers 가 지형을 만들기 전에 넣습니다.
    /// </summary>
    public Func<string, HeightmapLoader?>? HoleMouth { get; set; }

    /// <summary>
    /// 동굴 입구 구멍을 열어 둘지. false 면 구멍 자리에 마개(<see cref="HeightmapLoader.HoleSet.Plug"/>)를 붙여 막습니다.
    /// 충돌 조각은 그대로 두고 작은 마개만 붙였다 떼므로 바로 바뀝니다. <see cref="SetHolesOpen"/> 으로 바꿉니다.
    /// </summary>
    public bool HolesOpen { get; private set; } = true;

    /// <summary>지금 열려 있는 충돌면 구멍 (구멍이 없거나 닫혀 있으면 null).</summary>
    public HeightmapLoader.HoleSet? Holes { get; private set; }

    private sealed record Built(
        HeightmapLoader Heightmap, ArrayMesh Mesh, HeightMapShape3D[] Shapes, Transform3D[] ShapeTransforms, IReadOnlyList<ShapeTile> Tiles);

    /// <summary>구멍을 뚫은 충돌면의 Godot 모양: 바뀐 조각 (조각 번호 → 모양), 덧판, 마개.</summary>
    private sealed record HoleShapes(
        HeightmapLoader.HoleSet Set, Dictionary<int, HeightMapShape3D> Tiles, ConcavePolygonShape3D? Patch, ConcavePolygonShape3D? Plug);

    /// <summary>(높이맵 경로, 입구 높이맵 경로) → 구멍을 뚫은 충돌면. 입구 구멍·프랙탈 디테일을 켜고 끌 때 다시 만들지 않습니다.</summary>
    private readonly Dictionary<(string Stem, string Mouth), HoleShapes> _holes = [];

    /// <summary>주변 지형이 회랑 안 겹침 띠에서 비운 동굴 자리 (<see cref="ClearCollisionAt"/>, 없으면 null).</summary>
    private HoleShapes? _cleared;

    /// <summary>경로 → 그 경로로 마지막에 만든 높이맵·메시·충돌 모양. Rebuild(true) 가 다시 씁니다.</summary>
    private readonly Dictionary<string, Built> _built = [];

    /// <summary>경로 → 다른 스레드에서 준비하는 중이거나 준비한 지형 (<see cref="PrepareInBackground"/>).</summary>
    private readonly Dictionary<string, Task<PreparedTerrain>> _preparing = [];

    public override void _Ready()
    {
        if (BuildOnReady)
        {
            Rebuild();
        }
    }

    /// <summary>
    /// 높이맵 stem 을 읽어 메시 덩어리 배열과 충돌 조각을 계산합니다. Godot 노드와 자원을 만들지 않으므로 주 스레드 밖에서
    /// 불러도 됩니다. 읽지 못하면 Heightmap.ErrorMessage 가 채워진 결과를 돌려줍니다.
    /// </summary>
    public static PreparedTerrain Prepare(string stem, float skirtM, Rect2 hole) =>
        Prepare(HeightmapLoader.LoadStem(stem), skirtM, hole);

    /// <summary>이미 읽은 높이맵으로 메시 덩어리 배열과 충돌 조각을 계산합니다 (주 스레드 밖에서 불러도 됨).</summary>
    public static PreparedTerrain Prepare(HeightmapLoader hm, float skirtM, Rect2 hole)
    {
        if (!hm.IsValid())
        {
            return new PreparedTerrain(hm.Stem, hm, [], []);
        }
        if (hole.HasArea())
        {
            // 경계 바로 안쪽 한 칸은 남겨 고운 지형과 이어지게 합니다.
            hole = hole.Grow(-hm.SpacingM);
        }
        return new PreparedTerrain(hm.Stem, hm, hm.BuildMeshChunks(skirtM), hm.BuildShapeTiles(hole));
    }

    /// <summary>
    /// 준비한 지형으로 자식 노드(메시, 충돌)를 한 번에 새로 만듭니다 (주 스레드). 성공하면 true. 실패하면 전에 있던 자식을
    /// 지우고 Heightmap 에 실패한 높이맵(ErrorMessage 채워짐)을 둡니다.
    /// </summary>
    public bool Apply(PreparedTerrain prepared)
    {
        foreach (int _ in ApplySteps(prepared))
        {
        }
        return prepared.IsValid;
    }

    /// <summary>
    /// <see cref="Apply"/> 를 나눠서 합니다: MoveNext 한 번에 메시 덩어리 하나나 충돌 조각 하나를 만듭니다. 부르는 쪽이
    /// 단계 사이에 프레임을 그리면, 큰 지형(laptop 회랑 150만 표본)을 만드는 동안에도 화면이 멈추지 않습니다.
    /// 돌려주는 값은 지금까지 한 단계 수입니다. 끝까지 돌아야 BuildCount 가 늘고 다시 쓸 수 있게 기억됩니다.
    /// </summary>
    public IEnumerable<int> ApplySteps(PreparedTerrain prepared)
    {
        ClearChildren();
        Heightmap = prepared.Heightmap;
        if (!prepared.IsValid)
        {
            UsedStem = "";
            yield break;
        }
        UsedStem = prepared.Stem;
        var mesh = new ArrayMesh();
        var meshInstance = new MeshInstance3D { Name = "Mesh", Mesh = mesh, Position = prepared.Heightmap.Origin };
        if (Material is not null)
        {
            meshInstance.MaterialOverride = Material;
        }
        AddChild(meshInstance);
        int step = 0;
        foreach (MeshArrays chunk in prepared.Chunks)
        {
            chunk.AddTo(mesh);
            yield return ++step;
        }
        var body = new StaticBody3D { Name = "Body" };
        AddChild(body);
        var shapes = new HeightMapShape3D[prepared.Tiles.Count];
        for (int i = 0; i < shapes.Length; i++)
        {
            shapes[i] = prepared.Tiles[i].ToShape();
            body.AddChild(new CollisionShape3D { Name = $"Shape{i}", Shape = shapes[i], Transform = prepared.Tiles[i].Transform });
            yield return ++step;
        }
        _built[prepared.Stem] = new Built(
            prepared.Heightmap, mesh, shapes, prepared.Tiles.Select(t => t.Transform).ToArray(), prepared.Tiles);
        BuildCount++;
        ApplyHoles();
    }

    /// <summary>
    /// 지금 HeightmapStem 이 가리키는 실제 경로. 그 파일이 없으면 FallbackStem, 그것도 비어 있으면 빈 글.
    /// </summary>
    public string ResolveStem()
    {
        string stem = BakedPaths.Resolve(HeightmapStem);
        if (HeightmapLoader.Exists(stem))
        {
            return stem;
        }
        return FallbackStem;
    }

    /// <summary>
    /// 경로 stem 의 지형을 다른 스레드에서 미리 준비해 둡니다. 다음 <see cref="Rebuild"/> 가 그 결과를 씁니다
    /// (프랙탈 디테일을 처음 끌 때 기본 지형을 읽고 계산하느라 화면이 멈추지 않게). 이미 읽은 높이맵이 있으면 넘깁니다.
    /// </summary>
    public void PrepareInBackground(string stem, HeightmapLoader? loaded = null)
    {
        if (_built.ContainsKey(stem) || _preparing.ContainsKey(stem))
        {
            return;
        }
        float skirtM = SkirtM;
        Rect2 hole = CollisionHole;
        _preparing[stem] = Task.Run(() => loaded is not null && loaded.IsValid() && loaded.Stem == stem
            ? Prepare(loaded, skirtM, hole)
            : Prepare(stem, skirtM, hole));
    }

    /// <summary>
    /// 높이맵을 다시 읽고 자식 노드를 새로 만듭니다. 성공하면 true. reuse 가 true 면 같은 경로로 전에 만든
    /// 높이맵·메시·충돌 모양을 다시 써서 읽기와 메시 만들기를 건너뜁니다 (프랙탈 디테일). 미리 준비해 둔 지형
    /// (<see cref="PrepareInBackground"/>)이 있으면 그 결과를 씁니다. 아직 계산 중이면 끝날 때까지 기다립니다.
    /// </summary>
    public bool Rebuild(bool reuse = false)
    {
        string stem = ResolveStem();
        if (stem.Length == 0)
        {
            ClearChildren();
            GD.Print($"지형: {BakedPaths.Resolve(HeightmapStem)} 가 없습니다");
            return false;
        }
        if (stem == FallbackStem && stem != BakedPaths.Resolve(HeightmapStem))
        {
            GD.Print($"지형: {BakedPaths.Resolve(HeightmapStem)} 가 없어 표본 {FallbackStem} 를 씁니다");
        }
        if (reuse && _built.TryGetValue(stem, out Built? built))
        {
            Use(built, stem);
            return true;
        }
        PreparedTerrain prepared;
        if (_preparing.Remove(stem, out Task<PreparedTerrain>? task))
        {
            try
            {
                // 이 계산은 Godot 노드·자원을 만들지 않으므로 주 스레드가 기다려도 서로 막히지 않습니다.
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                prepared = new PreparedTerrain(stem, HeightmapLoader.Failed(stem, $"{e.GetType().Name}: {e.Message}"), [], []);
            }
        }
        else
        {
            prepared = Prepare(stem, SkirtM, CollisionHole);
        }
        if (!Apply(prepared))
        {
            GD.PushError("지형을 읽지 못했습니다: " + prepared.Heightmap.ErrorMessage);
            return false;
        }
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

    /// <summary>만들어 둔 지형으로 자식 노드를 바꿉니다.</summary>
    private void Use(Built built, string stem)
    {
        ClearChildren();
        Heightmap = built.Heightmap;
        UsedStem = stem;

        var meshInstance = new MeshInstance3D { Name = "Mesh", Mesh = built.Mesh, Position = Heightmap.Origin };
        if (Material is not null)
        {
            meshInstance.MaterialOverride = Material;
        }
        AddChild(meshInstance);

        var body = new StaticBody3D { Name = "Body" };
        for (int i = 0; i < built.Shapes.Length; i++)
        {
            body.AddChild(new CollisionShape3D { Name = $"Shape{i}", Shape = built.Shapes[i], Transform = built.ShapeTransforms[i] });
        }
        AddChild(body);
        ApplyHoles();
    }

    /// <summary><see cref="HoleMouth"/> 를 넣거나 바꾼 뒤 지금 충돌면에 구멍을 뚫거나 없앱니다.</summary>
    public void RefreshHoles() => ApplyHoles();

    /// <summary>동굴 입구 구멍을 열거나(마개를 뗌) 닫습니다(마개를 붙임).</summary>
    public void SetHolesOpen(bool open)
    {
        HolesOpen = open;
        ApplyHoles();
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
    /// <see cref="CollisionHole"/> 안 겹침 띠에서 points (국소 좌표, x·z 만 씀) 가 든 삼각형의 CollisionHole 안 부분을
    /// 충돌면에서 뺍니다 (<see cref="HeightmapLoader.BuildRectClear"/>). 주변 지형의 보이지 않는 면이 동굴을 막지 않게
    /// 동굴 충돌면과 입구 구멍 자리를 넣습니다. 지형을 만든 뒤 한 번 부릅니다. 반환: 바꾼 충돌 조각 수.
    /// </summary>
    public int ClearCollisionAt(IReadOnlyCollection<Vector3> points)
    {
        if (!CollisionHole.HasArea() || points.Count == 0 || Heightmap is null || !_built.TryGetValue(UsedStem, out Built? built))
        {
            return 0;
        }
        HeightmapLoader.HoleSet set = Heightmap.BuildRectClear(CollisionHole, points, built.Tiles);
        _cleared = ToShapes(set, built.Tiles);
        foreach ((int i, HeightMapShape3D shape) in _cleared.Tiles)
        {
            // 다시 쓸 때(Use)도 비운 조각을 쓰도록 만들어 둔 모양을 바꿉니다.
            built.Shapes[i] = shape;
        }
        ApplyHoles();
        return _cleared.Tiles.Count;
    }

    /// <summary>
    /// 지금 높이맵과 HoleMouth 의 답에 맞게 충돌 조각(Body/Shape0, …)의 모양과 Body/HolePatch(덧판), Body/HolePlug(마개),
    /// Body/RectPatch(주변 지형의 회랑 띠 덧판)를 정합니다.
    /// </summary>
    private void ApplyHoles()
    {
        if (GetNodeOrNull<StaticBody3D>("Body") is not StaticBody3D body || !_built.TryGetValue(UsedStem, out Built? built))
        {
            return;
        }
        HoleShapes? holes = null;
        HeightmapLoader? mouth = HoleMouth?.Invoke(UsedStem);
        if (mouth is not null && mouth.IsValid() && built.Heightmap.IsValid())
        {
            (string, string) key = (UsedStem, mouth.Stem);
            if (!_holes.TryGetValue(key, out holes))
            {
                holes = ToShapes(built.Heightmap.BuildHoleSet(mouth, built.Tiles), built.Tiles);
                _holes[key] = holes;
            }
        }
        for (int i = 0; i < built.Shapes.Length; i++)
        {
            Shape3D want = holes is not null && holes.Tiles.TryGetValue(i, out HeightMapShape3D? open) ? open : built.Shapes[i];
            if (body.GetNodeOrNull<CollisionShape3D>($"Shape{i}") is CollisionShape3D node && node.Shape != want)
            {
                node.Shape = want;
            }
        }
        SetChildShape(body, "HolePatch", holes?.Patch);
        SetChildShape(body, "HolePlug", holes is not null && !HolesOpen ? holes.Plug : null);
        SetChildShape(body, "RectPatch", _cleared?.Patch);
        Holes = holes is not null && HolesOpen && holes.Set.Open.Length > 0 ? holes.Set : null;
    }

    /// <summary>구멍 계산 결과를 Godot 충돌 모양으로 바꿉니다 (주 스레드). 덧판·마개는 높이맵처럼 앞뒤 양면을 막습니다.</summary>
    private static HoleShapes ToShapes(HeightmapLoader.HoleSet set, IReadOnlyList<ShapeTile> tiles)
    {
        var tileShapes = new Dictionary<int, HeightMapShape3D>();
        foreach ((int i, float[] data) in set.TileData)
        {
            tileShapes[i] = new HeightMapShape3D { MapWidth = tiles[i].Size, MapDepth = tiles[i].Size, MapData = data };
        }
        return new HoleShapes(set, tileShapes, Trimesh(set.Patch), Trimesh(set.Plug));
    }

    private static ConcavePolygonShape3D? Trimesh(Vector3[] faces)
    {
        if (faces.Length == 0)
        {
            return null;
        }
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(faces);
        return shape;
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

    private void ClearChildren()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
    }
}

/// <summary>
/// 다른 스레드에서 읽고 계산해 둔 지형: 높이맵, 메시 덩어리 배열, 충돌 조각. 덩어리가 없으면 읽지 못한 것입니다
/// (Heightmap.ErrorMessage 에 까닭).
/// </summary>
public sealed record PreparedTerrain(string Stem, HeightmapLoader Heightmap, List<MeshArrays> Chunks, List<ShapeTile> Tiles)
{
    public bool IsValid => Chunks.Count > 0 && Heightmap.IsValid();
}
