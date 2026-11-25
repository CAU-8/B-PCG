using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 굽기(Bpcg.Bake.Heightmap)가 쓴 높이맵 한 장(.bin + .json)을 읽습니다.
/// 읽은 뒤 그리기용 <see cref="ArrayMesh"/> 와 충돌용 <see cref="HeightMapShape3D"/> 를 만듭니다.
/// </summary>
/// <remarks>
/// 좌표 약속 (Godot 국소 접평면 좌표, 단위 m): X = 동쪽, Y = 위, Z = 남쪽.
/// 열(col)이 늘면 +X, 행(row)이 늘면 +Z. 0번 행이 북쪽 끝입니다.
/// 표본 (row, col) 의 위치 = origin + (col · spacing_m, 높이, row · spacing_m).
/// .bin 은 float32 리틀 엔디언입니다. Godot 가 도는 기기(x86_64, arm64)는 모두 리틀 엔디언이라
/// 바이트를 그대로 float 배열로 바꿉니다.
/// </remarks>
public sealed class HeightmapLoader
{
    public const string Format = "float32_le";

    /// <summary>열 수 (X 방향 표본 수).</summary>
    public int Width { get; set; }

    /// <summary>행 수 (Z 방향 표본 수). 고도가 아닙니다.</summary>
    public int Height { get; set; }

    /// <summary>표본 사이 수평 간격 (m).</summary>
    public float SpacingM { get; set; }

    /// <summary>북서쪽 모서리 표본(0번 행, 0번 열)의 국소 좌표 (m).</summary>
    public Vector3 Origin { get; set; } = Vector3.Zero;

    /// <summary>.json 에 적힌 최저, 최고 높이 (m).</summary>
    public float MinHeightM { get; set; }

    public float MaxHeightM { get; set; }

    /// <summary>높이 표본 (m), 행 우선. 인덱스 = row · width + col.</summary>
    public float[] Heights { get; set; } = [];

    /// <summary>.json 내용 전체.</summary>
    public OrderedDictionary<string, object?> Meta { get; private set; } = new();

    /// <summary>실패 이유. 비어 있으면 읽기에 성공한 것입니다.</summary>
    public string ErrorMessage { get; private set; } = "";

    /// <summary>읽은 파일의 경로 (확장자 뺀 것).</summary>
    public string Stem { get; private set; } = "";

    /// <summary>stem.json 과 stem.bin 이 둘 다 있는지 봅니다.</summary>
    public static bool Exists(string pathStem) =>
        Files.Exists(pathStem + ".json") && Files.Exists(pathStem + ".bin");

    /// <summary>높이맵을 읽습니다. 실패해도 null 이 아니라 ErrorMessage 가 채워진 객체를 돌려줍니다.</summary>
    public static HeightmapLoader LoadStem(string pathStem)
    {
        var loader = new HeightmapLoader();
        loader.Read(pathStem);
        return loader;
    }

    public bool IsValid() => ErrorMessage.Length == 0;

    /// <summary>
    /// 그리기용 메시를 만듭니다. 꼭짓점은 origin 을 뺀 타일 국소 좌표이므로 MeshInstance3D 를 origin 에 놓아야 합니다.
    /// 법선은 중심 차분으로 직접 계산합니다. skirtM &gt; 0 이면 가장자리를 따라 그만큼 아래로 내려가는 벽(양면)을
    /// 덧붙여 옆 타일과의 틈을 가립니다. 치마 꼭짓점은 width · height 개 뒤에 붙습니다.
    /// </summary>
    public ArrayMesh BuildMesh(float skirtM = 0.0f)
    {
        int count = Width * Height;
        int[] ring = skirtM > 0.0f ? Perimeter() : [];
        var vertices = new Vector3[count + ring.Length];
        var normals = new Vector3[count + ring.Length];
        var uvs = new Vector2[count + ring.Length];

        for (int row = 0; row < Height; row++)
        {
            int rowN = Math.Max(row - 1, 0);
            int rowS = Math.Min(row + 1, Height - 1);
            float dzM = (rowS - rowN) * SpacingM;
            int b = row * Width;
            for (int col = 0; col < Width; col++)
            {
                int colW = Math.Max(col - 1, 0);
                int colE = Math.Min(col + 1, Width - 1);
                int i = b + col;
                vertices[i] = new Vector3(col * SpacingM, Heights[i], row * SpacingM);
                float dxM = (colE - colW) * SpacingM;
                float dhDx = (Heights[b + colE] - Heights[b + colW]) / dxM;
                float dhDz = (Heights[(rowS * Width) + col] - Heights[(rowN * Width) + col]) / dzM;
                normals[i] = new Vector3(-dhDx, 1.0f, -dhDz).Normalized();
                uvs[i] = new Vector2((float)col / (Width - 1), (float)row / (Height - 1));
            }
        }

        // Godot 는 시계 방향(위에서 볼 때)을 앞면으로 봅니다.
        int[] indices = new int[((Width - 1) * (Height - 1) * 6) + (ring.Length * 12)];
        int k = 0;
        for (int row = 0; row < Height - 1; row++)
        {
            for (int col = 0; col < Width - 1; col++)
            {
                int nw = (row * Width) + col;
                int ne = nw + 1;
                int sw = nw + Width;
                int se = sw + 1;
                indices[k] = nw;
                indices[k + 1] = ne;
                indices[k + 2] = sw;
                indices[k + 3] = ne;
                indices[k + 4] = se;
                indices[k + 5] = sw;
                k += 6;
            }
        }

        // 치마: 둘레 표본마다 skirtM 아래 꼭짓점을 하나 두고, 이웃 둘레 표본과 사각형을 만듭니다.
        // 양쪽에서 보이도록 두 감김 방향을 모두 넣습니다.
        for (int j = 0; j < ring.Length; j++)
        {
            int top = ring[j];
            int v = count + j;
            vertices[v] = vertices[top] - new Vector3(0.0f, skirtM, 0.0f);
            normals[v] = normals[top];
            uvs[v] = uvs[top];
        }
        for (int j = 0; j < ring.Length; j++)
        {
            int a = ring[j];
            int bb = ring[(j + 1) % ring.Length];
            int aLow = count + j;
            int bLow = count + ((j + 1) % ring.Length);
            int[][] tris = [[a, bb, aLow], [bb, bLow, aLow], [a, aLow, bb], [bb, aLow, bLow]];
            foreach (int[] tri in tris)
            {
                indices[k] = tri[0];
                indices[k + 1] = tri[1];
                indices[k + 2] = tri[2];
                k += 3;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// 충돌 모양을 만듭니다. 놓을 자리는 ShapeTransform() 으로 얻습니다. hole 의 넓이가 0 보다 크면 그 사각형
    /// (국소 X, Z) 안 표본을 비워 충돌면에 구멍을 냅니다. 그 자리는 더 고운 지형(회랑)이 맡습니다.
    /// </summary>
    /// <remarks>
    /// 구멍 자리를 낮은 면으로 내려 두면, 회랑의 동굴로 들어간 플레이어가 그 보이지 않는 면에 설 수 있습니다.
    /// 그래서 표본을 아래로 내리지 않고 비웁니다(<see cref="HoleValue"/>).
    /// </remarks>
    public HeightMapShape3D BuildShape(Rect2 hole = default) =>
        new() { MapWidth = Width, MapDepth = Height, MapData = hole.HasArea() ? RectHoleData(hole, out _) : Heights };

    /// <summary>높이를 복사해 사각형 hole 안쪽(경계 제외) 표본을 비운 배열. inner: 비운 표본 표시.</summary>
    private float[] RectHoleData(Rect2 hole, out bool[] inner)
    {
        float[] data = (float[])Heights.Clone();
        inner = new bool[data.Length];
        int colLo = Math.Max(Mathf.FloorToInt((hole.Position.X - Origin.X) / SpacingM) + 1, 0);
        int colHi = Math.Min(Mathf.CeilToInt((hole.End.X - Origin.X) / SpacingM) - 1, Width - 1);
        int rowLo = Math.Max(Mathf.FloorToInt((hole.Position.Y - Origin.Z) / SpacingM) + 1, 0);
        int rowHi = Math.Min(Mathf.CeilToInt((hole.End.Y - Origin.Z) / SpacingM) - 1, Height - 1);
        for (int row = rowLo; row <= rowHi; row++)
        {
            for (int col = colLo; col <= colHi; col++)
            {
                int i = (row * Width) + col;
                if (i > 0)
                {
                    data[i] = HoleValue;
                    inner[i] = true;
                }
            }
        }
        return data;
    }

    /// <summary>
    /// 사각형 rect(국소 X, Z) 자리를 비운 충돌 모양과 덧판을 만듭니다. 주변 지형이 더 고운 회랑 지형에 자리를 내줄 때 씁니다.
    /// </summary>
    /// <remarks>
    /// rect 안쪽 한 칸 띠는 남겨 두 지형이 겹치게 합니다. 두 지형의 높이는 회랑 가장자리에서 다를 수 있어, 겹침 띠가 없으면
    /// 회랑 밖으로 걸어 나갈 때 계단에 막힙니다. 예를 들어 tiny 프로필(주변 지형 칸 100 m)로 만든 동굴 시험 회랑에서는
    /// 가장자리에서 주변 지형이 최대 24.7 m 높습니다.
    /// 다만 clear 의 점(국소 x, z: 동굴 충돌면과 입구 구멍)이 든 삼각형은 rect 안 부분을 비웁니다. 그 띠의 보이지 않는 면이
    /// 동굴 통로를 가로막거나 입구 구멍으로 떨어지는 플레이어를 받지 않게 하려는 것입니다. rect 밖 부분은 덧판으로 남깁니다.
    /// </remarks>
    public (HeightMapShape3D Shape, ConcavePolygonShape3D? Patch) BuildRectHoleShapes(Rect2 rect, IEnumerable<Vector3> clear)
    {
        Rect2 innerRect = rect.Grow(-SpacingM);
        float[] data = RectHoleData(innerRect, out bool[] inner);
        // clear 점이 든 삼각형 (겹침 띠에 남았던 것만). 겹침 띠보다 한 칸 더 안쪽 점은 이미 빈 삼각형에 드므로 건너뜁니다.
        // 삼각형 번호 = 칸 번호 · 2 + (0: 북서 반, 1: 남동 반).
        Rect2 deep = innerRect.Grow(-SpacingM);
        var cleared = new HashSet<int>();
        foreach (Vector3 p in clear)
        {
            if (deep.HasArea() && deep.HasPoint(new Vector2(p.X, p.Z)))
            {
                continue;
            }
            int tri = TriangleAt(p);
            if (tri >= 0 && !Touches(TriangleIds(tri), inner))
            {
                cleared.Add(tri);
            }
        }
        var opened = new List<int>();
        foreach (int tri in cleared)
        {
            (int a, int b, int c) = TriangleIds(tri);
            foreach (int v in new[] { a, b, c })
            {
                if (v > 0 && !float.IsNaN(data[v]))
                {
                    data[v] = HoleValue;
                    opened.Add(v);
                }
            }
        }
        var shape = new HeightMapShape3D { MapWidth = Width, MapDepth = Height, MapData = data };
        // 새로 비운 표본에 닿아 함께 빠진 삼각형을 되붙입니다: clear 삼각형은 rect 밖 부분만, 나머지는 통째로.
        // 처음부터 비운 표본(inner)에 닿은 삼각형은 원래 빠져 있던 것이라 그대로 둡니다.
        float x0 = rect.Position.X;
        float x1 = rect.End.X;
        float z0 = rect.Position.Y;
        float z1 = rect.End.Y;
        var faces = new List<Vector3>();
        foreach (int tri in new SortedSet<int>(opened.SelectMany(FanTriangles)))
        {
            (int a, int b, int c) = TriangleIds(tri);
            if (Touches((a, b, c), inner))
            {
                continue;
            }
            List<Vector3> t = [SamplePosition(a), SamplePosition(b), SamplePosition(c)];
            if (!cleared.Contains(tri))
            {
                faces.AddRange(t);
                continue;
            }
            // 삼각형 − rect = (x ≤ x0) ∪ (x ≥ x1) ∪ (x0 ≤ x ≤ x1, z ≤ z0) ∪ (x0 ≤ x ≤ x1, z ≥ z1): 서로 겹치지 않는 볼록 조각 넷
            List<Vector3> mid = Clip(Clip(t, p => p.X - x0), p => x1 - p.X);
            AddFan(faces, Clip(t, p => x0 - p.X));
            AddFan(faces, Clip(t, p => p.X - x1));
            AddFan(faces, Clip(mid, p => z0 - p.Z));
            AddFan(faces, Clip(mid, p => p.Z - z1));
        }
        return (shape, Trimesh(faces));
    }

    /// <summary>국소 (x, z) 가 든 삼각형 번호 (칸 번호 · 2 + 반). 높이맵 밖이면 −1.</summary>
    private int TriangleAt(Vector3 p)
    {
        float fx = (p.X - Origin.X) / SpacingM;
        float fz = (p.Z - Origin.Z) / SpacingM;
        int col = Mathf.FloorToInt(fx);
        int row = Mathf.FloorToInt(fz);
        if (col < 0 || row < 0 || col > Width - 2 || row > Height - 2)
        {
            return -1;
        }
        int half = (fx - col) + (fz - row) <= 1.0f ? 0 : 1;
        return (((row * (Width - 1)) + col) * 2) + half;
    }

    /// <summary>삼각형 번호 → 꼭짓점 표본 번호. 0: (북서, 북동, 남서), 1: (북동, 남동, 남서). 그리기 메시와 같은 순서.</summary>
    private (int A, int B, int C) TriangleIds(int tri)
    {
        int q = tri / 2;
        int nw = ((q / (Width - 1)) * Width) + (q % (Width - 1));
        return tri % 2 == 0 ? (nw, nw + 1, nw + Width) : (nw + 1, nw + Width + 1, nw + Width);
    }

    /// <summary>
    /// 표본 v 에 닿은 삼각형 번호 (많아야 6개): 북서 칸의 남동 반, 북동 칸의 두 반, 남서 칸의 두 반, 남동 칸의 북서 반.
    /// 높이맵 충돌이 비운 표본과 함께 빼는 삼각형과 같습니다.
    /// </summary>
    private IEnumerable<int> FanTriangles(int v)
    {
        int r = v / Width;
        int c = v % Width;
        (int Dr, int Dc, int Half)[] parts = [(-1, -1, 1), (-1, 0, 0), (-1, 0, 1), (0, -1, 0), (0, -1, 1), (0, 0, 0)];
        foreach ((int dr, int dc, int half) in parts)
        {
            int qr = r + dr;
            int qc = c + dc;
            if (qr >= 0 && qc >= 0 && qr < Height - 1 && qc < Width - 1)
            {
                yield return (((qr * (Width - 1)) + qc) * 2) + half;
            }
        }
    }

    private static bool Touches((int A, int B, int C) t, bool[] mask) => mask[t.A] || mask[t.B] || mask[t.C];

    /// <summary>볼록 다각형에서 f(p) ≥ 0 인 부분 (f 는 x, z 의 일차식). 꼭짓점 순서(앞면 방향)는 그대로입니다.</summary>
    private static List<Vector3> Clip(List<Vector3> poly, Func<Vector3, float> f)
    {
        var o = new List<Vector3>(poly.Count + 1);
        for (int k = 0; k < poly.Count; k++)
        {
            Vector3 p = poly[k];
            Vector3 q = poly[(k + 1) % poly.Count];
            float fp = f(p);
            float fq = f(q);
            if (fp >= 0.0f)
            {
                o.Add(p);
            }
            if ((fp >= 0.0f) != (fq >= 0.0f))
            {
                o.Add(p.Lerp(q, fp / (fp - fq)));
            }
        }
        return o;
    }

    /// <summary>
    /// 삼각형 t 에서 sign · f ≥ 0 인 부분. f 는 꼭짓점마다의 값이고, 변을 따라 선형으로 잇습니다 (꼭짓점 순서는 그대로).
    /// 예: f = (−1, 1, 1) 이고 sign = 1 이면 첫 꼭짓점에서 두 변의 가운데까지를 잘라 낸 사다리꼴, sign = −1 이면 그 작은 삼각형.
    /// </summary>
    private static List<Vector3> ClipByValues(List<Vector3> t, float[] f, float sign)
    {
        var o = new List<Vector3>(4);
        for (int k = 0; k < 3; k++)
        {
            int j = (k + 1) % 3;
            float fk = sign * f[k];
            float fj = sign * f[j];
            if (fk >= 0.0f)
            {
                o.Add(t[k]);
            }
            if ((fk >= 0.0f) != (fj >= 0.0f))
            {
                o.Add(t[k].Lerp(t[j], fk / (fk - fj)));
            }
        }
        return o;
    }

    /// <summary>볼록 다각형을 첫 꼭짓점에서 부채꼴로 나눠 넓이가 있는 삼각형만 faces 에 넣습니다.</summary>
    private static void AddFan(List<Vector3> faces, List<Vector3> poly)
    {
        for (int k = 1; k + 1 < poly.Count; k++)
        {
            // 꼭짓점 가까이에서 자르는 선이 지나면 넓이가 거의 0 인 삼각형이 생깁니다. 충돌에 쓸모가 없어 버립니다.
            if ((poly[k] - poly[0]).Cross(poly[k + 1] - poly[0]).LengthSquared() > MinPatchArea2)
            {
                faces.Add(poly[0]);
                faces.Add(poly[k]);
                faces.Add(poly[k + 1]);
            }
        }
    }

    /// <summary>삼각형 목록 → 앞뒤 양면 충돌 모양 (높이맵 충돌처럼 아래에서도 막음). 비었으면 null.</summary>
    private static ConcavePolygonShape3D? Trimesh(List<Vector3> faces)
    {
        if (faces.Count == 0)
        {
            return null;
        }
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces([.. faces]);
        return shape;
    }

    /// <summary>
    /// 높이맵 충돌에서 표본을 비우는 값. HeightMapShape3D 는 높이가 NaN 인 표본에 닿은 삼각형(표본 하나에 6개)을
    /// 충돌에서 뺍니다(Godot 물리, Jolt 물리 모두).
    /// </summary>
    /// <remarks>
    /// 0번 표본(북서쪽 모서리)은 비우지 않습니다. HeightMapShape3D 는 0번 표본으로 최저·최고 높이를 세기 시작하므로,
    /// 0번이 NaN 이면 둘 다 NaN 이 되고 구멍도 생기지 않습니다(Godot 4.7.2 + Jolt 에서 확인).
    /// </remarks>
    public const float HoleValue = float.NaN;

    /// <summary>동굴 입구 구멍을 뚫은 충돌 모양 (<see cref="BuildHoleShapes"/> 의 결과).</summary>
    /// <param name="Shape">구멍 표본을 비운 높이맵 충돌 모양. 놓을 자리는 ShapeTransform().</param>
    /// <param name="Patch">비운 표본이 함께 뺀 삼각형 가운데 구멍 밖 부분 (국소 좌표, 앞뒤 양면). 없으면 null.</param>
    /// <param name="Plug">같은 삼각형의 구멍 부분 (마개). 구멍을 닫을 때 Patch 와 함께 붙이면 원래 지형과 같습니다. 없으면 null.</param>
    /// <param name="Open">비운 표본 번호 (행 우선, 오름차순).</param>
    /// <param name="Mouth">표본마다 동굴까지의 거리 (m, 이 높이맵 격자).</param>
    /// <param name="PatchTriangles">덧판 삼각형 수.</param>
    /// <param name="PlugTriangles">마개 삼각형 수.</param>
    public sealed record HoleShapes(
        HeightMapShape3D Shape, ConcavePolygonShape3D? Patch, ConcavePolygonShape3D? Plug, int[] Open, float[] Mouth,
        int PatchTriangles, int PlugTriangles)
    {
        private readonly HashSet<int> _open = [.. Open];

        /// <summary>표본 i 를 비웠는지.</summary>
        public bool IsOpen(int i) => _open.Contains(i);
    }

    /// <summary>
    /// 동굴 입구 자리에 구멍을 뚫은 충돌 모양을 만듭니다. mouth 는 지표 점에서 동굴까지의 거리(m, 굽기의 cave_mouth)이고,
    /// 음수면 그 지표 점이 동굴 빈 곳 안입니다. 격자가 다르면 이 높이맵의 표본 자리에서 쌍선형으로 읽습니다.
    /// </summary>
    /// <remarks>
    /// 왜 필요한가: 지형 셰이더는 입구 자리를 그리지 않지만(cross_section.gdshader), 충돌면이 막혀 있으면 걷는 플레이어가
    /// 보이지 않는 땅에 서서 동굴에 들어가지 못합니다.
    /// 어떻게: mouth 가 음수인 표본을 비우면 그 표본에 닿은 삼각형 6개가 빠집니다. 빠진 삼각형을 삼각형 안에서 mouth 를
    /// 선형으로 이어 0 이 되는 선으로 잘라, mouth ≥ 0 인 부분(동굴 밖 땅)은 덧판으로, 나머지(구멍)는 마개로 둡니다.
    /// 예: 꼭짓점 mouth 가 (−1, 1, 1) 인 삼각형은 첫 꼭짓점에서 두 변의 가운데까지가 마개이고, 나머지 사다리꼴은 덧판입니다.
    /// 그래서 충돌면의 구멍은 동굴 빈 곳이 지표에 닿은 자리와 같고, 구멍으로 떨어지면 동굴 안에 떨어집니다.
    /// 구멍을 닫을 때는 마개만 붙입니다. 큰 높이맵 충돌 모양을 바꿔 끼우지 않으므로 켜고 끄기가 바로 됩니다.
    /// 높이맵 충돌은 아래에서 올라와도 막으므로(Jolt), 덧판과 마개도 양면으로 막습니다.
    /// 계산은 비운 표본에 닿은 삼각형만 돕니다 (laptop 회랑의 입구 표본 118개면 삼각형 700개 남짓).
    /// </remarks>
    public HoleShapes BuildHoleShapes(HeightmapLoader mouth)
    {
        float[] m = ValuesOnGrid(mouth);
        float[] data = (float[])Heights.Clone();
        var openIds = new List<int>();
        for (int i = 1; i < data.Length; i++)
        {
            if (m[i] < 0.0f)
            {
                data[i] = HoleValue;
                openIds.Add(i);
            }
        }
        var shape = new HeightMapShape3D { MapWidth = Width, MapDepth = Height, MapData = data };
        var patch = new List<Vector3>();
        var plug = new List<Vector3>();
        foreach (int tri in new SortedSet<int>(openIds.SelectMany(FanTriangles)))
        {
            (int a, int b, int c) = TriangleIds(tri);
            List<Vector3> t = [SamplePosition(a), SamplePosition(b), SamplePosition(c)];
            float[] f = [m[a], m[b], m[c]];
            AddFan(patch, ClipByValues(t, f, 1.0f));
            AddFan(plug, ClipByValues(t, f, -1.0f));
        }
        return new HoleShapes(shape, Trimesh(patch), Trimesh(plug), [.. openIds], m, patch.Count / 3, plug.Count / 3);
    }

    /// <summary>
    /// 다른 높이맵(같은 회랑 좌표)의 값을 이 높이맵의 표본 자리에서 읽습니다 (높이맵 안 값, Origin.Y 를 더하지 않음).
    /// 같은 격자면 그대로 쓰고, 밖이면 +∞.
    /// </summary>
    public float[] ValuesOnGrid(HeightmapLoader other)
    {
        if (other.Width == Width && other.Height == Height && Mathf.IsEqualApprox(other.SpacingM, SpacingM)
            && Mathf.IsEqualApprox(other.Origin.X, Origin.X) && Mathf.IsEqualApprox(other.Origin.Z, Origin.Z))
        {
            return other.Heights;
        }
        float[] v = new float[Width * Height];
        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                float h = other.HeightAt(Origin + new Vector3(col * SpacingM, 0.0f, row * SpacingM));
                v[(row * Width) + col] = float.IsFinite(h) ? h - other.Origin.Y : float.PositiveInfinity;
            }
        }
        return v;
    }

    /// <summary>표본 i 의 국소 좌표 (m).</summary>
    public Vector3 SamplePosition(int i) =>
        Origin + new Vector3((i % Width) * SpacingM, Heights[i], (i / Width) * SpacingM);

    /// <summary>덧판·마개 삼각형으로 남길 가장 작은 넓이의 (2배)² (m⁴). 1 cm² 삼각형의 값.</summary>
    private const float MinPatchArea2 = 4.0e-8f;

    /// <summary>HeightMapShape3D 는 가운데가 원점이고 표본 간격이 1 이므로, 수평으로 spacing 배 늘이고 타일 가운데로 옮깁니다.</summary>
    public Transform3D ShapeTransform()
    {
        Vector3 c = Origin + new Vector3((Width - 1) * SpacingM * 0.5f, 0.0f, (Height - 1) * SpacingM * 0.5f);
        return new Transform3D(Basis.FromScale(new Vector3(SpacingM, 1.0f, SpacingM)), c);
    }

    /// <summary>타일 가운데의 국소 좌표 (y 는 그 자리 높이, m).</summary>
    public Vector3 Center()
    {
        Vector3 p = Origin + new Vector3((Width - 1) * SpacingM * 0.5f, 0.0f, (Height - 1) * SpacingM * 0.5f);
        p.Y = HeightAt(p);
        return p;
    }

    /// <summary>국소 좌표 (x, z) 의 높이를 쌍선형 보간으로 구합니다 (m). 타일 밖이면 NaN.</summary>
    public float HeightAt(Vector3 localPos)
    {
        if (!IsValid())
        {
            return float.NaN;
        }
        double fx = (localPos.X - Origin.X) / (double)SpacingM;
        double fz = (localPos.Z - Origin.Z) / (double)SpacingM;
        if (fx < 0.0 || fz < 0.0 || fx > Width - 1 || fz > Height - 1)
        {
            return float.NaN;
        }
        int col = Math.Min((int)fx, Width - 2);
        int row = Math.Min((int)fz, Height - 2);
        double tx = fx - col;
        double tz = fz - row;
        int i = (row * Width) + col;
        double north = Mathf.Lerp(Heights[i], Heights[i + 1], tx);
        double south = Mathf.Lerp(Heights[i + Width], Heights[i + Width + 1], tx);
        return (float)(Origin.Y + Mathf.Lerp(north, south, tz));
    }

    /// <summary>둘레 표본 번호 (시계 방향, 겹침 없음): 북쪽 행 → 동쪽 열 → 남쪽 행 → 서쪽 열.</summary>
    private int[] Perimeter()
    {
        var ring = new List<int>();
        for (int col = 0; col < Width; col++)
        {
            ring.Add(col);
        }
        for (int row = 1; row < Height; row++)
        {
            ring.Add((row * Width) + Width - 1);
        }
        for (int col = Width - 2; col > -1; col--)
        {
            ring.Add(((Height - 1) * Width) + col);
        }
        for (int row = Height - 2; row > 0; row--)
        {
            ring.Add(row * Width);
        }
        return [.. ring];
    }

    private void Read(string pathStem)
    {
        Stem = pathStem;
        string jsonPath = pathStem + ".json";
        string binPath = pathStem + ".bin";
        if (!Files.Exists(jsonPath))
        {
            ErrorMessage = $"설명 파일이 없습니다: {jsonPath}";
            return;
        }
        if (!Files.Exists(binPath))
        {
            ErrorMessage = $"높이 파일이 없습니다: {binPath}";
            return;
        }
        OrderedDictionary<string, object?>? parsed = Json.ReadObject(jsonPath);
        if (parsed is null)
        {
            ErrorMessage = $"설명 파일을 JSON 으로 읽지 못했습니다: {jsonPath}";
            return;
        }
        Meta = parsed;
        foreach (string key in new[] { "format", "width", "height", "spacing_m", "min", "max", "origin" })
        {
            if (!Meta.ContainsKey(key))
            {
                ErrorMessage = $"{jsonPath} 에 '{key}' 필드가 없습니다";
                return;
            }
        }
        if (Json.S(Meta["format"]) != Format)
        {
            ErrorMessage = $"지원하지 않는 형식입니다: {Json.S(Meta["format"])} (읽을 수 있는 형식: {Format})";
            return;
        }

        Width = Json.I(Meta["width"]);
        Height = Json.I(Meta["height"]);
        SpacingM = Json.F(Meta["spacing_m"]);
        MinHeightM = Json.F(Meta["min"]);
        MaxHeightM = Json.F(Meta["max"]);
        List<object?>? o = Json.Arr(Meta["origin"]);
        if (Width < 2 || Height < 2 || SpacingM <= 0.0f || o is null || o.Count != 3)
        {
            ErrorMessage = $"{jsonPath} 의 크기, 간격, 원점 값이 잘못됐습니다";
            return;
        }
        Origin = new Vector3(Json.F(o[0]), Json.F(o[1]), Json.F(o[2]));

        byte[] bytes = Files.Bytes(binPath);
        if (bytes.Length != Width * Height * 4)
        {
            ErrorMessage = $"{binPath} 크기 {bytes.Length} 바이트가 width * height * 4 = {Width * Height * 4} 와 다릅니다";
            return;
        }
        Heights = Files.Floats(bytes);
    }
}
