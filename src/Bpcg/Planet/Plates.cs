using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.IO;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>경계 판정 결과 (classify_boundaries 의 dict).</summary>
public sealed record BoundaryClassification(byte[] BoundaryType, double[] Gamma, double[] Tau, int[] Other, double[] HalfGap);

/// <summary>ω 뒤집기 기록 한 줄 (ensure_divergent_boundaries 의 log).</summary>
public sealed record FlipRecord(int Round, int Plate, int Neighbor, string Method);

/// <summary>ensure_divergent_boundaries 의 반환.</summary>
public sealed record DivergentFix(double[] Omega, List<FlipRecord> Flips, BoundaryClassification Cls, List<int> Remaining);

/// <summary>
/// 판: 씨앗, 판 배정, 오일러 극 운동, 경계 판정, 경계까지 거리, 섭입 방향
/// (src/bpcg/planet/plates.py, docs/pipeline.md 4.1).
/// </summary>
/// <remarks>
/// (M, 3) 배열은 행 우선 평평한 double[] 입니다. 계산 순서와 동률 규칙은 docs/csharp_port.md 6장 planet ① 절을 따릅니다.
/// 경계까지 거리는 core <see cref="Distance.NearestSource"/> 의 동률 규칙에 기대므로 core 그래프가 비트 단위로 같아야 합니다.
/// </remarks>
public static class Plates
{
    public const long StreamPlateSeed = 301;
    public const long StreamPole = 302;
    public const long StreamSpeed = 303;
    public const long StreamWarp = 304;
    public const int MaxFlipRounds = 3;
    public const int BoundarySmoothIterations = 4;

    public const byte BoundaryNone = 0;
    public const byte Convergent = 1;
    public const byte Divergent = 2;
    public const byte Transform = 3;

    public const byte KindNone = 0;
    public const byte KindOceanContinent = 1;
    public const byte KindOceanOcean = 2;
    public const byte KindCollision = 3;

    private const double Myr = 1.0e6;

    /// <summary>구면 CellGraph 인지 확인합니다 (check_sphere_graph).</summary>
    public static void CheckSphereGraph(CellGraph graph)
    {
        if (graph.Kind != "sphere" || graph.R is not double r || !(r > 0))
        {
            throw new ArgumentException("판 계산은 반지름이 있는 구면 그래프(kind='sphere')에서만 합니다");
        }
    }

    /// <summary>(N,) bool 배열인지 확인합니다 (check_bool_mask).</summary>
    public static bool[] CheckBoolMask(bool[] mask, int nCells, string name)
    {
        if (mask.Length != nCells)
        {
            throw new ArgumentException($"{name} 모양이 칸 수와 다릅니다: ({mask.Length},), 기대 ({nCells},)");
        }
        return mask;
    }

    /// <summary>행성 시드에서 용도별 시드 (sub_seed): hash3(seed, stream, 0) &gt;&gt; 1.</summary>
    public static long SubSeed(long seed, long stream) => (long)(Hashing.Hash3(seed, stream, 0) >> 1);

    /// <summary>해시 균등수 4개 → Box–Muller 정규분포 3개 → 정규화 (hashed_unit_vectors). (count, 3).</summary>
    public static double[] HashedUnitVectors(long seed, long stream, int count)
    {
        double[] output = new double[count * 3];
        Span<double> g = stackalloc double[3];
        for (int k = 0; k < count; k++)
        {
            double u0 = Hashing.HashUnit(seed, stream, (4L * k) + 0);
            double u1 = Hashing.HashUnit(seed, stream, (4L * k) + 1);
            double u2 = Hashing.HashUnit(seed, stream, (4L * k) + 2);
            double u3 = Hashing.HashUnit(seed, stream, (4L * k) + 3);
            double r1 = Math.Sqrt(-2.0 * Math.Log(1.0 - u0)); // 1 − u ∈ (0, 1] 이라 log 가 유한
            double r2 = Math.Sqrt(-2.0 * Math.Log(1.0 - u2));
            g[0] = r1 * Math.Cos(2.0 * Math.PI * u1);
            g[1] = r1 * Math.Sin(2.0 * Math.PI * u1);
            g[2] = r2 * Math.Cos(2.0 * Math.PI * u3);
            double norm = LinAlg.Norm3(g);
            if (norm > 0.0)
            {
                output[k * 3] = g[0] / norm;
                output[(k * 3) + 1] = g[1] / norm;
                output[(k * 3) + 2] = g[2] / norm;
            }
            else
            {
                output[(k * 3) + 2] = 1.0;
            }
        }
        return output;
    }

    /// <summary>판 씨앗 s_k (M, 3) 단위 벡터 (plate_seeds).</summary>
    public static double[] PlateSeeds(Config cfg)
    {
        long count = cfg.I("plates.count");
        if (count < 2)
        {
            throw new ArgumentException($"판 개수는 2 이상이어야 합니다: {count}");
        }
        return HashedUnitVectors(SubSeed(cfg.I("planet.seed"), StreamPlateSeed), 0, (int)count);
    }

    /// <summary>판 각속도 ω_k (M, 3) [rad/yr] (plate_omegas). radiusM 이 없으면 cfg.planet.radius_m.</summary>
    public static double[] PlateOmegas(Config cfg, double? radiusM = null)
    {
        int count = (int)cfg.I("plates.count");
        long seed = SubSeed(cfg.I("planet.seed"), StreamPole);
        double[] axes = HashedUnitVectors(seed, 0, count);
        double[] sp = cfg.FArray("plates.speed_m_per_yr");
        if (sp.Length != 2)
        {
            throw new ArgumentException($"speed_m_per_yr 는 원소 2개여야 합니다: {sp.Length}");
        }
        double vLo = sp[0];
        double vHi = sp[1];
        if (!(0.0 <= vLo && vLo <= vHi))
        {
            throw new ArgumentException($"speed_m_per_yr 는 0 ≤ 하한 ≤ 상한이어야 합니다: ({vLo}, {vHi})");
        }
        double radius = radiusM ?? cfg.F("planet.radius_m");
        long sSeed = SubSeed(cfg.I("planet.seed"), StreamSpeed);
        double[] omega = new double[count * 3];
        for (int k = 0; k < count; k++)
        {
            double speed = vLo + ((vHi - vLo) * Hashing.HashUnit(sSeed, k, 0));
            double f = speed / radius;
            for (int i = 0; i < 3; i++)
            {
                omega[(k * 3) + i] = axes[(k * 3) + i] * f;
            }
        }
        return omega;
    }

    /// <summary>q (N,3), seeds (M,3) → argmax_k q·s_k (같으면 번호가 작은 판, _argmax_dot_kernel).</summary>
    internal static void ArgmaxDotKernel(double[] q, double[] seeds, int[] output)
    {
        int m = seeds.Length / 3;
        Parallelism.For(0, q.Length / 3, c =>
        {
            double best = double.NegativeInfinity;
            int arg = 0;
            for (int k = 0; k < m; k++)
            {
                double d = (q[c * 3] * seeds[k * 3]) + (q[(c * 3) + 1] * seeds[(k * 3) + 1])
                    + (q[(c * 3) + 2] * seeds[(k * 3) + 2]);
                if (d > best)
                {
                    best = d;
                    arg = k;
                }
            }
            output[c] = arg;
        });
    }

    /// <summary>칸마다 판 번호 π(c) (assign_plates): 경계를 벡터 노이즈로 흔든 구면 보로노이.</summary>
    public static int[] AssignPlates(double[] unit, double[] seeds, Config cfg)
    {
        double[] warp = Noise.VectorFbm3(
            unit, SubSeed(cfg.I("planet.seed"), StreamWarp), frequency: cfg.F("plates.warp_frequency"));
        double amp = cfg.F("plates.warp_amplitude");
        double[] q = new double[unit.Length];
        for (int i = 0; i < unit.Length; i++)
        {
            q[i] = unit[i] + (amp * warp[i]);
        }
        for (int c = 0; c < q.Length / 3; c++)
        {
            double norm = LinAlg.Norm3(q.AsSpan(c * 3, 3));
            q[c * 3] /= norm;
            q[(c * 3) + 1] /= norm;
            q[(c * 3) + 2] /= norm;
        }
        int[] output = new int[unit.Length / 3];
        ArgmaxDotKernel(q, seeds, output);
        return output;
    }

    /// <summary>경계 판정 커널 (_classify_kernel). 칸마다 다른 판 이웃들과의 γ, τ 평균 등.</summary>
    internal static void ClassifyKernel(
        double[] unit, int[] nbr, double[] dist, int[] plate, double[] omega, double radius, double kappa,
        byte[] btype, double[] gamma, double[] tau, int[] other, double[] halfGap)
    {
        const int nSlots = CellGraph.NSlots;
        Parallelism.For(0, plate.Length, c =>
        {
            int k = plate[c];
            double px = unit[c * 3];
            double py = unit[(c * 3) + 1];
            double pz = unit[(c * 3) + 2];
            double sg = 0.0;
            double st = 0.0;
            int cnt = 0;
            double hg = double.PositiveInfinity;
            int bestPlate = -1;
            int bestCount = 0;
            for (int s = 0; s < nSlots; s++)
            {
                int v = nbr[(c * nSlots) + s];
                if (v < 0)
                {
                    continue;
                }
                int kv = plate[v];
                if (kv == k)
                {
                    continue;
                }
                // b̂: 이웃 쪽을 가리키는 접선 방향 (가이드 5장)
                double qx = unit[v * 3];
                double qy = unit[(v * 3) + 1];
                double qz = unit[(v * 3) + 2];
                double d = (qx * px) + (qy * py) + (qz * pz);
                double bx = qx - (d * px);
                double by = qy - (d * py);
                double bz = qz - (d * pz);
                double bn = Math.Sqrt((bx * bx) + (by * by) + (bz * bz));
                if (bn == 0.0)
                {
                    continue;
                }
                bx /= bn;
                by /= bn;
                bz /= bn;
                // Δv = (ω' − ω) × R·p
                double wx = omega[kv * 3] - omega[k * 3];
                double wy = omega[(kv * 3) + 1] - omega[(k * 3) + 1];
                double wz = omega[(kv * 3) + 2] - omega[(k * 3) + 2];
                double dvx = ((wy * pz) - (wz * py)) * radius;
                double dvy = ((wz * px) - (wx * pz)) * radius;
                double dvz = ((wx * py) - (wy * px)) * radius;
                sg += -((dvx * bx) + (dvy * by) + (dvz * bz));
                double cx = (py * bz) - (pz * by);
                double cy = (pz * bx) - (px * bz);
                double cz = (px * by) - (py * bx);
                st += Math.Abs((dvx * cx) + (dvy * cy) + (dvz * cz));
                cnt++;
                double ds = dist[(c * nSlots) + s];
                if (ds < hg)
                {
                    hg = ds;
                }
                int mcount = 0;
                for (int s2 = 0; s2 < nSlots; s2++)
                {
                    int v2 = nbr[(c * nSlots) + s2];
                    if (v2 >= 0 && plate[v2] == kv)
                    {
                        mcount++;
                    }
                }
                if (mcount > bestCount || (mcount == bestCount && kv < bestPlate))
                {
                    bestCount = mcount;
                    bestPlate = kv;
                }
            }
            if (cnt == 0)
            {
                btype[c] = 0;
                gamma[c] = 0.0;
                tau[c] = 0.0;
                other[c] = -1;
                halfGap[c] = 0.0;
                return;
            }
            double gm = sg / cnt;
            double tm = st / cnt;
            if (gm > kappa * tm)
            {
                btype[c] = 1;
            }
            else if (gm < -kappa * tm)
            {
                btype[c] = 2;
            }
            else
            {
                btype[c] = 3;
            }
            gamma[c] = gm;
            tau[c] = tm;
            other[c] = bestPlate;
            halfGap[c] = 0.5 * hg;
        });
    }

    /// <summary>판 경계 칸을 수렴(1)·발산(2)·변환(3)으로 판정합니다 (classify_boundaries).</summary>
    public static BoundaryClassification ClassifyBoundaries(CellGraph graph, int[] plateId, double[] omega, double kappa)
    {
        CheckSphereGraph(graph);
        int n = graph.NCells;
        if (plateId.Length != n)
        {
            throw new ArgumentException($"plate_id 모양이 칸 수와 다릅니다: ({plateId.Length},)");
        }
        int m = omega.Length / 3;
        int pmin = int.MaxValue;
        int pmax = int.MinValue;
        foreach (int p in plateId)
        {
            pmin = Math.Min(pmin, p);
            pmax = Math.Max(pmax, p);
        }
        if (omega.Length % 3 != 0 || (n > 0 && (pmin < 0 || pmax >= m)))
        {
            throw new ArgumentException("omega 는 (M, 3) 이고 plate_id 는 0..M-1 이어야 합니다");
        }
        if (!(double.IsFinite(kappa) && kappa >= 0))
        {
            throw new ArgumentException($"boundary_kappa 는 0 이상이어야 합니다: {kappa}");
        }
        var cls = new BoundaryClassification(new byte[n], new double[n], new double[n], new int[n], new double[n]);
        ClassifyKernel(graph.Unit(), graph.Nbr, graph.Dist, plateId, omega, graph.R!.Value, kappa,
            cls.BoundaryType, cls.Gamma, cls.Tau, cls.Other, cls.HalfGap);
        return cls;
    }

    /// <summary>member 칸끼리(같은 판)만 이웃 평균을 iterations 번 (야코비, _smooth_on_cells_kernel).</summary>
    internal static double[] SmoothOnCellsKernel(double[] values, int[] cells, bool[] member, int[] plate, int[] nbr, int iterations)
    {
        double[] cur = (double[])values.Clone();
        double[] nxt = (double[])values.Clone();
        const int nSlots = CellGraph.NSlots;
        for (int it = 0; it < iterations; it++)
        {
            foreach (int c in cells)
            {
                double s = cur[c];
                int cnt = 1;
                for (int k = 0; k < nSlots; k++)
                {
                    int v = nbr[(c * nSlots) + k];
                    if (v >= 0 && member[v] && plate[v] == plate[c])
                    {
                        s += cur[v];
                        cnt++;
                    }
                }
                nxt[c] = s / cnt;
            }
            foreach (int c in cells)
            {
                cur[c] = nxt[c];
            }
        }
        return cur;
    }

    /// <summary>같은 종류·같은 판 경계 칸끼리 값을 이웃 평균합니다 (smooth_along_boundary).</summary>
    public static double[] SmoothAlongBoundary(double[] values, bool[] member, int[] plate, int[] nbr)
    {
        int[] cells = FlatNonZero(member);
        if (cells.Length == 0)
        {
            return (double[])values.Clone();
        }
        return SmoothOnCellsKernel(values, cells, member, plate, nbr, BoundarySmoothIterations);
    }

    /// <summary>같은 판 이웃만 남긴 그래프 (same_plate_graph). Pos·Dist·Area 는 공유합니다.</summary>
    public static CellGraph SamePlateGraph(CellGraph graph, int[] plateId)
    {
        const int nSlots = CellGraph.NSlots;
        int[] nbrSame = new int[graph.Nbr.Length];
        for (int c = 0; c < graph.NCells; c++)
        {
            for (int s = 0; s < nSlots; s++)
            {
                int v = graph.Nbr[(c * nSlots) + s];
                nbrSame[(c * nSlots) + s] = v >= 0 && plateId[v] == plateId[c] ? v : -1;
            }
        }
        return new CellGraph(graph.Kind, graph.Shape, graph.Pos, nbrSame, graph.Dist, graph.Area, graph.Spacing, graph.R);
    }

    /// <summary>가장 가까운 출발 칸까지 대원 거리 + 그 칸에서 경계선까지 반 칸 (_boundary_distance).</summary>
    internal static (double[] Dist, long[] Src) BoundaryDistance(CellGraph graph, bool[] isSource, double[] halfGap)
    {
        (double[] dist, long[] src) = Distance.NearestSource(graph, isSource);
        double[] output = new double[graph.NCells];
        for (int c = 0; c < output.Length; c++)
        {
            output[c] = src[c] >= 0 ? dist[c] + halfGap[src[c]] : double.PositiveInfinity;
        }
        return (output, src);
    }

    /// <summary>판마다 반확장 속도의 하한 [m/yr] (_age_consistent_rate_floor).</summary>
    internal static double[] AgeConsistentRateFloor(double[] distDiv, int[] plate, bool[] continental, int m, double capMyr)
    {
        double[] dMax = new double[m];
        for (int c = 0; c < plate.Length; c++)
        {
            if (!continental[c] && double.IsFinite(distDiv[c]))
            {
                dMax[plate[c]] = Math.Max(dMax[plate[c]], distDiv[c]); // np.maximum.at
            }
        }
        double denom = capMyr * Myr;
        double[] output = new double[m];
        for (int k = 0; k < m; k++)
        {
            output[k] = dMax[k] / denom;
        }
        return output;
    }

    /// <summary>해양저 나이 = 해령 거리 / 반확장 속도 [Myr] (seafloor_age_myr). 0..cap 로 자릅니다.</summary>
    public static double[] SeafloorAgeMyr(double[] distDivergentM, double[] spreadingMPerYr, double capMyr)
    {
        double[] age = new double[distDivergentM.Length];
        for (int c = 0; c < age.Length; c++)
        {
            double d = distDivergentM[c];
            double v = spreadingMPerYr[c];
            double a = double.IsFinite(d) && double.IsFinite(v) && v > 0.0 ? d / v / Myr : capMyr;
            age[c] = NpMath.Clip(a, 0.0, capMyr);
        }
        return age;
    }

    /// <summary>경계 칸마다 다른 판 이웃 중 대륙 비율과 해양 이웃 평균 나이 (_other_side_kernel).</summary>
    internal static void OtherSideKernel(
        int[] nbr, int[] plate, bool[] continental, double[] age, int[] cells, double[] contFrac, double[] otherAge)
    {
        const int nSlots = CellGraph.NSlots;
        Parallelism.For(0, cells.Length, t =>
        {
            int c = cells[t];
            int k = plate[c];
            int nOther = 0;
            int nCont = 0;
            double sAge = 0.0;
            int nAge = 0;
            for (int s = 0; s < nSlots; s++)
            {
                int v = nbr[(c * nSlots) + s];
                if (v < 0 || plate[v] == k)
                {
                    continue;
                }
                nOther++;
                if (continental[v])
                {
                    nCont++;
                }
                else
                {
                    sAge += age[v];
                    nAge++;
                }
            }
            contFrac[t] = nOther > 0 ? (double)nCont / nOther : 0.0;
            otherAge[t] = nAge > 0 ? sAge / nAge : NpMath.NaN;
        });
    }

    /// <summary>수렴 경계 칸마다 kind 와 side (+1 위판, −1 섭입판, 0 충돌) (_subduction_polarity).</summary>
    internal static (byte[] Kind, sbyte[] Side) SubductionPolarity(
        CellGraph graph, int[] plate, int[] other, bool[] isConv, bool[] continental, double[] ageMyr, int nPlates)
    {
        int n = graph.NCells;
        byte[] kind = new byte[n];
        sbyte[] side = new sbyte[n];
        int[] cells = FlatNonZero(isConv);
        if (cells.Length == 0)
        {
            return (kind, side);
        }
        double[] contFrac = new double[cells.Length];
        double[] otherAge = new double[cells.Length];
        double[] ageOcean = new double[n];
        for (int c = 0; c < n; c++)
        {
            ageOcean[c] = continental[c] ? 0.0 : ageMyr[c];
        }
        OtherSideKernel(graph.Nbr, plate, continental, ageOcean, cells, contFrac, otherAge);
        int nc = cells.Length;
        byte[] kCells = new byte[nc];
        long[] sub = new long[nc];
        long[] a = new long[nc];
        long[] b = new long[nc];
        for (int t = 0; t < nc; t++)
        {
            bool own = continental[cells[t]];
            bool oth = contFrac[t] >= 0.5;
            a[t] = plate[cells[t]];
            b[t] = other[cells[t]];
            kCells[t] = own && oth ? KindCollision : (own ^ oth ? KindOceanContinent : KindOceanOcean);
            sub[t] = -1;
            if (kCells[t] == KindOceanContinent)
            {
                sub[t] = own ? b[t] : a[t]; // 해양 쪽이 섭입
            }
        }
        // 해양-해양: 판 쌍마다 평균 나이를 비교해 더 늙은 쪽이 섭입 (같으면 번호가 작은 판)
        var oo = new List<int>();
        for (int t = 0; t < nc; t++)
        {
            if (kCells[t] == KindOceanOcean)
            {
                oo.Add(t);
            }
        }
        if (oo.Count > 0)
        {
            double[] sums = new double[nPlates * nPlates];
            double[] cnts = new double[nPlates * nPlates];
            foreach (int t in oo)
            {
                sums[(a[t] * nPlates) + b[t]] += ageOcean[cells[t]];
            }
            foreach (int t in oo)
            {
                cnts[(a[t] * nPlates) + b[t]] += 1.0;
            }
            foreach (int t in oo)
            {
                if (double.IsFinite(otherAge[t]))
                {
                    sums[(b[t] * nPlates) + a[t]] += otherAge[t];
                }
            }
            foreach (int t in oo)
            {
                if (double.IsFinite(otherAge[t]))
                {
                    cnts[(b[t] * nPlates) + a[t]] += 1.0;
                }
            }
            foreach (int t in oo)
            {
                double ageA = sums[(a[t] * nPlates) + b[t]] / cnts[(a[t] * nPlates) + b[t]];
                double ageB = sums[(b[t] * nPlates) + a[t]] / cnts[(b[t] * nPlates) + a[t]];
                sub[t] = ageA > ageB ? a[t] : (ageB > ageA ? b[t] : Math.Min(a[t], b[t]));
            }
        }
        for (int t = 0; t < nc; t++)
        {
            kind[cells[t]] = kCells[t];
            side[cells[t]] = (sbyte)(sub[t] < 0 ? 0 : (sub[t] == a[t] ? -1 : 1));
        }
        return (kind, side);
    }

    /// <summary>판 무게중심 단위 벡터 (M, 3) (_plate_centroids).</summary>
    internal static double[] PlateCentroids(double[] unit, double[] area, int[] plate, int m)
    {
        double[] cen = new double[m * 3];
        for (int ax = 0; ax < 3; ax++)
        {
            for (int c = 0; c < plate.Length; c++)
            {
                cen[(plate[c] * 3) + ax] += unit[(c * 3) + ax] * area[c]; // bincount(weights)
            }
        }
        double[] output = new double[m * 3];
        for (int k = 0; k < m; k++)
        {
            double norm = LinAlg.Norm3(cen.AsSpan(k * 3, 3));
            for (int ax = 0; ax < 3; ax++)
            {
                output[(k * 3) + ax] = norm > 0 ? cen[(k * 3) + ax] / norm : 0.0;
            }
        }
        return output;
    }

    /// <summary>c_j 에서 c_k 쪽으로 미는 회전축 normalize(c_j × c_k) (_away_axis).</summary>
    internal static double[] AwayAxis(ReadOnlySpan<double> cJ, ReadOnlySpan<double> cK)
    {
        double[] axis = new double[3];
        LinAlg.Cross3(cJ, cK, axis);
        double norm = LinAlg.Norm3(axis);
        if (norm < 1e-9)
        {
            double[] e = new double[3];
            int arg = 0;
            double best = Math.Abs(cK[0]);
            for (int i = 1; i < 3; i++)
            {
                if (Math.Abs(cK[i]) < best)
                {
                    best = Math.Abs(cK[i]);
                    arg = i;
                }
            }
            e[arg] = 1.0;
            LinAlg.Cross3(cK, e, axis);
            norm = LinAlg.Norm3(axis);
        }
        return [axis[0] / norm, axis[1] / norm, axis[2] / norm];
    }

    /// <summary>
    /// 해양 면적이 절반을 넘는 판마다 발산 경계 칸이 생기도록 ω 를 고칩니다 (ensure_divergent_boundaries).
    /// </summary>
    public static DivergentFix EnsureDivergentBoundaries(
        CellGraph graph, int[] plateId, double[] omega, bool[] continental, double kappa, double minRate,
        int maxRounds = MaxFlipRounds)
    {
        CheckSphereGraph(graph);
        CheckBoolMask(continental, graph.NCells, "continental");
        if (maxRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRounds), "max_rounds 는 0 이상이어야 합니다");
        }
        double[] om = (double[])omega.Clone();
        int m = om.Length / 3;
        double[] area = graph.Area;
        double[] plateArea = new double[m];
        double[] oceanArea = new double[m];
        for (int c = 0; c < plateId.Length; c++)
        {
            plateArea[plateId[c]] += area[c];
        }
        for (int c = 0; c < plateId.Length; c++)
        {
            oceanArea[plateId[c]] += area[c] * (continental[c] ? 0.0 : 1.0);
        }
        bool[] mostlyOcean = new bool[m];
        for (int k = 0; k < m; k++)
        {
            mostlyOcean[k] = NpMath.NanToNum(oceanArea[k] / plateArea[k]) > 0.5;
        }
        double[] centroids = PlateCentroids(graph.Unit(), area, plateId, m);
        bool[] flipped = new bool[m];
        var log = new List<FlipRecord>();
        BoundaryClassification cls = null!;
        var failing = new List<int>();
        for (int rnd = 0; rnd <= maxRounds; rnd++)
        {
            cls = ClassifyBoundaries(graph, plateId, om, kappa);
            bool[] hasDiv = new bool[m];
            for (int c = 0; c < plateId.Length; c++)
            {
                if (cls.BoundaryType[c] == Divergent)
                {
                    hasDiv[plateId[c]] = true;
                }
            }
            failing = [];
            for (int k = 0; k < m; k++)
            {
                if (mostlyOcean[k] && !hasDiv[k] && plateArea[k] > 0)
                {
                    failing.Add(k);
                }
            }
            if (failing.Count == 0 || rnd == maxRounds)
            {
                break;
            }
            bool[] changed = new bool[m];
            foreach (int k in failing)
            {
                var nbSet = new SortedSet<int>();
                for (int c = 0; c < plateId.Length; c++)
                {
                    if (plateId[c] == k && cls.Other[c] >= 0)
                    {
                        nbSet.Add(cls.Other[c]);
                    }
                }
                if (nbSet.Count == 0)
                {
                    continue;
                }
                bool anyChanged = false;
                foreach (int v in nbSet)
                {
                    anyChanged |= changed[v];
                }
                if (anyChanged)
                {
                    continue;
                }
                int j = -1;
                double bestArea = double.NegativeInfinity;
                foreach (int v in nbSet)
                {
                    if (j < 0 || plateArea[v] > bestArea)
                    {
                        j = v;
                        bestArea = plateArea[v];
                    }
                }
                string method;
                if (!flipped[k])
                {
                    for (int i = 0; i < 3; i++)
                    {
                        om[(k * 3) + i] = -om[(k * 3) + i];
                    }
                    method = "negate";
                }
                else
                {
                    double rate = NpMath.PyMax(LinAlg.Norm3(om.AsSpan(k * 3, 3)), minRate);
                    double[] ax = AwayAxis(centroids.AsSpan(j * 3, 3), centroids.AsSpan(k * 3, 3));
                    for (int i = 0; i < 3; i++)
                    {
                        om[(k * 3) + i] = om[(j * 3) + i] + (rate * ax[i]);
                    }
                    method = "away_from_neighbor";
                }
                flipped[k] = true;
                changed[k] = true;
                log.Add(new FlipRecord(rnd + 1, k, j, method));
            }
        }
        return new DivergentFix(om, log, cls, failing);
    }

    private static int[] FlatNonZero(bool[] mask)
    {
        var o = new List<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i])
            {
                o.Add(i);
            }
        }
        return o.ToArray();
    }

    /// <summary>
    /// 판 필드를 만듭니다 (generate_plates, docs/pipeline.md 4.1). 반환 fields 는 Python 과 같은 이름·순서이고,
    /// info 는 manifest meta.info.plates 에 그대로 쓰이는 dict 입니다.
    /// </summary>
    public static (FieldSet Fields, OrderedDictionary<string, object?> Info) GeneratePlates(
        CellGraph graph, bool[] continental, Config cfg)
    {
        CheckSphereGraph(graph);
        int n = graph.NCells;
        bool[] cont = CheckBoolMask(continental, n, "continental");
        int m = (int)cfg.I("plates.count");
        double kappa = cfg.F("plates.boundary_kappa");
        double cap = cfg.F("ocean.age_cap_myr");
        double[] unit = graph.Unit();
        double[] area = graph.Area;

        double[] seeds = PlateSeeds(cfg);
        int[] plate = AssignPlates(unit, seeds, cfg);
        double[] omegaInitial = PlateOmegas(cfg, graph.R!.Value);
        double[] plateArea = new double[m];
        double[] oceanArea = new double[m];
        for (int c = 0; c < n; c++)
        {
            plateArea[plate[c]] += area[c];
        }
        for (int c = 0; c < n; c++)
        {
            oceanArea[plate[c]] += area[c] * (cont[c] ? 0.0 : 1.0);
        }
        double[] oceanFrac = new double[m];
        for (int k = 0; k < m; k++)
        {
            oceanFrac[k] = plateArea[k] > 0 ? oceanArea[k] / plateArea[k] : NpMath.NaN;
        }
        double minRate = cfg.FArray("plates.speed_m_per_yr")[0] / graph.R!.Value;
        DivergentFix fix = EnsureDivergentBoundaries(graph, plate, omegaInitial, cont, kappa, minRate);
        BoundaryClassification cls = fix.Cls;

        byte[] btype = cls.BoundaryType;
        double[] halfGap = cls.HalfGap;
        bool[] isConv = new bool[n];
        bool[] isDiv = new bool[n];
        for (int c = 0; c < n; c++)
        {
            isConv[c] = btype[c] == Convergent;
            isDiv[c] = btype[c] == Divergent;
        }
        // 경계 칸 γ 의 격자 잡음을 같은 종류·같은 판 경계를 따라 지웁니다 (판정은 그대로).
        double[] gamma = (double[])cls.Gamma.Clone();
        double[] gDiv = SmoothAlongBoundary(cls.Gamma, isDiv, plate, graph.Nbr);
        double[] gConv = SmoothAlongBoundary(cls.Gamma, isConv, plate, graph.Nbr);
        for (int c = 0; c < n; c++)
        {
            if (isDiv[c])
            {
                gamma[c] = gDiv[c];
            }
        }
        for (int c = 0; c < n; c++)
        {
            if (isConv[c])
            {
                gamma[c] = gConv[c];
            }
        }
        CellGraph gSame = SamePlateGraph(graph, plate);

        // 발산: 같은 판 해령까지 (흐름선 근사). 판에 해령이 없으면 가장 가까운 아무 해령.
        (double[] distDiv, long[] srcDiv) = BoundaryDistance(gSame, isDiv, halfGap);
        bool anyMissing = Array.Exists(srcDiv, s => s < 0);
        bool anyDiv = Array.IndexOf(isDiv, true) >= 0;
        if (anyMissing && anyDiv)
        {
            (double[] dG, long[] sG) = BoundaryDistance(graph, isDiv, halfGap);
            for (int c = 0; c < n; c++)
            {
                if (srcDiv[c] < 0)
                {
                    distDiv[c] = dG[c];
                    srcDiv[c] = sG[c];
                }
            }
        }
        double[] spreadingKin = new double[n];
        for (int c = 0; c < n; c++)
        {
            double v = srcDiv[c] >= 0 ? -0.5 * gamma[Math.Max(srcDiv[c], 0)] : 0.0;
            spreadingKin[c] = Math.Max(v, 0.0);
        }
        double[] rateFloor = AgeConsistentRateFloor(distDiv, plate, cont, m, cap);
        double[] spreading = new double[n];
        for (int c = 0; c < n; c++)
        {
            spreading[c] = Math.Max(spreadingKin[c], rateFloor[plate[c]]);
        }
        double[] ageProv = SeafloorAgeMyr(distDiv, spreading, cap);

        (byte[] kindCell, sbyte[] sideCell) = SubductionPolarity(graph, plate, cls.Other, isConv, cont, ageProv, m);
        (double[] distConv, long[] srcConv) = BoundaryDistance(gSame, isConv, halfGap);
        double[] convergence = new double[n];
        byte[] kind = new byte[n];
        sbyte[] side = new sbyte[n];
        for (int c = 0; c < n; c++)
        {
            bool has = srcConv[c] >= 0;
            long sc = Math.Max(srcConv[c], 0);
            convergence[c] = has ? gamma[sc] : 0.0;
            kind[c] = has ? kindCell[sc] : KindNone;
            side[c] = has ? sideCell[sc] : (sbyte)0;
        }

        var fields = new FieldSet
        {
            ["plate_id"] = plate,
            ["boundary_type"] = btype,
            ["convergence_m_per_yr"] = convergence,
            ["convergence_kind"] = kind,
            ["subduction_side"] = side,
            ["dist_convergent_m"] = distConv,
            ["dist_divergent_m"] = distDiv,
            ["spreading_m_per_yr"] = spreading,
        };
        var flips = new List<object?>();
        foreach (FlipRecord f in fix.Flips)
        {
            flips.Add(new OrderedDictionary<string, object?>
            {
                ["round"] = (long)f.Round,
                ["plate"] = (long)f.Plate,
                ["neighbor"] = (long)f.Neighbor,
                ["method"] = f.Method,
            });
        }
        var remaining = new List<object?>();
        foreach (int k in fix.Remaining)
        {
            remaining.Add((long)k);
        }
        long nConv = 0;
        long nDivC = 0;
        long nTr = 0;
        foreach (byte t in btype)
        {
            nConv += t == Convergent ? 1 : 0;
            nDivC += t == Divergent ? 1 : 0;
            nTr += t == Transform ? 1 : 0;
        }
        var info = new OrderedDictionary<string, object?>
        {
            ["seeds"] = new NpyArray("<f8", [m, 3], seeds),
            ["omegas"] = new NpyArray("<f8", [m, 3], fix.Omega),
            ["omegas_initial"] = new NpyArray("<f8", [m, 3], omegaInitial),
            ["flips"] = flips,
            ["ocean_fraction"] = oceanFrac,
            ["plate_area_m2"] = plateArea,
            ["plates_without_divergent"] = remaining,
            ["n_boundary"] = new OrderedDictionary<string, object?>
            {
                ["convergent"] = nConv,
                ["divergent"] = nDivC,
                ["transform"] = nTr,
            },
            ["age_provisional_myr"] = ageProv,
            ["spreading_kinematic_m_per_yr"] = spreadingKin,
            ["spreading_floor_m_per_yr"] = rateFloor,
        };
        return (fields, info);
    }
}
