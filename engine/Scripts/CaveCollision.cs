using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 동굴 메시(caves.glb)에 걷기용 충돌체를 붙입니다. 걷는 플레이어가 동굴 바닥에 서고 벽에 막히게 합니다.
/// </summary>
/// <remarks>
/// 메시를 수평 사각 조각(한 변 <see cref="TileM"/>)으로 나눠 조각마다 ConcavePolygonShape3D 하나를 둡니다.
/// 삼각형은 세 꼭짓점의 가운데가 든 조각에 넣습니다. 물리 엔진은 플레이어에서 먼 조각을 경계 상자만 보고 건너뜁니다.
/// 예: laptop 프로필 시드 0 회랑의 동굴 삼각형 205,192개는 조각 72개에 나뉩니다.
/// 동굴 벽의 앞면은 동굴 빈 곳을 봅니다(굽기의 caves.glb 약속). 충돌도 앞면만 막습니다(backface_collision 끔).
/// 그래서 동굴 안의 플레이어는 바닥과 벽에 막히고, 바위 속에서 벽 뒷면에 닿으면 막히지 않고 지나갑니다.
/// 충돌체는 물리 층 1(걷는 플레이어가 부딪는 층)과 <see cref="LayerBit"/>(동굴만 맞히는 광선용)에 둡니다.
/// </remarks>
public static class CaveCollision
{
    /// <summary>충돌 조각 한 변 (m). 회랑(1 km × 6 km)이면 조각이 많아야 8 × 47 개입니다.</summary>
    public const float TileM = 128.0f;

    /// <summary>동굴 충돌체만 맞히는 광선이 쓰는 물리 층 비트 (층 2).</summary>
    public const uint LayerBit = 1u << 1;

    /// <summary>충돌체 노드 이름 (동굴 뿌리 노드의 자식).</summary>
    public const string BodyName = "Body";

    /// <summary>
    /// 삼각형을 조각별로 모을 때 MoveNext 한 번에 다루는 삼각형 수. 한 번에 모두 모으면 laptop 회랑(삼각형 약 20만 개)에서
    /// 그 프레임이 약 0.1 s 멈췄습니다 (맥북 에어 M3). 2만 개씩이면 열한 번에 나뉩니다.
    /// </summary>
    public const int FacesPerStep = 20000;

    /// <summary><see cref="BuildSteps"/> 의 결과: 충돌체, 조각 수, 삼각형 수, 가장 낮은 꼭짓점의 높이 (root 기준 Y).</summary>
    public sealed class Result
    {
        public StaticBody3D? Body { get; set; }

        public int Tiles { get; set; }

        public int Faces { get; set; }

        public float MinY { get; set; } = float.PositiveInfinity;

        /// <summary>삼각형마다 세 꼭짓점의 가운데 (root 기준 좌표). 주변 지형 충돌면이 동굴 자리를 비울 때 씁니다.</summary>
        public List<Vector3> Centers { get; } = [];
    }

    /// <summary>
    /// root 아래 모든 MeshInstance3D 의 삼각형으로 root 의 자식 StaticBody3D (<see cref="BodyName"/>) 를 만듭니다.
    /// root 가 트리 밖이어도 됩니다 (꼭짓점은 부모를 따라 root 기준 좌표로 옮김). 삼각형이 없으면 충돌체를 만들지 않습니다.
    /// </summary>
    /// <remarks>
    /// MoveNext 한 번에 일 한 조각을 합니다: 처음에 삼각형을 <see cref="FacesPerStep"/> 개씩 조각별로 모으고,
    /// 그다음 충돌 조각 하나씩 붙입니다.
    /// 회랑을 불러오는 동안 조각 사이에 화면을 그릴 수 있습니다 (<see cref="BakedLayers.Build"/>).
    /// </remarks>
    public static IEnumerable<int> BuildSteps(Node3D root, Result result)
    {
        var tiles = new Dictionary<(int X, int Z), List<Vector3>>();
        int step = 0;
        foreach (Node node in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)node;
            if (mi.Mesh is null)
            {
                continue;
            }
            Transform3D xf = RelativeTransform(mi, root);
            Vector3[] v = mi.Mesh.GetFaces();
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                Vector3 a = xf * v[i];
                Vector3 b = xf * v[i + 1];
                Vector3 c = xf * v[i + 2];
                Vector3 mid = (a + b + c) / 3.0f;
                (int, int) key = (Mathf.FloorToInt(mid.X / TileM), Mathf.FloorToInt(mid.Z / TileM));
                if (!tiles.TryGetValue(key, out List<Vector3>? list))
                {
                    list = [];
                    tiles[key] = list;
                }
                list.Add(a);
                list.Add(b);
                list.Add(c);
                result.Centers.Add(mid);
                result.Faces++;
                result.MinY = Math.Min(result.MinY, Math.Min(a.Y, Math.Min(b.Y, c.Y)));
                if (result.Faces % FacesPerStep == 0)
                {
                    yield return ++step;
                }
            }
        }
        if (result.Faces == 0)
        {
            yield break;
        }
        yield return ++step;
        var body = new StaticBody3D { Name = BodyName, CollisionLayer = 1u | LayerBit };
        root.AddChild(body);
        result.Body = body;
        foreach ((int x, int z) in tiles.Keys.OrderBy(k => k.Z).ThenBy(k => k.X))
        {
            var shape = new ConcavePolygonShape3D();
            shape.SetFaces([.. tiles[(x, z)]]);
            body.AddChild(new CollisionShape3D { Name = $"Tile_{x}_{z}", Shape = shape });
            result.Tiles++;
            yield return ++step;
        }
    }

    /// <summary><see cref="BuildSteps"/> 를 한 번에 끝까지 돌립니다. 반환: (충돌체, 조각 수, 삼각형 수, 가장 낮은 꼭짓점 높이).</summary>
    public static (StaticBody3D? Body, int Tiles, int Faces, float MinY) Build(Node3D root)
    {
        var result = new Result();
        foreach (int _ in BuildSteps(root, result))
        {
        }
        return (result.Body, result.Tiles, result.Faces, result.MinY);
    }

    /// <summary>node 의 좌표를 root 기준으로 옮기는 변환 (트리 밖에서도 부모를 따라 곱함).</summary>
    private static Transform3D RelativeTransform(Node3D node, Node3D root)
    {
        Transform3D t = Transform3D.Identity;
        for (Node? n = node; n is not null && n != root; n = n.GetParent())
        {
            if (n is Node3D n3)
            {
                t = n3.Transform * t;
            }
        }
        return t;
    }

    /// <summary>회랑 가장자리 땅속 벽의 노드 이름 (BakedLayers 의 자식)과, 벽 위 끝을 지표보다 낮추는 거리 (m).</summary>
    public const string EdgeWallName = "CaveEdgeWall";

    public const float EdgeWallCoverM = 0.5f;

    /// <summary>
    /// 회랑 가장자리를 따라 땅속에 보이지 않는 벽을 세웁니다. 동굴 메시는 회랑 사각형에서 잘리므로, 회랑 밖으로 이어지는 통로는
    /// 끝이 열려 있습니다. 그 끝으로 걸어 나가면 아무 면도 없는 땅속으로 떨어집니다. 그래서 가장자리의 지표 표본마다
    /// 동굴 최저점(bottomY)부터 지표 EdgeWallCoverM 아래까지 벽을 두고, 앞면이 회랑 안쪽을 보게 해 앞면만 막습니다.
    /// 지표를 걷는 플레이어는 벽 위를 지나가고, 회랑 밖에서 들어올 때는 벽 뒷면이라 막히지 않습니다.
    /// 벽은 물리 층 1 에만 두어, 동굴만 맞히는 광선(<see cref="LayerBit"/>)에는 잡히지 않습니다. 반환: 벽 노드 (없으면 null).
    /// </summary>
    public static StaticBody3D? BuildEdgeWall(Node3D parent, Rect2 rect, HeightmapLoader surface, float bottomY)
    {
        var faces = new List<Vector3>();
        float step = surface.SpacingM;
        float x0 = rect.Position.X;
        float x1 = rect.End.X;
        float z0 = rect.Position.Y;
        float z1 = rect.End.Y;
        // (가장자리 시작점, 끝점, 회랑 안쪽 방향)
        (Vector3 A, Vector3 B, Vector3 Inward)[] edges =
        [
            (new(x0, 0, z0), new(x1, 0, z0), Vector3.Back),
            (new(x0, 0, z1), new(x1, 0, z1), Vector3.Forward),
            (new(x0, 0, z0), new(x0, 0, z1), Vector3.Right),
            (new(x1, 0, z0), new(x1, 0, z1), Vector3.Left),
        ];
        foreach ((Vector3 a, Vector3 b, Vector3 inward) in edges)
        {
            int n = Mathf.CeilToInt((b - a).Length() / step);
            for (int i = 0; i < n; i++)
            {
                // 높이맵 가장자리 바로 안쪽(1 cm)에서 지표를 읽습니다. 경계 위의 점은 반올림 때문에 밖으로 잡힐 수 있습니다.
                Vector3 p = a.Lerp(b, (float)i / n) + (inward * 0.01f);
                Vector3 q = a.Lerp(b, (float)(i + 1) / n) + (inward * 0.01f);
                float tp = surface.HeightAt(p) - EdgeWallCoverM;
                float tq = surface.HeightAt(q) - EdgeWallCoverM;
                if (!float.IsFinite(tp) || !float.IsFinite(tq) || Math.Min(tp, tq) <= bottomY)
                {
                    continue;
                }
                Vector3 pt = new(p.X, tp, p.Z);
                Vector3 qt = new(q.X, tq, q.Z);
                Vector3 qb = new(q.X, bottomY, q.Z);
                Vector3 pb = new(p.X, bottomY, p.Z);
                foreach ((Vector3 u, Vector3 v, Vector3 w) in new[] { (pt, qt, qb), (pt, qb, pb) })
                {
                    // Plane(a, b, c) 의 법선이 앞면 방향입니다 (시계 방향 점 순서). 회랑 안쪽을 보도록 맞춥니다.
                    bool flip = new Plane(u, v, w).Normal.Dot(inward) < 0.0f;
                    faces.AddRange(flip ? [u, w, v] : [u, v, w]);
                }
            }
        }
        if (faces.Count == 0)
        {
            return null;
        }
        var shape = new ConcavePolygonShape3D();
        shape.SetFaces([.. faces]);
        var body = new StaticBody3D { Name = EdgeWallName, CollisionLayer = 1u };
        body.AddChild(new CollisionShape3D { Name = "Shape", Shape = shape });
        parent.AddChild(body);
        return body;
    }

    /// <summary>
    /// 동굴 충돌체만 맞히는 아래쪽 광선 (from → 아래로 depthM). 벽 뒷면은 맞히지 않습니다. 맞은 높이 (전역 Y), 없으면 NaN.
    /// </summary>
    public static float FloorBelow(PhysicsDirectSpaceState3D space, Vector3 from, float depthM)
    {
        float d = HitDistance(space, from, Vector3.Down, depthM, false);
        return float.IsFinite(d) ? from.Y - d : float.NaN;
    }

    /// <summary>
    /// 전역 위치 p 가 동굴 빈 곳 안인지. 아래로 쏜 광선이 처음 맞는 동굴 면이 앞면(바닥)이고, 위와 옆 네 방향에서도
    /// 처음 맞는 동굴 면이 뒷면이 아니면 안입니다. 동굴 벽의 앞면은 빈 곳을 보므로, 바위 속에서 쏜 광선은 가장 가까운
    /// 벽의 뒷면을 먼저 맞힙니다. 입구나 수직굴처럼 위가 트인 곳은 위쪽 광선이 아무것도 맞히지 않아도 안으로 봅니다.
    /// </summary>
    /// <remarks>
    /// 재질 부피(strata.u8)의 공기 상자로는 가릴 수 없습니다. 상자가 4 m × 4 m × 2 m 라, 반지름 2 m 통로 안의 점이
    /// 바위 상자에 드는 일이 있습니다.
    /// </remarks>
    public static bool Inside(PhysicsDirectSpaceState3D space, Vector3 p, float reachM = InsideReachM)
    {
        foreach (Vector3 dir in new[] { Vector3.Down, Vector3.Up, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back })
        {
            float front = HitDistance(space, p, dir, reachM, false);
            float any = HitDistance(space, p, dir, reachM, true);
            if (any < front - 1e-3f || (dir == Vector3.Down && !float.IsFinite(front)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><see cref="Inside"/> 가 광선을 쏘는 거리 (m). 회랑 단면 부피의 깊이(300 m)와 같게 잡아 깊은 수직굴의 바닥까지 닿습니다.</summary>
    public const float InsideReachM = 300.0f;

    /// <summary>동굴 충돌체만 맞히는 광선이 처음 맞는 곳까지의 거리 (m). 없으면 +∞. backFaces 가 false 면 벽 뒷면은 지나칩니다.</summary>
    private static float HitDistance(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 dir, float reachM, bool backFaces)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, from + (dir * reachM), LayerBit);
        query.HitBackFaces = backFaces;
        Godot.Collections.Dictionary hit = space.IntersectRay(query);
        return hit.Count == 0 ? float.PositiveInfinity : hit["position"].AsVector3().DistanceTo(from);
    }

    /// <summary>충돌체의 삼각형 수 (그리기 메시의 삼각형 수와 견줄 때 씀).</summary>
    public static int CountFaces(StaticBody3D body) =>
        body.GetChildren().OfType<CollisionShape3D>().Sum(s => s.Shape is ConcavePolygonShape3D c ? c.GetFaces().Length / 3 : 0);
}
