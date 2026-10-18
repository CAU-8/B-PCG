using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.IO;
using Bpcg.Volume;

namespace Bpcg.Bake;

/// <summary>히어로 국소 좌표 → 엔진 좌표 변환 (EngineFrame). X = 동 − cx, Y = 고도 − y_offset, Z = −(북 − cy).</summary>
public sealed record EngineFrame(double Cx, double Cy, double YOffset)
{
    /// <summary>(M, 3) 국소 (동, 북, 위) → (M, 3) 엔진 (X, Y, Z).</summary>
    public double[] ToEngine(double[] pts)
    {
        double[] o = new double[pts.Length];
        for (int i = 0; i < pts.Length / 3; i++)
        {
            o[i * 3] = pts[i * 3] - Cx;
            o[(i * 3) + 1] = pts[(i * 3) + 2] - YOffset;
            o[(i * 3) + 2] = -(pts[(i * 3) + 1] - Cy);
        }
        return o;
    }

    /// <summary>(M, 3) 엔진 (X, Y, Z) → (M, 3) 국소 (동, 북, 위).</summary>
    public double[] ToLocal(double[] pts)
    {
        double[] o = new double[pts.Length];
        for (int i = 0; i < pts.Length / 3; i++)
        {
            o[i * 3] = pts[i * 3] + Cx;
            o[(i * 3) + 1] = Cy - pts[(i * 3) + 2];
            o[(i * 3) + 2] = pts[(i * 3) + 1] + YOffset;
        }
        return o;
    }

    public OrderedDictionary<string, object?> AsDict() => new()
    {
        ["axes"] = "x_east_y_up_z_south",
        ["units"] = "m",
        ["local_origin_east_north_m"] = new List<object?> { Cx, Cy },
        ["y_offset_m"] = YOffset,
        ["rule"] = "X = east - cx, Y = elevation - y_offset, Z = -(north - cy)",
    };
}

/// <summary>엔진 좌표 동굴 메시: 꼭짓점 (V, 3), 삼각형 (F, 3), 꼭짓점 법선 (V, 3), COLOR_0 (V, 4) uint8.</summary>
public sealed class CaveMesh
{
    public required double[] Vertices { get; init; }

    public required int[] Faces { get; init; }

    public required double[] Normals { get; init; }

    public required byte[] Colors { get; init; }

    public int VertexCount => Vertices.Length / 3;

    public int FaceCount => Faces.Length / 3;
}

/// <summary>
/// 동굴 메시: 3D 샘플 함수의 동굴 SDF 를 조각별로 등위면 추출해 glb 로 굽습니다 (src/bpcg/bake/mesh.py).
/// </summary>
/// <remarks>
/// TODO(port): Python 은 scikit-image 의 Lewiner marching cubes 를 씁니다(핵심이 Cython 이라 소스가 없음).
/// 여기서는 같은 격자에서 Kuhn 분할 marching tetrahedra 로 d_cave = 0 등위면을 뽑습니다. 등위면은 같지만
/// 삼각형 수·꼭짓점 배치는 다릅니다. glb 의 JSON 배치도 trimesh 와 바이트까지 같지 않습니다.
/// </remarks>
public static class Mesh
{
    public const int TileVoxels = 64;
    public const int BandPadVoxels = 2;
    public const double SdfClipVoxels = 4.0;
    public const double ColorProbeVoxels = 0.5;

    private static List<(double Lo, double Hi)> MergeIntervals(List<(double Lo, double Hi)> iv)
    {
        var sorted = iv.OrderBy(t => t.Lo).ThenBy(t => t.Hi).ToList();
        var o = new List<(double Lo, double Hi)>();
        foreach ((double lo, double hi) in sorted)
        {
            if (o.Count > 0 && lo <= o[^1].Hi)
            {
                o[^1] = (o[^1].Lo, Math.Max(o[^1].Hi, hi));
            }
            else
            {
                o.Add((lo, hi));
            }
        }
        return o;
    }

    /// <summary>수평 상자 안 동굴이 있을 수 있는 높이 구간들 (cave_bands).</summary>
    public static List<(double Lo, double Hi)> CaveBands(HeroVolume volume, (double XMin, double XMax, double YMin, double YMax) bbox, double padM)
    {
        double dx = volume.Dx;
        double r = volume.RPass;
        int i0 = Math.Max((int)Math.Floor(((bbox.XMin - volume.X0) / dx) - 0.5) - 1, 0);
        int i1 = Math.Min((int)Math.Ceiling(((bbox.XMax - volume.X0) / dx) - 0.5) + 1, volume.Nx - 1);
        int j0 = Math.Max((int)Math.Floor(((volume.Y0 - bbox.YMax) / dx) - 0.5) - 1, 0);
        int j1 = Math.Min((int)Math.Ceiling(((volume.Y0 - bbox.YMin) / dx) - 0.5) + 1, volume.Ny - 1);
        var iv = new List<(double, double)>();
        int n = volume.Ny * volume.Nx;
        if (i0 <= i1 && j0 <= j1)
        {
            for (int k = 0; k < volume.NLevels; k++)
            {
                double mn = double.PositiveInfinity;
                double mx = double.NegativeInfinity;
                bool any = false;
                for (int j = j0; j <= j1; j++)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        double v = volume.CaveLevels[(k * n) + (j * volume.Nx) + i];
                        if (double.IsFinite(v))
                        {
                            any = true;
                            mn = Math.Min(mn, v);
                            mx = Math.Max(mx, v);
                        }
                    }
                }
                if (any)
                {
                    iv.Add((mn - r - padM, mx + r + padM));
                }
            }
        }
        double[] a = volume.CapA;
        double[] b = volume.CapB;
        for (int e = 0; e < volume.NCapsules; e++)
        {
            double loX = Math.Min(a[e * 3], b[e * 3]) - r;
            double hiX = Math.Max(a[e * 3], b[e * 3]) + r;
            double loY = Math.Min(a[(e * 3) + 1], b[(e * 3) + 1]) - r;
            double hiY = Math.Max(a[(e * 3) + 1], b[(e * 3) + 1]) + r;
            if (hiX >= bbox.XMin && loX <= bbox.XMax && hiY >= bbox.YMin && loY <= bbox.YMax)
            {
                iv.Add((Math.Min(a[(e * 3) + 2], b[(e * 3) + 2]) - r - padM, Math.Max(a[(e * 3) + 2], b[(e * 3) + 2]) + r + padM));
            }
        }
        return MergeIntervals(iv);
    }

    // Kuhn 분할: 정육면체 꼭짓점 (0,0,0)~(1,1,1) 를 축 순서마다 하나씩, 사면체 6개
    private static readonly int[][] Tets =
    [
        [0, 1, 2, 6], [0, 3, 2, 6], [0, 3, 7, 6], [0, 4, 7, 6], [0, 4, 5, 6], [0, 1, 5, 6],
    ];

    private static readonly (int Dx, int Dy, int Dz)[] Corner =
    [
        (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1),
    ];

    /// <summary>
    /// 격자 f (nx, ny, nz) [i, j, k] 행 우선의 0 등위면 (marching tetrahedra). 삼각형 앞면은 값이 커지는 쪽(skimage 와 같은 약속).
    /// 반환 꼭짓점은 (i·v, j·v, k·v).
    /// </summary>
    internal static (List<double> Verts, List<int> Faces) IsoSurface(double[] f, int nx, int ny, int nz, double v)
    {
        var verts = new List<double>();
        var faces = new List<int>();
        // 모서리마다 처음 만든 꼭짓점 번호. Kuhn 분할의 모서리는 낮은 꼭짓점에서 높은 꼭짓점으로 가는 방향이 모두
        // 성분 0·1 의 7가지라, (낮은 꼭짓점 번호·8 + 방향) 하나로 (min, max) 꼭짓점 쌍을 대신합니다.
        var edgeVert = new Dictionary<long, int>();
        long Id(int i, int j, int k) => (((long)i * ny) + j) * nz + k;
        int Vert(int ia, int ja, int ka, int ib, int jb, int kb)
        {
            long a = Id(ia, ja, ka);
            long b = Id(ib, jb, kb);
            long key = a < b
                ? (a * 8) + (((ib - ia) << 2) | ((jb - ja) << 1) | (kb - ka))
                : (b * 8) + (((ia - ib) << 2) | ((ja - jb) << 1) | (ka - kb));
            if (edgeVert.TryGetValue(key, out int idx))
            {
                return idx;
            }
            double fa = f[a];
            double fb = f[b];
            double t = fa / (fa - fb);
            idx = verts.Count / 3;
            verts.Add((ia + (t * (ib - ia))) * v);
            verts.Add((ja + (t * (jb - ja))) * v);
            verts.Add((ka + (t * (kb - ka))) * v);
            edgeVert[key] = idx;
            return idx;
        }
        void AddTri(int p, int q, int r, (double X, double Y, double Z) outside)
        {
            int pi = p * 3;
            int qi = q * 3;
            int ri = r * 3;
            double ux = verts[qi] - verts[pi];
            double uy = verts[qi + 1] - verts[pi + 1];
            double uz = verts[qi + 2] - verts[pi + 2];
            double wx = verts[ri] - verts[pi];
            double wy = verts[ri + 1] - verts[pi + 1];
            double wz = verts[ri + 2] - verts[pi + 2];
            double nxv = (uy * wz) - (uz * wy);
            double nyv = (uz * wx) - (ux * wz);
            double nzv = (ux * wy) - (uy * wx);
            if (nxv == 0.0 && nyv == 0.0 && nzv == 0.0)
            {
                return; // allow_degenerate=False
            }
            double cx = (verts[pi] + verts[qi] + verts[ri]) / 3.0;
            double cy = (verts[pi + 1] + verts[qi + 1] + verts[ri + 1]) / 3.0;
            double cz = (verts[pi + 2] + verts[qi + 2] + verts[ri + 2]) / 3.0;
            double dot = (nxv * (outside.X - cx)) + (nyv * (outside.Y - cy)) + (nzv * (outside.Z - cz));
            if (dot >= 0)
            {
                faces.Add(p);
                faces.Add(q);
                faces.Add(r);
            }
            else
            {
                faces.Add(p);
                faces.Add(r);
                faces.Add(q);
            }
        }
        int[] ci = new int[4];
        int[] cj = new int[4];
        int[] ck = new int[4];
        int[] ins = new int[4];
        int[] outs = new int[4];
        for (int i = 0; i + 1 < nx; i++)
        {
            for (int j = 0; j + 1 < ny; j++)
            {
                for (int k = 0; k + 1 < nz; k++)
                {
                    // 정육면체 꼭짓점 8개의 부호를 한 번만 읽습니다. 모두 같은 쪽이면 사면체 6개 모두 건너뛰므로 바로 다음 칸으로.
                    int cubeIn = 0;
                    for (int q = 0; q < 8; q++)
                    {
                        (int ddx, int ddy, int ddz) = Corner[q];
                        if (f[Id(i + ddx, j + ddy, k + ddz)] < 0.0)
                        {
                            cubeIn |= 1 << q;
                        }
                    }
                    if (cubeIn == 0 || cubeIn == 0xFF)
                    {
                        continue;
                    }
                    foreach (int[] tet in Tets)
                    {
                        int maskIn = 0;
                        for (int q = 0; q < 4; q++)
                        {
                            (int ddx, int ddy, int ddz) = Corner[tet[q]];
                            ci[q] = i + ddx;
                            cj[q] = j + ddy;
                            ck[q] = k + ddz;
                            if ((cubeIn & (1 << tet[q])) != 0)
                            {
                                maskIn |= 1 << q;
                            }
                        }
                        int nIn = System.Numerics.BitOperations.PopCount((uint)maskIn);
                        if (nIn == 0 || nIn == 4)
                        {
                            continue;
                        }
                        int nOut = 0;
                        nIn = 0;
                        for (int q = 0; q < 4; q++)
                        {
                            if ((maskIn & (1 << q)) != 0)
                            {
                                ins[nIn++] = q;
                            }
                            else
                            {
                                outs[nOut++] = q;
                            }
                        }
                        double ox = 0;
                        double oy = 0;
                        double oz = 0;
                        for (int o = 0; o < nOut; o++)
                        {
                            int q = outs[o];
                            ox += ci[q] * v;
                            oy += cj[q] * v;
                            oz += ck[q] * v;
                        }
                        (double, double, double) outside = (ox / nOut, oy / nOut, oz / nOut);
                        int E(int qa, int qb) => Vert(ci[qa], cj[qa], ck[qa], ci[qb], cj[qb], ck[qb]);
                        if (nIn == 1 || nIn == 3)
                        {
                            int lone = nIn == 1 ? ins[0] : outs[0];
                            int[] rest = nIn == 1 ? outs : ins;
                            AddTri(E(lone, rest[0]), E(lone, rest[1]), E(lone, rest[2]), outside);
                        }
                        else
                        {
                            int p0 = E(ins[0], outs[0]);
                            int p1 = E(ins[0], outs[1]);
                            int p2 = E(ins[1], outs[1]);
                            int p3 = E(ins[1], outs[0]);
                            AddTri(p0, p1, p2, outside);
                            AddTri(p0, p2, p3, outside);
                        }
                    }
                }
            }
        }
        return (verts, faces);
    }

    private sealed class TileMesh
    {
        public bool HasBands;
        public long Voxels;
        public List<(double Ox, double Oy, double Oz, List<double> Verts, List<int> Faces)> Bands = [];
        public ExceptionDispatchInfo? Error;
    }

    // 조각 하나의 띠별 등위면 (cave_surface 의 조각 몸체). 꼭짓점은 조각 원점 기준 국소 좌표, 원점은 Ox·Oy·Oz.
    private static TileMesh CaveTile(HeroVolume volume, double[] xs, double[] ys, (int Ia, int Ib, int Ja, int Jb) tile,
        double v, double pad, double clip)
    {
        (int ia, int ib, int ja, int jb) = tile;
        var r = new TileMesh();
        List<(double Lo, double Hi)> bands = CaveBands(volume, (xs[ia], xs[ib], ys[ja], ys[jb]), pad);
        if (bands.Count == 0)
        {
            return r;
        }
        r.HasBands = true;
        int ntx = ib - ia + 1;
        int nty = jb - ja + 1;
        double[] cx = new double[ntx * nty];
        double[] cy = new double[ntx * nty];
        for (int i = 0; i < ntx; i++)
        {
            for (int j = 0; j < nty; j++)
            {
                cx[(i * nty) + j] = xs[ia + i];
                cy[(i * nty) + j] = ys[ja + j];
            }
        }
        foreach ((double lo, double hi) in bands)
        {
            int k0 = (int)Math.Floor(lo / v);
            int k1 = (int)Math.Ceiling(hi / v);
            if (k1 - k0 < 2)
            {
                k1 = k0 + 2;
            }
            int nz = k1 - k0 + 1;
            double[] up = new double[cx.Length * nz];
            for (int c = 0; c < cx.Length; c++)
            {
                for (int k = 0; k < nz; k++)
                {
                    up[(c * nz) + k] = (k0 + k) * v;
                }
            }
            double[] dc = (double[])volume.EvaluateGrid(cx, cy, up, nz, double.PositiveInfinity, "d_cave")["d_cave"];
            double fmin = double.PositiveInfinity;
            double fmax = double.NegativeInfinity;
            for (int q = 0; q < dc.Length; q++)
            {
                dc[q] = Numerics.NpMath.Clip(dc[q], -clip, clip);
                fmin = Math.Min(fmin, dc[q]);
                fmax = Math.Max(fmax, dc[q]);
            }
            r.Voxels += dc.Length;
            if (fmin >= 0.0 || fmax <= 0.0)
            {
                continue;
            }
            (List<double> vt, List<int> fc) = IsoSurface(dc, ntx, nty, nz, v);
            if (fc.Count == 0)
            {
                continue;
            }
            r.Bands.Add((xs[ia], ys[ja], k0 * v, vt, fc));
        }
        return r;
    }

    // 삼각형이 쓰는 꼭짓점 번호를 오름차순·중복 없이 (Distinct().OrderBy() 와 같은 결과를 표시 배열 한 번으로).
    private static int[] SortedUnique(int[] ids, int nVerts)
    {
        bool[] seen = new bool[nVerts];
        int count = 0;
        for (int q = 0; q < ids.Length; q++)
        {
            if (!seen[ids[q]])
            {
                seen[ids[q]] = true;
                count++;
            }
        }
        int[] used = new int[count];
        int m = 0;
        for (int c = 0; c < nVerts; c++)
        {
            if (seen[c])
            {
                used[m++] = c;
            }
        }
        return used;
    }

    /// <summary>
    /// 회랑 rect 안 동굴 벽 삼각형 (cave_surface, 국소 좌표). 반환: (verts (V, 3), faces (F, 3), diag).
    /// </summary>
    public static (double[] Verts, int[] Faces, OrderedDictionary<string, object?> Diag) CaveSurface(
        HeroVolume volume, (double XMin, double XMax, double YMin, double YMax) rect, double voxelM, Action<string>? log = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        double v = voxelM;
        if (!(double.IsFinite(v) && v > 0))
        {
            throw new ArgumentException($"voxel_m 은 0 보다 커야 합니다: {voxelM}");
        }
        int nxv = (int)Math.Floor(((rect.XMax - rect.XMin) / v) + 1e-9) + 1;
        int nyv = (int)Math.Floor(((rect.YMax - rect.YMin) / v) + 1e-9) + 1;
        double[] xs = Enumerable.Range(0, nxv).Select(i => rect.XMin + (i * v)).ToArray();
        double[] ys = Enumerable.Range(0, nyv).Select(j => rect.YMin + (j * v)).ToArray();
        double pad = BandPadVoxels * v;
        double clip = SdfClipVoxels * v;
        // 조각마다 (띠별 꼭짓점·삼각형) 을 따로 만든 뒤 조각 순서대로 이어 붙입니다. 조각끼리는 서로 독립이라
        // 병렬로 만들어도 이어 붙인 결과는 한 조각씩 차례로 만든 것과 같습니다.
        var tileStarts = new List<(int Ia, int Ib, int Ja, int Jb)>();
        for (int ia = 0; ia < Math.Max(nxv - 1, 1); ia += TileVoxels)
        {
            int ib = Math.Min(ia + TileVoxels, nxv - 1);
            for (int ja = 0; ja < Math.Max(nyv - 1, 1); ja += TileVoxels)
            {
                tileStarts.Add((ia, ib, ja, Math.Min(ja + TileVoxels, nyv - 1)));
            }
        }
        var results = new TileMesh[tileStarts.Count];
        Parallelism.ForTasks(tileStarts.Count, t =>
        {
            try
            {
                results[t] = CaveTile(volume, xs, ys, tileStarts[t], v, pad, clip);
            }
            catch (Exception e)
            {
                results[t] = new TileMesh { Error = ExceptionDispatchInfo.Capture(e) };
            }
        });
        // 조각 순서대로 이어 붙입니다. 전체 크기를 먼저 세어 꼭 맞는 배열에 바로 쓰고(목록을 두 배씩 늘리고 다시 복사하지 않음),
        // 옮긴 조각 결과는 곧바로 놓습니다. 한 조각씩 돌 때처럼 앞 조각의 오류가 먼저 나갑니다.
        long tiles = 0;
        long tilesWithCaves = 0;
        long voxels = 0;
        long totalVerts = 0;
        long totalFaces = 0;
        foreach (TileMesh r in results)
        {
            r.Error?.Throw();
            foreach ((double _, double _, double _, List<double> vt, List<int> fc) in r.Bands)
            {
                totalVerts += vt.Count / 3;
                totalFaces += fc.Count / 3;
            }
        }
        double[] verts = new double[totalVerts * 3];
        // 꼭짓점 번호는 int 로 둡니다. 꼭짓점 배열(꼭짓점 수 × 3 실수)이 .NET 배열 길이 한계(약 21억) 안에 들어야 하므로
        // 번호는 늘 int 범위 안입니다.
        int[] faces = new int[totalFaces * 3];
        int nVert = 0;
        long vAt = 0;
        long fAt = 0;
        for (int t = 0; t < results.Length; t++)
        {
            TileMesh r = results[t];
            results[t] = null!;
            tiles++;
            voxels += r.Voxels;
            if (r.HasBands)
            {
                foreach ((double ox, double oy, double oz, List<double> vt, List<int> fc) in r.Bands)
                {
                    for (int q = 0; q < vt.Count; q += 3)
                    {
                        verts[vAt++] = vt[q] + ox;
                        verts[vAt++] = vt[q + 1] + oy;
                        verts[vAt++] = vt[q + 2] + oz;
                    }
                    // 값이 커지는 쪽(동굴 밖)을 앞면으로 둔 삼각형을 뒤집어 빈 곳 쪽으로.
                    for (int q = 0; q < fc.Count; q += 3)
                    {
                        faces[fAt++] = fc[q] + nVert;
                        faces[fAt++] = fc[q + 2] + nVert;
                        faces[fAt++] = fc[q + 1] + nVert;
                    }
                    nVert += vt.Count / 3;
                }
                tilesWithCaves++;
            }
            (int _, int ibTile, int jaTile, int _) = tileStarts[t];
            if (jaTile + TileVoxels >= Math.Max(nyv - 1, 1))
            {
                log?.Invoke($"[동굴 메시] 열 {ibTile}/{nxv - 1} 조각 끝, 꼭짓점 {nVert}");
            }
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["tiles"] = tiles,
            ["tiles_with_caves"] = tilesWithCaves,
            ["voxels"] = voxels,
        };
        if (verts.Length == 0)
        {
            diag["faces_raw"] = 0L;
            diag["faces_kept"] = 0L;
            diag["seconds"] = Stopwatch.GetElapsedTime(tAll).TotalSeconds;
            return ([], [], diag);
        }
        diag["faces_raw"] = (long)(faces.Length / 3);
        int nf = faces.Length / 3;
        // 무게중심의 d1 < 0 인 삼각형만 남깁니다. 무게중심은 덩어리마다 만들고 d1 만 평가합니다
        // (점마다 값은 모두 한꺼번에 평가할 때와 같음).
        bool[] keepFace = new bool[nf];
        int nKept = 0;
        double[] cen = new double[Math.Min(nf, HeroVolume.ChunkPoints) * 3];
        for (int t0 = 0; t0 < nf; t0 += HeroVolume.ChunkPoints)
        {
            int len = Math.Min(HeroVolume.ChunkPoints, nf - t0);
            double[] chunk = len * 3 == cen.Length ? cen : new double[len * 3];
            for (int i = 0; i < len; i++)
            {
                int t = t0 + i;
                for (int ax = 0; ax < 3; ax++)
                {
                    // verts[faces].mean(axis=1): 꼭짓점 세 개의 np.mean
                    double a = verts[((long)faces[t * 3] * 3) + ax];
                    double b = verts[((long)faces[(t * 3) + 1] * 3) + ax];
                    double c = verts[((long)faces[(t * 3) + 2] * 3) + ax];
                    chunk[(i * 3) + ax] = (0.0 + a + b + c) / 3.0;
                }
            }
            double[] d1 = volume.EvaluateFields(chunk, VolumeField.D1).D1;
            for (int i = 0; i < len; i++)
            {
                if (d1[i] < 0.0)
                {
                    keepFace[t0 + i] = true;
                    nKept++;
                }
            }
        }
        diag["faces_kept"] = (long)nKept;
        // 남긴 삼각형이 쓰는 꼭짓점을 번호 순서대로 새 번호로 (SortedUnique 와 같은 결과)
        int nv = verts.Length / 3;
        int[] remap = new int[nv];
        Array.Fill(remap, -1);
        for (int t = 0; t < nf; t++)
        {
            if (keepFace[t])
            {
                remap[faces[t * 3]] = 0;
                remap[faces[(t * 3) + 1]] = 0;
                remap[faces[(t * 3) + 2]] = 0;
            }
        }
        int nUsed = 0;
        for (int q = 0; q < nv; q++)
        {
            if (remap[q] == 0)
            {
                remap[q] = nUsed++;
            }
        }
        double[] vOut = new double[(long)nUsed * 3];
        for (int q = 0; q < nv; q++)
        {
            if (remap[q] >= 0)
            {
                Array.Copy(verts, (long)q * 3, vOut, (long)remap[q] * 3, 3);
            }
        }
        int[] fOut = new int[nKept * 3];
        int o = 0;
        for (int t = 0; t < nf; t++)
        {
            if (keepFace[t])
            {
                fOut[o++] = remap[faces[t * 3]];
                fOut[o++] = remap[faces[(t * 3) + 1]];
                fOut[o++] = remap[faces[(t * 3) + 2]];
            }
        }
        diag["seconds"] = Stopwatch.GetElapsedTime(tAll).TotalSeconds;
        return (vOut, fOut, diag);
    }

    /// <summary>
    /// 국소 꼭짓점·삼각형 → 엔진 좌표 메시 (build_mesh): 같은 자리 꼭짓점을 붙이고(소수 8자리), 각도 가중 꼭짓점 법선,
    /// 색은 법선 반대(암석) 쪽 0.5 칸 점의 재질.
    /// </summary>
    public static CaveMesh BuildMesh(double[] vertsLocal, int[] faces, HeroVolume volume, EngineFrame frame, double voxelM)
    {
        double[]? ve = frame.ToEngine(vertsLocal);
        // merge_vertices: 소수 8자리로 반올림한 자리가 같으면 한 꼭짓점 (처음 나온 순서로 번호)
        int nv = ve.Length / 3;
        var key = new Dictionary<(long, long, long), int>(nv);
        int[]? map = new int[nv];
        double[] merged = new double[ve.Length];
        int mergedCount = 0;
        for (int i = 0; i < nv; i++)
        {
            (long, long, long) k = (
                (long)Math.Round(ve[i * 3] * 1e8), (long)Math.Round(ve[(i * 3) + 1] * 1e8), (long)Math.Round(ve[(i * 3) + 2] * 1e8));
            if (!key.TryGetValue(k, out int idx))
            {
                idx = mergedCount / 3;
                key[k] = idx;
                merged[mergedCount++] = ve[i * 3];
                merged[mergedCount++] = ve[(i * 3) + 1];
                merged[mergedCount++] = ve[(i * 3) + 2];
            }
            map[i] = idx;
        }
        // 다 쓴 큰 배열은 바로 놓습니다 (한 번만 도는 메서드라 지역 변수가 끝까지 살아 있을 수 있음).
        ve = null;
        key = null;
        int[] f2 = new int[faces.Length];
        for (int q = 0; q < faces.Length; q++)
        {
            f2[q] = map[faces[q]];
        }
        map = null;
        // remove_unreferenced_vertices
        int[] used = SortedUnique(f2, mergedCount / 3);
        int[] remap = new int[mergedCount / 3];
        Array.Fill(remap, -1);
        double[] verts = new double[used.Length * 3];
        for (int q = 0; q < used.Length; q++)
        {
            remap[used[q]] = q;
            verts[q * 3] = merged[used[q] * 3];
            verts[(q * 3) + 1] = merged[(used[q] * 3) + 1];
            verts[(q * 3) + 2] = merged[(used[q] * 3) + 2];
        }
        merged = null!;
        int[] fFinal = new int[f2.Length];
        for (int q = 0; q < f2.Length; q++)
        {
            fFinal[q] = remap[f2[q]];
        }
        f2 = null!;
        double[] normals = VertexNormals(verts, fFinal);
        double[] probe = new double[verts.Length];
        for (int q = 0; q < verts.Length; q++)
        {
            probe[q] = verts[q] - (ColorProbeVoxels * voxelM * normals[q]);
        }
        byte[] mat = volume.EvaluateFields(frame.ToLocal(probe), VolumeField.SolidMaterial).SolidMaterial;
        byte[] rgba = new byte[mat.Length * 4];
        for (int q = 0; q < mat.Length; q++)
        {
            int m = Math.Min((int)mat[q], Rocks.NRocks - 1);
            rgba[q * 4] = Rocks.ColorRgb[m * 3];
            rgba[(q * 4) + 1] = Rocks.ColorRgb[(m * 3) + 1];
            rgba[(q * 4) + 2] = Rocks.ColorRgb[(m * 3) + 2];
            rgba[(q * 4) + 3] = mat[q];
        }
        return new CaveMesh { Vertices = verts, Faces = fFinal, Normals = normals, Colors = rgba };
    }

    // trimesh weighted_vertex_normals: 면 법선을 꼭짓점 각도로 가중해 더한 뒤 정규화.
    // 삼각형마다 (꼭짓점 셋의 각도 × 면 법선) 을 덩어리로 나눠 병렬로 구하고, 꼭짓점에 더하는 일은 삼각형 순서대로 한 줄로 합니다.
    // 더하는 순서가 예전과 같아 값이 비트까지 같습니다.
    private const int NormalChunkFaces = 1 << 18;

    private static double[] VertexNormals(double[] v, int[] f)
    {
        double[] acc = new double[v.Length];
        int nf = f.Length / 3;
        int chunk = Math.Min(nf, NormalChunkFaces);
        double[] contrib = new double[chunk * 9];
        bool[] skip = new bool[chunk];
        for (int t0 = 0; t0 < nf; t0 += NormalChunkFaces)
        {
            int len = Math.Min(NormalChunkFaces, nf - t0);
            Parallelism.ForChunks(0, len, 4096, (lo, hi) =>
            {
                Span<double> p = stackalloc double[9];
                for (int k = lo; k < hi; k++)
                {
                    int t = t0 + k;
                    for (int q = 0; q < 3; q++)
                    {
                        v.AsSpan((int)(f[(t * 3) + q] * 3), 3).CopyTo(p[(q * 3)..]);
                    }
                    double ux = p[3] - p[0];
                    double uy = p[4] - p[1];
                    double uz = p[5] - p[2];
                    double wx = p[6] - p[0];
                    double wy = p[7] - p[1];
                    double wz = p[8] - p[2];
                    double nx = (uy * wz) - (uz * wy);
                    double ny = (uz * wx) - (ux * wz);
                    double nz = (ux * wy) - (uy * wx);
                    double nn = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                    if (nn == 0)
                    {
                        skip[k] = true;
                        continue;
                    }
                    skip[k] = false;
                    nx /= nn;
                    ny /= nn;
                    nz /= nn;
                    for (int q = 0; q < 3; q++)
                    {
                        int a = q * 3;
                        int b = ((q + 1) % 3) * 3;
                        int c = ((q + 2) % 3) * 3;
                        double e1x = p[b] - p[a];
                        double e1y = p[b + 1] - p[a + 1];
                        double e1z = p[b + 2] - p[a + 2];
                        double e2x = p[c] - p[a];
                        double e2y = p[c + 1] - p[a + 1];
                        double e2z = p[c + 2] - p[a + 2];
                        double l1 = Math.Sqrt((e1x * e1x) + (e1y * e1y) + (e1z * e1z));
                        double l2 = Math.Sqrt((e2x * e2x) + (e2y * e2y) + (e2z * e2z));
                        double cos = l1 > 0 && l2 > 0 ? Numerics.NpMath.Clip(((e1x * e2x) + (e1y * e2y) + (e1z * e2z)) / (l1 * l2), -1.0, 1.0) : 1.0;
                        double ang = Math.Acos(cos);
                        contrib[(k * 9) + a] = nx * ang;
                        contrib[(k * 9) + a + 1] = ny * ang;
                        contrib[(k * 9) + a + 2] = nz * ang;
                    }
                }
            });
            for (int k = 0; k < len; k++)
            {
                if (skip[k])
                {
                    continue;
                }
                int t = t0 + k;
                for (int q = 0; q < 3; q++)
                {
                    long vi = (long)f[(t * 3) + q] * 3;
                    acc[vi] += contrib[(k * 9) + (q * 3)];
                    acc[vi + 1] += contrib[(k * 9) + (q * 3) + 1];
                    acc[vi + 2] += contrib[(k * 9) + (q * 3) + 2];
                }
            }
        }
        for (int i = 0; i < acc.Length / 3; i++)
        {
            double l = Math.Sqrt((acc[i * 3] * acc[i * 3]) + (acc[(i * 3) + 1] * acc[(i * 3) + 1]) + (acc[(i * 3) + 2] * acc[(i * 3) + 2]));
            if (l > 0)
            {
                acc[i * 3] /= l;
                acc[(i * 3) + 1] /= l;
                acc[(i * 3) + 2] /= l;
            }
        }
        return acc;
    }

    /// <summary>메시를 glb 로 씁니다 (export_glb, NORMAL·COLOR_0 포함). 반환: 파일 크기 [바이트].</summary>
    public static long ExportGlb(CaveMesh mesh, string path)
    {
        int nv = mesh.VertexCount;
        int nf = mesh.FaceCount;
        double[] mn = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] mx = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        for (int q = 0; q < nv * 3; q++)
        {
            float fv = (float)mesh.Vertices[q];
            mn[q % 3] = Math.Min(mn[q % 3], fv);
            mx[q % 3] = Math.Max(mx[q % 3], fv);
        }
        byte[] col = mesh.Colors;
        static int Pad4(int x) => (x + 3) & ~3;
        int idxLen = nf * 3 * 4;
        int posLen = nv * 12;
        int nrmLen = nv * 12;
        int oIdx = 0;
        int oPos = Pad4(oIdx + idxLen);
        int oNrm = Pad4(oPos + posLen);
        int oCol = Pad4(oNrm + nrmLen);
        int binLen = Pad4(oCol + col.Length);
        var gltf = new OrderedDictionary<string, object?>
        {
            ["scene"] = 0L,
            ["scenes"] = new List<object?> { new OrderedDictionary<string, object?> { ["nodes"] = new List<object?> { 0L } } },
            ["asset"] = new OrderedDictionary<string, object?> { ["version"] = "2.0", ["generator"] = "bpcg (C#)" },
            ["accessors"] = new List<object?>
            {
                Accessor(0, 5125, nf * 3, "SCALAR", false),
                Accessor(1, 5126, nv, "VEC3", false, mn.Select(x => (object?)x).ToList(), mx.Select(x => (object?)x).ToList()),
                Accessor(2, 5126, nv, "VEC3", false),
                Accessor(3, 5121, nv, "VEC4", true),
            },
            ["meshes"] = new List<object?>
            {
                new OrderedDictionary<string, object?>
                {
                    ["name"] = "caves",
                    ["primitives"] = new List<object?>
                    {
                        new OrderedDictionary<string, object?>
                        {
                            ["attributes"] = new OrderedDictionary<string, object?> { ["POSITION"] = 1L, ["NORMAL"] = 2L, ["COLOR_0"] = 3L },
                            ["indices"] = 0L,
                            ["mode"] = 4L,
                        },
                    },
                },
            },
            ["nodes"] = new List<object?> { new OrderedDictionary<string, object?> { ["name"] = "caves", ["mesh"] = 0L } },
            ["buffers"] = new List<object?> { new OrderedDictionary<string, object?> { ["byteLength"] = (long)binLen } },
            ["bufferViews"] = new List<object?>
            {
                View(oIdx, idxLen),
                View(oPos, posLen),
                View(oNrm, nrmLen),
                View(oCol, col.Length),
            },
        };
        byte[] json = System.Text.Encoding.UTF8.GetBytes(PyJson.Dumps(gltf, new PyJson.Options(EnsureAscii: false)));
        int jsonLen = Pad4(json.Length);
        int total = 12 + 8 + jsonLen + 8 + binLen;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        // 파일 전체를 메모리에 만들지 않고, 같은 바이트를 순서대로 흘려 씁니다
        // (머리 → JSON 덩어리(빈칸으로 4바이트 맞춤) → BIN 덩어리(0 으로 4바이트 맞춤)).
        string tmp = path + ".part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Span<byte> head = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(head, 0x46546C67); // "glTF"
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(head[8..], (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(head[12..], (uint)jsonLen);
            BinaryPrimitives.WriteUInt32LittleEndian(head[16..], 0x4E4F534A); // "JSON"
            fs.Write(head);
            fs.Write(json);
            for (int q = json.Length; q < jsonLen; q++)
            {
                fs.WriteByte((byte)' ');
            }
            Span<byte> binHead = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(binHead, (uint)binLen);
            BinaryPrimitives.WriteUInt32LittleEndian(binHead[4..], 0x004E4942); // "BIN\0"
            fs.Write(binHead);
            byte[] buf = new byte[1 << 20];
            int fill = 0;
            long written = 0;
            void Flush()
            {
                fs.Write(buf, 0, fill);
                written += fill;
                fill = 0;
            }
            void PadTo(long offset)
            {
                while (written + fill < offset)
                {
                    if (fill == buf.Length)
                    {
                        Flush();
                    }
                    buf[fill++] = 0;
                }
            }
            for (int q = 0; q < mesh.Faces.Length; q++)
            {
                if (fill + 4 > buf.Length)
                {
                    Flush();
                }
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(fill), (uint)mesh.Faces[q]);
                fill += 4;
            }
            PadTo(oPos);
            foreach (double[] src in new[] { mesh.Vertices, mesh.Normals })
            {
                for (int q = 0; q < nv * 3; q++)
                {
                    if (fill + 4 > buf.Length)
                    {
                        Flush();
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(fill), (float)src[q]);
                    fill += 4;
                }
                PadTo(ReferenceEquals(src, mesh.Vertices) ? oNrm : oCol);
            }
            Flush();
            fs.Write(col);
            written += col.Length;
            PadTo(binLen);
            Flush();
        }
        File.Move(tmp, path, true);
        return total;
    }

    private static OrderedDictionary<string, object?> Accessor(int view, int componentType, int count, string type, bool normalized,
        List<object?>? min = null, List<object?>? max = null)
    {
        var a = new OrderedDictionary<string, object?>
        {
            ["bufferView"] = (long)view,
            ["componentType"] = (long)componentType,
            ["count"] = (long)count,
            ["type"] = type,
        };
        if (normalized)
        {
            a["normalized"] = true;
        }
        if (min is not null)
        {
            a["min"] = min;
            a["max"] = max;
        }
        return a;
    }

    private static OrderedDictionary<string, object?> View(int offset, int length) => new()
    {
        ["buffer"] = 0L,
        ["byteOffset"] = (long)offset,
        ["byteLength"] = (long)length,
    };
}
