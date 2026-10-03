using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public required long[] Faces { get; init; }

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
    internal static (List<double> Verts, List<long> Faces) IsoSurface(double[] f, int nx, int ny, int nz, double v)
    {
        var verts = new List<double>();
        var faces = new List<long>();
        var edgeVert = new Dictionary<(long, long), long>();
        long Id(int i, int j, int k) => (((long)i * ny) + j) * nz + k;
        long Vert(int ia, int ja, int ka, int ib, int jb, int kb)
        {
            long a = Id(ia, ja, ka);
            long b = Id(ib, jb, kb);
            (long, long) key = a < b ? (a, b) : (b, a);
            if (edgeVert.TryGetValue(key, out long idx))
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
        void AddTri(long p, long q, long r, (double X, double Y, double Z) outside)
        {
            int pi = (int)p * 3;
            int qi = (int)q * 3;
            int ri = (int)r * 3;
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
        for (int i = 0; i + 1 < nx; i++)
        {
            for (int j = 0; j + 1 < ny; j++)
            {
                for (int k = 0; k + 1 < nz; k++)
                {
                    foreach (int[] tet in Tets)
                    {
                        int maskIn = 0;
                        for (int q = 0; q < 4; q++)
                        {
                            (int ddx, int ddy, int ddz) = Corner[tet[q]];
                            ci[q] = i + ddx;
                            cj[q] = j + ddy;
                            ck[q] = k + ddz;
                            if (f[Id(ci[q], cj[q], ck[q])] < 0.0)
                            {
                                maskIn |= 1 << q;
                            }
                        }
                        int nIn = System.Numerics.BitOperations.PopCount((uint)maskIn);
                        if (nIn == 0 || nIn == 4)
                        {
                            continue;
                        }
                        var ins = new List<int>();
                        var outs = new List<int>();
                        for (int q = 0; q < 4; q++)
                        {
                            ((maskIn & (1 << q)) != 0 ? ins : outs).Add(q);
                        }
                        double ox = 0;
                        double oy = 0;
                        double oz = 0;
                        foreach (int q in outs)
                        {
                            ox += ci[q] * v;
                            oy += cj[q] * v;
                            oz += ck[q] * v;
                        }
                        (double, double, double) outside = (ox / outs.Count, oy / outs.Count, oz / outs.Count);
                        long E(int qa, int qb) => Vert(ci[qa], cj[qa], ck[qa], ci[qb], cj[qb], ck[qb]);
                        if (nIn == 1 || nIn == 3)
                        {
                            int lone = nIn == 1 ? ins[0] : outs[0];
                            List<int> rest = nIn == 1 ? outs : ins;
                            AddTri(E(lone, rest[0]), E(lone, rest[1]), E(lone, rest[2]), outside);
                        }
                        else
                        {
                            long p0 = E(ins[0], outs[0]);
                            long p1 = E(ins[0], outs[1]);
                            long p2 = E(ins[1], outs[1]);
                            long p3 = E(ins[1], outs[0]);
                            AddTri(p0, p1, p2, outside);
                            AddTri(p0, p2, p3, outside);
                        }
                    }
                }
            }
        }
        return (verts, faces);
    }

    /// <summary>
    /// 회랑 rect 안 동굴 벽 삼각형 (cave_surface, 국소 좌표). 반환: (verts (V, 3), faces (F, 3), diag).
    /// </summary>
    public static (double[] Verts, long[] Faces, OrderedDictionary<string, object?> Diag) CaveSurface(
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
        var vertsL = new List<double>();
        var facesL = new List<long>();
        long nVert = 0;
        long tiles = 0;
        long tilesWithCaves = 0;
        long voxels = 0;
        for (int ia = 0; ia < Math.Max(nxv - 1, 1); ia += TileVoxels)
        {
            int ib = Math.Min(ia + TileVoxels, nxv - 1);
            for (int ja = 0; ja < Math.Max(nyv - 1, 1); ja += TileVoxels)
            {
                int jb = Math.Min(ja + TileVoxels, nyv - 1);
                tiles++;
                List<(double Lo, double Hi)> bands = CaveBands(volume, (xs[ia], xs[ib], ys[ja], ys[jb]), pad);
                if (bands.Count == 0)
                {
                    continue;
                }
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
                    voxels += dc.Length;
                    if (fmin >= 0.0 || fmax <= 0.0)
                    {
                        continue;
                    }
                    (List<double> vt, List<long> fc) = IsoSurface(dc, ntx, nty, nz, v);
                    if (fc.Count == 0)
                    {
                        continue;
                    }
                    double ox = xs[ia];
                    double oy = ys[ja];
                    double oz = k0 * v;
                    for (int q = 0; q < vt.Count; q += 3)
                    {
                        vertsL.Add(vt[q] + ox);
                        vertsL.Add(vt[q + 1] + oy);
                        vertsL.Add(vt[q + 2] + oz);
                    }
                    // 값이 커지는 쪽(동굴 밖)을 앞면으로 둔 삼각형을 뒤집어 빈 곳 쪽으로.
                    for (int q = 0; q < fc.Count; q += 3)
                    {
                        facesL.Add(fc[q] + nVert);
                        facesL.Add(fc[q + 2] + nVert);
                        facesL.Add(fc[q + 1] + nVert);
                    }
                    nVert += vt.Count / 3;
                }
                tilesWithCaves++;
            }
            log?.Invoke($"[동굴 메시] 열 {ib}/{nxv - 1} 조각 끝, 꼭짓점 {nVert}");
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["tiles"] = tiles,
            ["tiles_with_caves"] = tilesWithCaves,
            ["voxels"] = voxels,
        };
        if (vertsL.Count == 0)
        {
            diag["faces_raw"] = 0L;
            diag["faces_kept"] = 0L;
            diag["seconds"] = Stopwatch.GetElapsedTime(tAll).TotalSeconds;
            return ([], [], diag);
        }
        double[] verts = vertsL.ToArray();
        long[] faces = facesL.ToArray();
        diag["faces_raw"] = (long)(faces.Length / 3);
        int nf = faces.Length / 3;
        double[] cen = new double[nf * 3];
        for (int t = 0; t < nf; t++)
        {
            for (int ax = 0; ax < 3; ax++)
            {
                // verts[faces].mean(axis=1): 꼭짓점 세 개의 np.mean
                double a = verts[(faces[t * 3] * 3) + ax];
                double b = verts[(faces[(t * 3) + 1] * 3) + ax];
                double c = verts[(faces[(t * 3) + 2] * 3) + ax];
                cen[(t * 3) + ax] = (0.0 + a + b + c) / 3.0;
            }
        }
        double[] d1 = volume.Evaluate(cen).D1;
        var keep = new List<long>();
        for (int t = 0; t < nf; t++)
        {
            if (d1[t] < 0.0)
            {
                keep.Add(faces[t * 3]);
                keep.Add(faces[(t * 3) + 1]);
                keep.Add(faces[(t * 3) + 2]);
            }
        }
        diag["faces_kept"] = (long)(keep.Count / 3);
        long[] used = keep.Distinct().OrderBy(x => x).ToArray();
        long[] remap = new long[verts.Length / 3];
        Array.Fill(remap, -1L);
        for (long q = 0; q < used.Length; q++)
        {
            remap[used[q]] = q;
        }
        double[] vOut = new double[used.Length * 3];
        for (long q = 0; q < used.Length; q++)
        {
            Array.Copy(verts, used[q] * 3, vOut, q * 3, 3);
        }
        long[] fOut = keep.Select(x => remap[x]).ToArray();
        diag["seconds"] = Stopwatch.GetElapsedTime(tAll).TotalSeconds;
        return (vOut, fOut, diag);
    }

    /// <summary>
    /// 국소 꼭짓점·삼각형 → 엔진 좌표 메시 (build_mesh): 같은 자리 꼭짓점을 붙이고(소수 8자리), 각도 가중 꼭짓점 법선,
    /// 색은 법선 반대(암석) 쪽 0.5 칸 점의 재질.
    /// </summary>
    public static CaveMesh BuildMesh(double[] vertsLocal, long[] faces, HeroVolume volume, EngineFrame frame, double voxelM)
    {
        double[] ve = frame.ToEngine(vertsLocal);
        // merge_vertices: 소수 8자리로 반올림한 자리가 같으면 한 꼭짓점
        int nv = ve.Length / 3;
        var key = new Dictionary<(long, long, long), long>();
        long[] map = new long[nv];
        var merged = new List<double>();
        for (int i = 0; i < nv; i++)
        {
            (long, long, long) k = (
                (long)Math.Round(ve[i * 3] * 1e8), (long)Math.Round(ve[(i * 3) + 1] * 1e8), (long)Math.Round(ve[(i * 3) + 2] * 1e8));
            if (!key.TryGetValue(k, out long idx))
            {
                idx = merged.Count / 3;
                key[k] = idx;
                merged.Add(ve[i * 3]);
                merged.Add(ve[(i * 3) + 1]);
                merged.Add(ve[(i * 3) + 2]);
            }
            map[i] = idx;
        }
        long[] f2 = faces.Select(x => map[x]).ToArray();
        // remove_unreferenced_vertices
        long[] used = f2.Distinct().OrderBy(x => x).ToArray();
        long[] remap = new long[merged.Count / 3];
        Array.Fill(remap, -1L);
        double[] verts = new double[used.Length * 3];
        for (long q = 0; q < used.Length; q++)
        {
            remap[used[q]] = q;
            verts[q * 3] = merged[(int)(used[q] * 3)];
            verts[(q * 3) + 1] = merged[(int)(used[q] * 3) + 1];
            verts[(q * 3) + 2] = merged[(int)(used[q] * 3) + 2];
        }
        long[] fFinal = f2.Select(x => remap[x]).ToArray();
        double[] normals = VertexNormals(verts, fFinal);
        double[] probe = new double[verts.Length];
        for (int q = 0; q < verts.Length; q++)
        {
            probe[q] = verts[q] - (ColorProbeVoxels * voxelM * normals[q]);
        }
        byte[] mat = volume.Evaluate(frame.ToLocal(probe)).SolidMaterial;
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

    // trimesh weighted_vertex_normals: 면 법선을 꼭짓점 각도로 가중해 더한 뒤 정규화
    private static double[] VertexNormals(double[] v, long[] f)
    {
        double[] acc = new double[v.Length];
        for (int t = 0; t < f.Length / 3; t++)
        {
            long[] idx = [f[t * 3], f[(t * 3) + 1], f[(t * 3) + 2]];
            double[] p = new double[9];
            for (int q = 0; q < 3; q++)
            {
                Array.Copy(v, idx[q] * 3, p, q * 3, 3);
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
                continue;
            }
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
                acc[idx[q] * 3] += nx * ang;
                acc[(idx[q] * 3) + 1] += ny * ang;
                acc[(idx[q] * 3) + 2] += nz * ang;
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
        byte[] idx = new byte[nf * 3 * 4];
        for (int q = 0; q < mesh.Faces.Length; q++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan(q * 4), (uint)mesh.Faces[q]);
        }
        byte[] pos = new byte[nv * 12];
        byte[] nrm = new byte[nv * 12];
        double[] mn = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] mx = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        for (int q = 0; q < nv * 3; q++)
        {
            float fv = (float)mesh.Vertices[q];
            BinaryPrimitives.WriteSingleLittleEndian(pos.AsSpan(q * 4), fv);
            BinaryPrimitives.WriteSingleLittleEndian(nrm.AsSpan(q * 4), (float)mesh.Normals[q]);
            mn[q % 3] = Math.Min(mn[q % 3], fv);
            mx[q % 3] = Math.Max(mx[q % 3], fv);
        }
        byte[] col = mesh.Colors;
        static int Pad4(int x) => (x + 3) & ~3;
        int oIdx = 0;
        int oPos = Pad4(oIdx + idx.Length);
        int oNrm = Pad4(oPos + pos.Length);
        int oCol = Pad4(oNrm + nrm.Length);
        int binLen = Pad4(oCol + col.Length);
        byte[] bin = new byte[binLen];
        idx.CopyTo(bin, oIdx);
        pos.CopyTo(bin, oPos);
        nrm.CopyTo(bin, oNrm);
        col.CopyTo(bin, oCol);
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
                View(oIdx, idx.Length),
                View(oPos, pos.Length),
                View(oNrm, nrm.Length),
                View(oCol, col.Length),
            },
        };
        byte[] json = System.Text.Encoding.UTF8.GetBytes(PyJson.Dumps(gltf, new PyJson.Options(EnsureAscii: false)));
        int jsonLen = Pad4(json.Length);
        byte[] jsonPadded = new byte[jsonLen];
        Array.Fill(jsonPadded, (byte)' ');
        json.CopyTo(jsonPadded, 0);
        int total = 12 + 8 + jsonLen + 8 + binLen;
        byte[] glb = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0), 0x46546C67); // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); // "JSON"
        jsonPadded.CopyTo(glb, 20);
        int ob = 20 + jsonLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(ob), (uint)binLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(ob + 4), 0x004E4942); // "BIN\0"
        bin.CopyTo(glb, ob + 8);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = path + ".part";
        File.WriteAllBytes(tmp, glb);
        File.Move(tmp, path, true);
        return glb.Length;
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
