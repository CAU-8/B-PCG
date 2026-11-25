using System;
using System.Collections.Generic;
using Bpcg.Core;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 굽기(Bpcg.Bake.Heightmap)가 쓴 높이맵 한 장(.bin + .json)을 읽습니다.
/// 읽은 뒤 그리기용 메시(한 장 <see cref="BuildMesh"/>, 조각으로 나눈 <see cref="BuildChunkedMesh"/>)와
/// 충돌용 <see cref="HeightMapShape3D"/> 를 만듭니다.
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

    /// <summary>
    /// 지형을 나눠 그리는 조각 한 변의 칸 수. 조각으로 나눠야 Godot 가 화면 밖 조각과 그림자 거리 밖 조각을 건너뜁니다.
    /// 끝에 남는 칸이 이 값의 절반보다 적으면 앞 조각에 붙입니다.
    /// </summary>
    public const int ChunkQuads = 64;

    /// <summary>
    /// 조각마다 미리 만들어 두는 거친 메시의 칸 건너뛰기 간격. 2 는 두 칸마다 한 줄만 써서 삼각형이 약 4분의 1이 됩니다.
    /// 조각 둘레는 늘 모든 꼭짓점을 써서, 옆 조각이 어떤 간격을 쓰든 틈이 생기지 않습니다.
    /// </summary>
    public static readonly int[] LodStrides = [2, 4, 8, 16, 32];

    /// <summary>거친 메시의 높이 오차 하한 (m). 평평한 조각도 0 이 아니게 둡니다(Godot 가 거리 0 에서 0 ÷ 0 을 하지 않게).</summary>
    public const float MinLodErrorM = 0.001f;

    /// <summary>
    /// 높이맵 가장자리에 닿은 조각의 거친 메시 전환 거리 배수. 치마(가장자리에서 아래로 내린 벽)가 있는 지형에서, 거친 메시가
    /// 가장자리 바로 안쪽에서 가장자리 높이보다 1 px 안쪽만큼 꺼지면 치마 윗부분이 가는 점선으로 드러납니다. 그래서 이 조각들은
    /// 4배 먼 거리(화면 오차 0.25 px)부터 거친 메시로 바꿉니다(GeometryInstance3D.LodBias).
    /// </summary>
    public const float EdgeLodBias = 4.0f;

    /// <summary>나눠 그리는 지형 메시: 조각 메시(행 우선)와 둘레 치마 메시(치마가 없으면 null).</summary>
    /// <param name="Chunks">조각 메시. 꼭짓점은 BuildMesh 와 같은 타일 국소 좌표라 모두 origin 에 놓습니다.</param>
    /// <param name="OnEdge">조각마다 높이맵 가장자리에 닿는지.</param>
    /// <param name="Skirt">가장자리 치마만 담은 메시.</param>
    /// <param name="Triangles">가장 고운 메시로 그릴 때의 삼각형 수 (치마 포함).</param>
    public sealed record ChunkedMesh(ArrayMesh[] Chunks, bool[] OnEdge, ArrayMesh? Skirt, long Triangles);

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
            int b = row * Width;
            for (int col = 0; col < Width; col++)
            {
                int i = b + col;
                vertices[i] = new Vector3(col * SpacingM, Heights[i], row * SpacingM);
                normals[i] = NormalAt(row, col);
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
    /// 그리기용 메시를 chunkQuads × chunkQuads 칸 조각으로 나눠 만듭니다. 꼭짓점과 법선은 <see cref="BuildMesh"/> 와 같고,
    /// 조각 경계의 꼭짓점은 양쪽 조각에 한 번씩 들어갑니다. 조각마다 <see cref="LodStrides"/> 간격의 거친 삼각형 목록을
    /// 함께 넣고, 목록마다 그 조각의 높이 오차(m)를 붙입니다. Godot 는 이 오차가 화면에서 1 px(프로젝트 설정
    /// rendering/mesh_lod/lod_change/threshold_pixels)보다 작아지는 거리부터 거친 목록으로 그립니다.
    /// skirtM &gt; 0 이면 둘레 치마를 따로 만듭니다. 조각 꼭짓점 계산은 조각마다 따로 돌아 병렬로 해도 결과가 같습니다.
    /// </summary>
    public ChunkedMesh BuildChunkedMesh(float skirtM = 0.0f, int chunkQuads = ChunkQuads)
    {
        int[] cols = ChunkEdges(Width - 1, chunkQuads);
        int[] rows = ChunkEdges(Height - 1, chunkQuads);
        int nc = cols.Length - 1;
        int n = (rows.Length - 1) * nc;
        var parts = new ChunkArrays[n];
        Parallelism.ForTasks(n, k => parts[k] = BuildChunk(rows[k / nc], rows[(k / nc) + 1], cols[k % nc], cols[(k % nc) + 1]));
        var chunks = new ArrayMesh[n];
        bool[] onEdge = new bool[n];
        long triangles = 0;
        for (int k = 0; k < n; k++)
        {
            chunks[k] = parts[k].ToMesh();
            triangles += parts[k].Indices.Length / 3;
            int row = k / nc;
            int col = k % nc;
            onEdge[k] = row == 0 || row == rows.Length - 2 || col == 0 || col == nc - 1;
        }
        ArrayMesh? skirt = null;
        if (skirtM > 0.0f)
        {
            (skirt, int skirtTriangles) = BuildSkirtMesh(skirtM);
            triangles += skirtTriangles;
        }
        return new ChunkedMesh(chunks, onEdge, skirt, triangles);
    }

    /// <summary>
    /// 칸 수 n 을 size 칸씩 나눈 조각 경계 (0, size, 2·size, …, n). 끝에 남는 조각이 size 의 절반보다 작으면 앞 조각에 붙입니다.
    /// 예: n = 3,000, size = 64 이면 경계 48개(조각 47개), 끝 조각은 56칸입니다.
    /// </summary>
    public static int[] ChunkEdges(int n, int size)
    {
        var edges = new List<int> { 0 };
        for (int at = size; at < n; at += size)
        {
            edges.Add(at);
        }
        if (edges.Count > 1 && n - edges[^1] < size / 2)
        {
            edges.RemoveAt(edges.Count - 1);
        }
        edges.Add(n);
        return [.. edges];
    }

    /// <summary>
    /// 가로 nx 칸, 세로 nz 칸 조각의 삼각형 목록 (꼭짓점 번호 = 행 · (nx + 1) + 열). stride 1 은 모든 칸을 쓰는
    /// BuildMesh 와 같은 목록입니다. stride s &gt; 1 은 s 칸마다 한 줄만 쓰는 거친 목록이고, s 는 nx 와 nz 보다 작아야 합니다.
    /// 거친 목록도 조각 둘레의 꼭짓점은 모두 써서, 옆 조각이 어떤 간격을 쓰든 경계선이 늘 같습니다.
    /// 둘레에 닿지 않은 거친 칸은 삼각형 둘, 둘레에 한 변이 닿은 칸은 그 변의 꼭짓점들을 반씩 나눠 맞은편 두 모서리에서
    /// 부채꼴로 잇고, 두 변이 닿은 귀퉁이 칸은 안쪽 모서리 하나에서 부채꼴로 잇습니다.
    /// 모든 삼각형은 위에서 볼 때 시계 방향(Godot 앞면)이고, 목록마다 조각 사각형을 겹침 없이 꼭 한 번 덮습니다.
    /// </summary>
    public static int[] LodIndices(int nx, int nz, int stride)
    {
        int w = nx + 1;
        if (stride <= 1)
        {
            int[] full = new int[nx * nz * 6];
            int k = 0;
            for (int r = 0; r < nz; r++)
            {
                for (int c = 0; c < nx; c++)
                {
                    int nw = (r * w) + c;
                    full[k] = nw;
                    full[k + 1] = nw + 1;
                    full[k + 2] = nw + w;
                    full[k + 3] = nw + 1;
                    full[k + 4] = nw + w + 1;
                    full[k + 5] = nw + w;
                    k += 6;
                }
            }
            return full;
        }
        if (stride >= Math.Min(nx, nz))
        {
            throw new ArgumentException(
                $"거친 메시 간격 stride {stride} 칸이 조각 크기 {nx} × {nz} 칸보다 작아야 합니다. stride 를 {Math.Min(nx, nz) - 1} 이하로 주세요");
        }
        int[] cs = CoarseLines(nx, stride);
        int[] rs = CoarseLines(nz, stride);
        var indices = new List<int>();
        var ring = new List<int>((4 * stride) + 4);
        void Tri(int a, int b, int c)
        {
            // 위에서 볼 때 시계 방향 (열 = x 오른쪽, 행 = z 아래쪽 화면에서 외적 > 0) 으로 맞춥니다.
            long cross = ((long)((b % w) - (a % w)) * ((c / w) - (a / w))) - ((long)((b / w) - (a / w)) * ((c % w) - (a % w)));
            indices.Add(a);
            indices.Add(cross > 0 ? b : c);
            indices.Add(cross > 0 ? c : b);
        }
        for (int ri = 0; ri < rs.Length - 1; ri++)
        {
            for (int ci = 0; ci < cs.Length - 1; ci++)
            {
                int rA = rs[ri];
                int rB = rs[ri + 1];
                int cA = cs[ci];
                int cB = cs[ci + 1];
                bool north = ri == 0;
                bool south = ri == rs.Length - 2;
                bool west = ci == 0;
                bool east = ci == cs.Length - 2;
                int nw = (rA * w) + cA;
                int ne = (rA * w) + cB;
                int sw = (rB * w) + cA;
                int se = (rB * w) + cB;
                int fine = (north ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0) + (east ? 1 : 0);
                if (fine == 0)
                {
                    Tri(nw, ne, sw);
                    Tri(ne, se, sw);
                    continue;
                }
                if (fine == 1)
                {
                    // 둘레에 닿은 변의 꼭짓점 top[0..len] 과 맞은편 두 모서리 a1 (top[0] 쪽), a2 (top[len] 쪽).
                    // top 의 앞 절반은 a1 에서, 뒤 절반은 a2 에서 부채꼴로 잇고, 가운데 꼭짓점과 a1·a2 로 삼각형 하나를 둡니다.
                    bool alongRow = north || south;
                    int line = north ? rA : south ? rB : west ? cA : cB;
                    int lo = alongRow ? cA : rA;
                    int len = alongRow ? cB - cA : rB - rA;
                    int a1 = north ? sw : south ? nw : west ? ne : nw;
                    int a2 = north ? se : south ? ne : west ? se : sw;
                    int Top(int u) => alongRow ? (line * w) + lo + u : ((lo + u) * w) + line;
                    int mid = len / 2;
                    for (int u = 0; u < mid; u++)
                    {
                        Tri(a1, Top(u), Top(u + 1));
                    }
                    Tri(a1, Top(mid), a2);
                    for (int u = mid; u < len; u++)
                    {
                        Tri(a2, Top(u), Top(u + 1));
                    }
                    continue;
                }
                // 귀퉁이 칸: 둘레를 시계 방향으로 돌며 둘레에 놓인 변의 꼭짓점을 모두 넣고, 안쪽 모서리에서 부채꼴로 잇습니다.
                ring.Clear();
                ring.Add(nw);
                for (int c = cA + 1; north && c < cB; c++)
                {
                    ring.Add((rA * w) + c);
                }
                ring.Add(ne);
                for (int r = rA + 1; east && r < rB; r++)
                {
                    ring.Add((r * w) + cB);
                }
                ring.Add(se);
                for (int c = cB - 1; south && c > cA; c--)
                {
                    ring.Add((rB * w) + c);
                }
                ring.Add(sw);
                for (int r = rB - 1; west && r > rA; r--)
                {
                    ring.Add((r * w) + cA);
                }
                int apex = !north && !west ? nw : !north && !east ? ne : !south && !east ? se : sw;
                int start = ring.IndexOf(apex);
                for (int j = 1; j < ring.Count - 1; j++)
                {
                    Tri(apex, ring[(start + j) % ring.Count], ring[(start + j + 1) % ring.Count]);
                }
            }
        }
        return [.. indices];
    }

    /// <summary>0, s, 2s, … (n 보다 작은 것) 과 n.</summary>
    private static int[] CoarseLines(int n, int stride)
    {
        var lines = new List<int>();
        for (int at = 0; at < n; at += stride)
        {
            lines.Add(at);
        }
        lines.Add(n);
        return [.. lines];
    }

    /// <summary>행 r0..r1, 열 c0..c1 (양 끝 포함) 조각의 꼭짓점·법선과 삼각형 목록.</summary>
    private ChunkArrays BuildChunk(int r0, int r1, int c0, int c1)
    {
        int nx = c1 - c0;
        int nz = r1 - r0;
        int w = nx + 1;
        var part = new ChunkArrays(new Vector3[w * (nz + 1)], new Vector3[w * (nz + 1)], LodIndices(nx, nz, 1));
        for (int r = 0; r <= nz; r++)
        {
            for (int c = 0; c <= nx; c++)
            {
                int row = r0 + r;
                int col = c0 + c;
                part.Vertices[(r * w) + c] = new Vector3(col * SpacingM, Heights[(row * Width) + col], row * SpacingM);
                part.Normals[(r * w) + c] = NormalAt(row, col);
            }
        }
        float last = 0.0f;
        foreach (int stride in LodStrides)
        {
            if (stride >= Math.Min(nx, nz))
            {
                break;
            }
            // 오차는 간격이 커질수록 커지게 맞춥니다 (Godot 는 오차가 작은 목록부터 차례로 봅니다).
            int[] lod = LodIndices(nx, nz, stride);
            float errorM = Math.Max(Math.Max(LodErrorM(r0, c0, nx, lod), MinLodErrorM), last * 1.01f);
            part.Lods.Add((errorM, lod));
            last = errorM;
        }
        return part;
    }

    /// <summary>
    /// 거친 삼각형 목록 lodIndices 로 그린 면이 조각 (r0, c0, 가로 nx 칸) 의 표본 높이와 다른 정도의 최댓값 (m).
    /// 삼각형마다 덮는 표본을 한 줄씩 훑어, 삼각형 평면의 높이와 표본 높이를 견줍니다.
    /// </summary>
    private float LodErrorM(int r0, int c0, int nx, int[] lodIndices)
    {
        int w = nx + 1;
        double worst = 0.0;
        for (int t = 0; t < lodIndices.Length; t += 3)
        {
            int r1 = lodIndices[t] / w;
            int k1 = lodIndices[t] % w;
            int r2 = lodIndices[t + 1] / w;
            int k2 = lodIndices[t + 1] % w;
            int r3 = lodIndices[t + 2] / w;
            int k3 = lodIndices[t + 2] % w;
            double h1 = H(r0 + r1, c0 + k1);
            double det = ((double)(r2 - r1) * (k3 - k1)) - ((double)(k2 - k1) * (r3 - r1));
            if (det == 0.0)
            {
                continue;
            }
            // 평면 h = h1 + dhDr · (행 − r1) + dhDk · (열 − k1)
            double dh2 = H(r0 + r2, c0 + k2) - h1;
            double dh3 = H(r0 + r3, c0 + k3) - h1;
            double dhDr = ((dh2 * (k3 - k1)) - ((k2 - k1) * dh3)) / det;
            double dhDk = (((r2 - r1) * dh3) - (dh2 * (r3 - r1))) / det;
            for (int r = Math.Min(r1, Math.Min(r2, r3)); r <= Math.Max(r1, Math.Max(r2, r3)); r++)
            {
                // 이 행이 삼각형과 겹치는 열 범위
                double lo = double.PositiveInfinity;
                double hi = double.NegativeInfinity;
                EdgeSpan(r, r1, k1, r2, k2, ref lo, ref hi);
                EdgeSpan(r, r2, k2, r3, k3, ref lo, ref hi);
                EdgeSpan(r, r3, k3, r1, k1, ref lo, ref hi);
                for (int k = (int)Math.Ceiling(lo - 1e-9); k <= (int)Math.Floor(hi + 1e-9); k++)
                {
                    double lod = h1 + (dhDr * (r - r1)) + (dhDk * (k - k1));
                    worst = Math.Max(worst, Math.Abs(H(r0 + r, c0 + k) - lod));
                }
            }
        }
        return (float)worst;
    }

    /// <summary>행 r 이 변 (ra, ka)–(rb, kb) 와 만나는 열을 [lo, hi] 에 넣습니다. 수평 변이 그 행 위에 있으면 두 끝을 넣습니다.</summary>
    private static void EdgeSpan(int r, int ra, int ka, int rb, int kb, ref double lo, ref double hi)
    {
        if (ra == rb)
        {
            if (ra == r)
            {
                lo = Math.Min(lo, Math.Min(ka, kb));
                hi = Math.Max(hi, Math.Max(ka, kb));
            }
            return;
        }
        if ((long)(r - ra) * (r - rb) <= 0)
        {
            double k = ka + ((double)(r - ra) * (kb - ka) / (rb - ra));
            lo = Math.Min(lo, k);
            hi = Math.Max(hi, k);
        }
    }

    /// <summary>둘레 치마만 담은 메시 (양면). 반환: 메시와 삼각형 수.</summary>
    private (ArrayMesh Mesh, int Triangles) BuildSkirtMesh(float skirtM)
    {
        int[] ring = Perimeter();
        int n = ring.Length;
        var vertices = new Vector3[2 * n];
        var normals = new Vector3[2 * n];
        for (int j = 0; j < n; j++)
        {
            int row = ring[j] / Width;
            int col = ring[j] % Width;
            vertices[j] = new Vector3(col * SpacingM, Heights[ring[j]], row * SpacingM);
            vertices[n + j] = vertices[j] - new Vector3(0.0f, skirtM, 0.0f);
            normals[j] = NormalAt(row, col);
            normals[n + j] = normals[j];
        }
        int[] indices = new int[n * 12];
        int k = 0;
        for (int j = 0; j < n; j++)
        {
            int a = j;
            int b = (j + 1) % n;
            int aLow = n + j;
            int bLow = n + ((j + 1) % n);
            foreach (int v in (int[])[a, b, aLow, b, bLow, aLow, a, aLow, b, b, aLow, bLow])
            {
                indices[k++] = v;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return (mesh, n * 4);
    }

    private float H(int row, int col) => Heights[(row * Width) + col];

    /// <summary>표본 (row, col) 의 법선. 양옆 표본의 높이 차로 기울기를 잽니다 (가장자리는 한쪽 차).</summary>
    private Vector3 NormalAt(int row, int col)
    {
        int rowN = Math.Max(row - 1, 0);
        int rowS = Math.Min(row + 1, Height - 1);
        int colW = Math.Max(col - 1, 0);
        int colE = Math.Min(col + 1, Width - 1);
        int b = row * Width;
        float dxM = (colE - colW) * SpacingM;
        float dzM = (rowS - rowN) * SpacingM;
        float dhDx = (Heights[b + colE] - Heights[b + colW]) / dxM;
        float dhDz = (Heights[(rowS * Width) + col] - Heights[(rowN * Width) + col]) / dzM;
        return new Vector3(-dhDx, 1.0f, -dhDz).Normalized();
    }

    /// <summary>조각 하나의 배열. 꼭짓점 계산은 다른 스레드에서 하고, Godot 메시는 ToMesh 로 주 스레드에서 만듭니다.</summary>
    private sealed class ChunkArrays(Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        public Vector3[] Vertices { get; } = vertices;

        public Vector3[] Normals { get; } = normals;

        /// <summary>가장 고운 삼각형 목록.</summary>
        public int[] Indices { get; } = indices;

        /// <summary>거친 삼각형 목록과 그 높이 오차 (m), 오차가 작은 것부터.</summary>
        public List<(float ErrorM, int[] Indices)> Lods { get; } = [];

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = Normals;
            arrays[(int)Mesh.ArrayType.Index] = Indices;
            var lods = new Godot.Collections.Dictionary();
            foreach ((float errorM, int[] lodIndices) in Lods)
            {
                lods[errorM] = lodIndices;
            }
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, lods: lods);
            return mesh;
        }
    }

    /// <summary>
    /// 충돌 모양을 만듭니다. 놓을 자리는 ShapeTransform() 으로 얻습니다. hole 의 넓이가 0 보다 크면 그 사각형
    /// (국소 X, Z) 안 표본을 최저 높이보다 holeDropM 아래로 내려, 더 고운 지형과 충돌면이 겹치지 않게 합니다.
    /// </summary>
    public HeightMapShape3D BuildShape(Rect2 hole = default, float holeDropM = 50.0f)
    {
        var shape = new HeightMapShape3D { MapWidth = Width, MapDepth = Height };
        if (hole.HasArea())
        {
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
            shape.MapData = data;
        }
        else
        {
            shape.MapData = Heights;
        }
        return shape;
    }

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
