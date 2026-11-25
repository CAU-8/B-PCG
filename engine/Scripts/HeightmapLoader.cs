using System;
using System.Collections.Generic;
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

    /// <summary>메시 덩어리 하나에 담는 꼭짓점 수의 목표 (<see cref="BuildMeshChunks"/>).</summary>
    public const int MeshChunkVertices = 100_000;

    /// <summary>
    /// 충돌 조각 한 변의 표본 수 상한 (256 칸). 조각이 작을수록 한 조각을 만드는 동안 화면이 덜 멈춥니다.
    /// 큰 정사각 지형도 여러 조각으로 나눠 프레임마다 조금씩 만듭니다.
    /// </summary>
    public const int MaxShapeTile = 257;

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

    /// <summary>읽지 못한 높이맵 (까닭 message). 다른 스레드의 계산이 예외로 끝났을 때 씁니다.</summary>
    public static HeightmapLoader Failed(string pathStem, string message) =>
        new() { Stem = pathStem, ErrorMessage = message };

    /// <summary>
    /// 그리기용 메시를 만듭니다. 꼭짓점은 origin 을 뺀 타일 국소 좌표이므로 MeshInstance3D 를 origin 에 놓아야 합니다.
    /// 법선은 중심 차분으로 직접 계산합니다. skirtM &gt; 0 이면 가장자리를 따라 그만큼 아래로 내려가는 벽(양면)을
    /// 덧붙여 옆 타일과의 틈을 가립니다. 치마 꼭짓점은 width · height 개 뒤에 붙습니다.
    /// </summary>
    public ArrayMesh BuildMesh(float skirtM = 0.0f) => BuildMeshArrays(skirtM).ToArrayMesh();

    /// <summary>
    /// <see cref="BuildMesh"/> 의 배열만 계산합니다. Godot 자원을 만들지 않으므로 주 스레드 밖에서 불러도 됩니다
    /// (회랑 장면은 불러오는 동안 화면이 멈추지 않게 다른 스레드에서 계산합니다).
    /// </summary>
    public MeshArrays BuildMeshArrays(float skirtM = 0.0f)
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

        return new MeshArrays { Vertices = vertices, Normals = normals, Uvs = uvs, Indices = indices };
    }

    /// <summary>
    /// <see cref="BuildMeshArrays"/> 의 메시를 행 묶음 여러 덩어리로 나눕니다 (덩어리마다 꼭짓점 maxVertices 개 안팎).
    /// 이웃 덩어리와 경계 행을 같이 가지므로 이음매가 없고, 치마는 마지막 덩어리입니다. 주 스레드가 덩어리 하나씩 메시 면
    /// (surface)으로 올려, 큰 지형도 한 프레임에 몰아 만들지 않습니다. Godot 자원을 만들지 않으므로 주 스레드 밖에서
    /// 불러도 됩니다.
    /// </summary>
    public List<MeshArrays> BuildMeshChunks(float skirtM = 0.0f, int maxVertices = MeshChunkVertices)
    {
        MeshArrays full = BuildMeshArrays(skirtM);
        int rowsPerChunk = Math.Max(2, maxVertices / Width);
        var chunks = new List<MeshArrays>();
        for (int row0 = 0; ; row0 += rowsPerChunk - 1)
        {
            int row1 = Math.Min(row0 + rowsPerChunk - 1, Height - 1);
            int first = row0 * Width;
            int last = ((row1 + 1) * Width) - 1;
            // 행 우선으로 쌓은 사각형이라, 이 행 묶음의 삼각형 번호는 한 구간에 이어져 있습니다.
            int[] indices = full.Indices[(row0 * (Width - 1) * 6)..(row1 * (Width - 1) * 6)];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] -= first;
            }
            chunks.Add(new MeshArrays
            {
                Vertices = full.Vertices[first..(last + 1)],
                Normals = full.Normals[first..(last + 1)],
                Uvs = full.Uvs[first..(last + 1)],
                Indices = indices,
            });
            if (row1 == Height - 1)
            {
                break;
            }
        }
        int skirtStart = (Width - 1) * (Height - 1) * 6;
        if (full.Indices.Length > skirtStart)
        {
            // 치마 삼각형이 쓰는 꼭짓점(둘레 위·아래)만 모아 번호를 새로 매깁니다.
            var local = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            int[] indices = new int[full.Indices.Length - skirtStart];
            for (int i = 0; i < indices.Length; i++)
            {
                int g = full.Indices[skirtStart + i];
                if (!local.TryGetValue(g, out int l))
                {
                    l = vertices.Count;
                    local[g] = l;
                    vertices.Add(full.Vertices[g]);
                    normals.Add(full.Normals[g]);
                    uvs.Add(full.Uvs[g]);
                }
                indices[i] = l;
            }
            chunks.Add(new MeshArrays { Vertices = [.. vertices], Normals = [.. normals], Uvs = [.. uvs], Indices = indices });
        }
        return chunks;
    }

    /// <summary>
    /// 충돌 모양 하나를 만듭니다. 놓을 자리는 ShapeTransform() 으로 얻습니다. hole 의 넓이가 0 보다 크면 그 사각형
    /// (국소 X, Z) 안 표본을 최저 높이보다 holeDropM 아래로 내려, 더 고운 지형과 충돌면이 겹치지 않게 합니다.
    /// 가로·세로 표본 수가 다르면 물리 엔진이 느리게 만드므로, 지형에는 <see cref="BuildShapeTiles"/> 를 씁니다.
    /// </summary>
    public HeightMapShape3D BuildShape(Rect2 hole = default, float holeDropM = 50.0f) =>
        new() { MapWidth = Width, MapDepth = Height, MapData = ShapeHeights(hole, holeDropM) };

    /// <summary>HeightMapShape3D 는 가운데가 원점이고 표본 간격이 1 이므로, 수평으로 spacing 배 늘이고 타일 가운데로 옮깁니다.</summary>
    public Transform3D ShapeTransform() => TileTransform(0, 0, Width, Height);

    /// <summary>충돌 조각 한 변의 표본 수: 가로·세로 가운데 짧은 쪽, 많아야 <see cref="MaxShapeTile"/>.</summary>
    public int ShapeTileSize() => Math.Min(Math.Min(Width, Height), MaxShapeTile);

    /// <summary>
    /// 충돌 모양을 정사각 조각 여러 장으로 만듭니다. 조각 한 변의 표본 수는 <see cref="ShapeTileSize"/> 이고, 이웃 조각과
    /// 가장자리 표본 한 줄을 같이 씁니다. 맨 끝 조각은 높이맵 끝에 맞추므로 앞 조각과 겹칠 수 있습니다.
    /// Godot 의 Jolt 물리 엔진은 정사각 높이맵만 높이장(height field)으로 만들고, 가로·세로가 다르면 삼각형 메시로
    /// 바꿉니다. laptop 회랑(3,001 × 501 표본)을 메시로 바꾸는 데 5~8 s 걸렸고, 같은 표본을 501 × 501 조각 6장으로
    /// 나누면 0.09~0.7 s 걸렸습니다 (맥북 에어 M3, 2026-11-26, 화면 없이, 다른 작업 때문에 부하 평균 30~165).
    /// hole 은 <see cref="BuildShape"/> 와 같습니다. Godot 자원을 만들지 않으므로 주 스레드 밖에서 불러도 됩니다.
    /// </summary>
    public List<ShapeTile> BuildShapeTiles(Rect2 hole = default, float holeDropM = 50.0f)
    {
        float[] data = ShapeHeights(hole, holeDropM);
        int size = ShapeTileSize();
        var tiles = new List<ShapeTile>();
        foreach (int row0 in TileStarts(Height, size))
        {
            foreach (int col0 in TileStarts(Width, size))
            {
                float[] tile = new float[size * size];
                for (int r = 0; r < size; r++)
                {
                    Array.Copy(data, ((row0 + r) * Width) + col0, tile, r * size, size);
                }
                tiles.Add(new ShapeTile(size, tile, TileTransform(col0, row0, size, size)));
            }
        }
        return tiles;
    }

    /// <summary>
    /// 길이 n 인 축을 size 표본 조각으로 덮을 때 조각마다 시작 번호. 조각끼리 끝 표본 한 줄을 같이 쓰고, 마지막 조각은
    /// 끝(n − size)에 맞춥니다. 예: n = 3,001, size = 501 이면 0, 500, …, 2,500 (6장).
    /// </summary>
    public static List<int> TileStarts(int n, int size)
    {
        var starts = new List<int>();
        for (int s = 0; ; s += size - 1)
        {
            if (s + size >= n)
            {
                starts.Add(n - size);
                return starts;
            }
            starts.Add(s);
        }
    }

    /// <summary>(col0, row0) 에서 시작하는 cols × rows 표본 조각의 충돌 모양 자리 (HeightMapShape3D 는 가운데가 원점).</summary>
    private Transform3D TileTransform(int col0, int row0, int cols, int rows)
    {
        Vector3 c = Origin + new Vector3((col0 + ((cols - 1) * 0.5f)) * SpacingM, 0.0f, (row0 + ((rows - 1) * 0.5f)) * SpacingM);
        return new Transform3D(Basis.FromScale(new Vector3(SpacingM, 1.0f, SpacingM)), c);
    }

    /// <summary>충돌용 높이 (hole 안 표본은 최저 높이보다 holeDropM 아래). hole 이 없으면 Heights 그대로.</summary>
    private float[] ShapeHeights(Rect2 hole, float holeDropM)
    {
        if (!hole.HasArea())
        {
            return Heights;
        }
        float[] data = (float[])Heights.Clone();
        float low = MinHeightM - holeDropM;
        // 사각형 안쪽(경계 제외)에 드는 행·열 범위
        int colLo = Math.Max(Mathf.FloorToInt((hole.Position.X - Origin.X) / SpacingM) + 1, 0);
        int colHi = Math.Min(Mathf.CeilToInt((hole.End.X - Origin.X) / SpacingM) - 1, Width - 1);
        int rowLo = Math.Max(Mathf.FloorToInt((hole.Position.Y - Origin.Z) / SpacingM) + 1, 0);
        int rowHi = Math.Min(Mathf.CeilToInt((hole.End.Y - Origin.Z) / SpacingM) - 1, Height - 1);
        for (int row = rowLo; row <= rowHi; row++)
        {
            for (int col = colLo; col <= colHi; col++)
            {
                data[(row * Width) + col] = low;
            }
        }
        return data;
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

/// <summary>
/// 그리기용 메시 배열 (꼭짓점, 법선, UV, 삼각형 번호). 주 스레드 밖에서 계산하고, 주 스레드에서
/// <see cref="ToArrayMesh"/> 로 Godot 메시를 만듭니다.
/// </summary>
public sealed class MeshArrays
{
    public Vector3[] Vertices { get; init; } = [];

    public Vector3[] Normals { get; init; } = [];

    /// <summary>비어 있으면 UV 없이 만듭니다.</summary>
    public Vector2[] Uvs { get; init; } = [];

    public int[] Indices { get; init; } = [];

    /// <summary>Godot 메시를 만듭니다 (주 스레드). 꼭짓점이 없으면 면이 없는 빈 메시입니다.</summary>
    public ArrayMesh ToArrayMesh()
    {
        var mesh = new ArrayMesh();
        AddTo(mesh);
        return mesh;
    }

    /// <summary>이 배열을 mesh 의 면(surface) 하나로 더합니다 (주 스레드). 꼭짓점이 없으면 아무것도 하지 않습니다.</summary>
    public void AddTo(ArrayMesh mesh)
    {
        if (Vertices.Length == 0)
        {
            return;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Normals;
        if (Uvs.Length > 0)
        {
            arrays[(int)Mesh.ArrayType.TexUV] = Uvs;
        }
        arrays[(int)Mesh.ArrayType.Index] = Indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }
}

/// <summary>
/// 충돌 모양 한 조각: 한 변 Size 표본의 정사각 높이 (행 우선) 와 놓을 자리. 주 스레드 밖에서 계산하고, 주 스레드에서
/// <see cref="ToShape"/> 로 Godot 충돌 모양을 만듭니다.
/// </summary>
public sealed record ShapeTile(int Size, float[] Data, Transform3D Transform)
{
    public HeightMapShape3D ToShape() => new() { MapWidth = Size, MapDepth = Size, MapData = Data };
}
