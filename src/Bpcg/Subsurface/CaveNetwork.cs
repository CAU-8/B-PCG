using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Subsurface;

/// <summary>동굴 통로 그물을 만들 때 쓰는 값들.</summary>
/// <param name="RadiusMaxM">통로가 가장 굵을 때의 반지름 [m] (caves.passage_radius_m).</param>
/// <param name="FullSizeAreaM2">통로가 가장 굵어지는, 싱크홀로 모인 빗물 넓이 [m²] (caves.full_size_area_m2).</param>
/// <param name="SinkSpacingM">싱크홀 사이의 최소 거리 [m] (caves.sink_spacing_m).</param>
/// <param name="SinkMinAreaM2">빗물이 이 넓이 이상 모이는 칸만 싱크홀 후보 [m²] (caves.sink_min_area_m2).</param>
/// <param name="PathWavelengthM">물이 잘 통하는 자리와 덜 통하는 자리가 번갈아 놓이는 간격 [m] (caves.noise_wavelength_m).</param>
/// <param name="Seed">시드 (planet.seed).</param>
/// <param name="LakeSpacingM">동굴 호수가 생기는 방 사이의 최소 거리 [m] (caves.lake_spacing_m). 0 이면 방을 두지 않습니다.</param>
/// <param name="ChamberRadiusM">방의 반지름 [m] (caves.chamber_radius_m). 0 이면 방을 두지 않습니다.</param>
public sealed record CaveNetworkParams(
    double RadiusMaxM, double FullSizeAreaM2, double SinkSpacingM, double SinkMinAreaM2, double PathWavelengthM, long Seed,
    double LakeSpacingM = 0.0, double ChamberRadiusM = 0.0);

/// <summary>
/// 동굴 통로 그물: 싱크홀(지표의 물이 땅속으로 들어가는 자리)에서 샘(땅속의 물이 지표로 나오는 자리)까지 물이 흐르는 길.
/// </summary>
/// <remarks>
/// 왜 필요한가: 지구의 석회암 동굴은 빗물이 땅속으로 들어가 샘으로 나오는 물길을 따라 자랍니다. 물길은 하류로 가며 합쳐지므로
/// 통로가 나뭇가지 꼴이 되고, 물이 많이 모인 하류일수록 통로가 굵습니다. 무작위 무늬로 통로를 놓으면 물이 들어가는 곳도
/// 나오는 곳도 없는 닫힌 고리가 됩니다.
/// 만드는 순서:
/// 1. 층마다 샘 칸에서 출발해, 동굴 층이 있는 칸으로 '가장 힘이 덜 드는 길'을 넓혀 갑니다(Dijkstra). 칸마다 물이 흘러갈
///    다음 칸 하나가 정해지고, 이 길들은 샘으로 갈수록 합쳐집니다. 힘은 거리 × 그 자리의 저항이고, 저항은 암석의 틈이
///    많고 적은 정도를 흉내 낸 잡음으로 정합니다. 아래층(지금 물이 흐르는 층)에서는 물 높이가 낮아지는 쪽으로만 잇습니다.
/// 2. 석회암 지표에서 빗물이 모이는 칸 가운데 서로 떨어진 칸을 싱크홀로 고릅니다.
/// 3. 싱크홀마다 샘까지 길을 따라가며, 지나는 칸에 그 싱크홀로 모인 빗물 넓이를 더합니다. 넓이가 0 보다 큰 칸이 통로입니다.
/// 4. 통로 반지름은 모인 넓이의 제곱근에 비례합니다(단면 넓이가 물의 양에 비례). 싱크홀에서 가장 낮은 층까지 수직굴을 둡니다.
/// 5. 아래층 통로의 몇 곳을 넓고 깊은 방으로 만듭니다. 물 높이는 그대로라 방에는 물이 깊게 고입니다(동굴 호수).
/// 계산에는 더하기, 곱하기, 제곱근만 씁니다. 지수·로그 함수는 운영체제마다 끝자리가 달라, 길을 고르는 비교가 달라질 수 있습니다.
/// </remarks>
public sealed class CaveNetwork
{
    /// <summary>
    /// 통로 반지름의 아래 한계 [m]. 동굴 메시는 2 m 칸으로 만듭니다. 반지름이 2 m 보다 작은 관은 칸 사이로 빠져
    /// 메시가 토막 나므로 이보다 가늘게 두지 않습니다.
    /// </summary>
    public const double RadiusMinM = 2.0;

    /// <summary>싱크홀을 둘 수 있는 가장 얕은 깊이 [m]: 지표에서 동굴 층까지 이만큼 이상일 때만 둡니다.</summary>
    public const double SinkMinDepthM = 10.0;

    /// <summary>
    /// 아래층 통로를 물 높이 위로 올리는 정도 (반지름의 배수). 0.4 면 관의 바닥이 물 높이보다 반지름의 0.6 배 아래라,
    /// 통로 바닥에 얕은 물이 흐릅니다 (반지름 3 m 면 물 깊이 1.8 m).
    /// </summary>
    public const double StreamLift = 0.4;

    /// <summary>
    /// 방의 가운데를 물 높이보다 낮추는 정도 (방 반지름의 배수). −0.2 면 방의 가운데가 물 높이보다 반지름의 0.2 배 아래라,
    /// 물 깊이는 반지름의 1.2 배, 물 위의 빈 높이는 반지름의 0.8 배입니다 (반지름 6 m 면 물 깊이 7.2 m, 빈 높이 4.8 m).
    /// </summary>
    public const double ChamberLift = -0.2;

    /// <summary>방을 둘 수 있는 가장 얕은 깊이 (방 반지름의 배수): 지표에서 물 높이까지 이만큼 이상일 때만 둡니다.</summary>
    public const double ChamberMinCoverRadii = 3.0;

    /// <summary>저항의 범위: 물이 가장 잘 통하는 자리와 가장 덜 통하는 자리 (거리 1 m 를 가는 데 드는 힘).</summary>
    public const double ResistanceMin = 0.25;
    public const double ResistanceMax = 4.0;

    private const int PathOctaves = 3;
    private const int SmoothPasses = 2;
    private const double LevelEps = 1.0e-6;

    // 해시·잡음의 용도 번호.
    private const long StreamSink = 7261;
    private const long StreamPath = 7262;
    private const long StreamLake = 7263;

    private CaveNetwork()
    {
    }

    /// <summary>동굴 층 수 K.</summary>
    public int NLevels { get; private init; }

    /// <summary>통로 조각의 한쪽 끝 (E, 3) [m]. 층 통로는 상류 쪽 끝, 수직굴은 지표 쪽 끝.</summary>
    public double[] SegA { get; private init; } = [];

    /// <summary>통로 조각의 다른 쪽 끝 (E, 3) [m].</summary>
    public double[] SegB { get; private init; } = [];

    /// <summary>통로 조각의 반지름 (E,) [m].</summary>
    public double[] SegRadius { get; private init; } = [];

    /// <summary>조각이 수직굴이면 참 (E,). 수직굴만 지표로 열립니다.</summary>
    public bool[] SegIsShaft { get; private init; } = [];

    /// <summary>조각이 동굴 호수가 생기는 방이면 참 (E,). 방 조각은 층 통로와 수직굴 뒤에 놓입니다.</summary>
    public bool[] SegIsChamber { get; private init; } = [];

    /// <summary>동굴 호수가 생기는 방의 칸 번호 (오름차순). 아래층에만 둡니다.</summary>
    public int[] Lakes { get; private init; } = [];

    /// <summary>방마다 가운데의 x, y 와 물 높이 (L, 3) [m]. Lakes 와 같은 순서.</summary>
    public double[] LakeXyz { get; private init; } = [];

    /// <summary>싱크홀 칸 번호 (오름차순).</summary>
    public int[] Sinks { get; private init; } = [];

    /// <summary>(K·N) 층 k 의 칸 c 를 지나는 물이 지표에서 모인 넓이 [m²] (index k·N + c). 통로가 없으면 0.</summary>
    public double[] Flow { get; private init; } = [];

    /// <summary>(K·N) 층 k 에서 칸 c 의 물이 흘러가는 다음 칸. 샘 칸과 길이 닿지 않는 칸은 −1.</summary>
    public int[] Parent { get; private init; } = [];

    /// <summary>(N,) 칸마다 그 칸에서 하류로 나가는 층 통로의 길이 합 [m].</summary>
    public double[] PassageM { get; private init; } = [];

    public int NSegments => SegRadius.Length;

    /// <summary>모인 넓이 [m²] 로 통로 반지름 [m] 을 정합니다: r = r_max · sqrt(넓이 ÷ 가득 찬 넓이), RadiusMinM 과 r_max 사이로 자름.</summary>
    public static double Radius(double flowAreaM2, double radiusMaxM, double fullSizeAreaM2)
    {
        double r = radiusMaxM * Math.Sqrt(Math.Max(flowAreaM2, 0.0) / fullSizeAreaM2);
        return Math.Min(Math.Max(r, Math.Min(RadiusMinM, radiusMaxM)), radiusMaxM);
    }

    /// <summary>잡음 값(대략 −1~1)을 저항으로 바꿉니다. 0 이하의 잡음은 잘 통하는 자리, 큰 잡음은 덜 통하는 자리입니다.</summary>
    public static double Resistance(double noise)
    {
        double t = Math.Min(Math.Max(0.5 + noise, 0.0), 1.0);
        return ResistanceMin + ((ResistanceMax - ResistanceMin) * t * t);
    }

    /// <summary>
    /// 통로 그물을 만듭니다.
    /// z: (N,) 지표 높이 [m]. levels: (K·N) 층 k 의 높이 [m] (index k·N + c), 동굴 층이 없는 칸은 NaN.
    /// outlet: (K·N) 층 k 의 샘 칸(입구 칸 가운데 산 밖으로 열리는 칸). sinkAllowed: (N,) 싱크홀을 둘 수 있는 칸
    /// (지표 암석이 물에 녹고, 물에 잠기지 않은 육지 칸). drainageAreaM2: (N,) 그 칸까지 빗물이 모이는 넓이 [m²].
    /// </summary>
    public static CaveNetwork Build(
        CellGraph graph, double[] z, double[] levels, bool[] outlet, bool[] sinkAllowed, double[] drainageAreaM2,
        CaveNetworkParams p)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException($"동굴 통로 그물은 평면 그래프에서만 만듭니다. 받은 그래프: '{graph.Kind}'");
        }
        int n = graph.NCells;
        if (z.Length != n || sinkAllowed.Length != n || drainageAreaM2.Length != n)
        {
            throw new ArgumentException(
                $"z, sinkAllowed, drainageAreaM2 는 칸 수 ({n},) 와 같은 길이여야 합니다. "
                + $"받은 길이: {z.Length}, {sinkAllowed.Length}, {drainageAreaM2.Length}");
        }
        if (levels.Length == 0 || levels.Length % n != 0 || outlet.Length != levels.Length)
        {
            throw new ArgumentException(
                $"levels 와 outlet 은 (층 수 × {n},) 길이여야 합니다. 받은 길이: levels {levels.Length}, outlet {outlet.Length}");
        }
        if (!(p.RadiusMaxM > 0.0 && p.FullSizeAreaM2 > 0.0 && p.SinkSpacingM >= 0.0 && p.SinkMinAreaM2 >= 0.0 && p.PathWavelengthM > 0.0))
        {
            throw new ArgumentException(
                "동굴 통로 설정은 passage_radius_m > 0, full_size_area_m2 > 0, sink_spacing_m ≥ 0, sink_min_area_m2 ≥ 0, "
                + $"noise_wavelength_m > 0 이어야 합니다. 받은 값: {p.RadiusMaxM}, {p.FullSizeAreaM2}, {p.SinkSpacingM}, "
                + $"{p.SinkMinAreaM2}, {p.PathWavelengthM}");
        }
        if (!(p.LakeSpacingM >= 0.0 && p.ChamberRadiusM >= 0.0))
        {
            throw new ArgumentException(
                $"동굴 호수 설정은 lake_spacing_m ≥ 0, chamber_radius_m ≥ 0 이어야 합니다. 받은 값: {p.LakeSpacingM}, {p.ChamberRadiusM}");
        }
        int kN = levels.Length / n;
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;

        // --- 1. 층마다 샘에서 넓혀 가는 가장 힘이 덜 드는 길
        double[] pts = new double[n * 3];
        for (int c = 0; c < n; c++)
        {
            pts[c * 3] = pos[c * 3];
            pts[(c * 3) + 1] = pos[(c * 3) + 1];
        }
        int[] parent = new int[kN * n];
        Array.Fill(parent, -1);
        bool[] reached = new bool[kN * n];
        for (int k = 0; k < kN; k++)
        {
            long pathSeed = (long)(Hashing.Hash3(p.Seed, StreamPath, k) >> 1);
            double[] noise = Noise.Fbm3(pts, pathSeed, PathOctaves, frequency: 1.0 / p.PathWavelengthM);
            double[] resist = new double[n];
            for (int c = 0; c < n; c++)
            {
                resist[c] = Resistance(noise[c]);
            }
            double[] cost = new double[n];
            Array.Fill(cost, double.PositiveInfinity);
            var queue = new PriorityQueue<int, (double Cost, int Cell)>();
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                if (outlet[off + c] && double.IsFinite(levels[off + c]))
                {
                    cost[c] = 0.0;
                    queue.Enqueue(c, (0.0, c));
                }
            }
            // 아래층은 지금 물이 흐르는 층이라, 물 높이가 샘 쪽으로 낮아지는 길만 잇습니다. 위층은 옛 물길이라 높이를 따지지 않습니다.
            bool downhillOnly = k == 0;
            while (queue.TryDequeue(out int v, out (double Cost, int Cell) pr))
            {
                if (pr.Cost > cost[v])
                {
                    continue;
                }
                reached[off + v] = true;
                double lv = levels[off + v];
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int u = nbr[(v * CellGraph.NSlots) + s];
                    if (u < 0)
                    {
                        continue;
                    }
                    double lu = levels[off + u];
                    if (!double.IsFinite(lu) || (downhillOnly && lu < lv - LevelEps))
                    {
                        continue;
                    }
                    double next = pr.Cost + (dist[(v * CellGraph.NSlots) + s] * 0.5 * (resist[v] + resist[u]));
                    if (next < cost[u] || (next == cost[u] && parent[off + u] >= 0 && v < parent[off + u]))
                    {
                        cost[u] = next;
                        parent[off + u] = v;
                        queue.Enqueue(u, (next, u));
                    }
                }
            }
        }

        // --- 2. 싱크홀: 석회암 지표에서 빗물이 모이고, 아래에 샘까지 길이 닿는 동굴 층이 있는 칸
        var candidates = new List<int>();
        for (int c = 0; c < n; c++)
        {
            if (sinkAllowed[c] && drainageAreaM2[c] >= p.SinkMinAreaM2 && FeedsAnyLevel(c))
            {
                candidates.Add(c);
            }
        }
        int[] sinks = Thin(candidates, pos, p.SinkSpacingM, p.Seed, StreamSink);

        // --- 3. 싱크홀마다 샘까지 따라가며 모인 넓이를 더합니다
        double[] flow = new double[kN * n];
        foreach (int s in sinks)
        {
            for (int k = 0; k < kN; k++)
            {
                if (!Feeds(s, k))
                {
                    continue;
                }
                int c = s;
                for (int step = 0; c >= 0 && step <= n; step++)
                {
                    flow[(k * n) + c] += drainageAreaM2[s];
                    c = parent[(k * n) + c];
                }
            }
        }

        // --- 4. 통로가 지나는 자리: 칸의 대표점(흔들어 둔 점)을 잇고, 꺾임을 두 번 다듬습니다. 샘 칸은 칸 한가운데에 둡니다.
        bool[] isSink = new bool[n];
        foreach (int s in sinks)
        {
            isSink[s] = true;
        }
        double[] xy = new double[kN * n * 2];
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                bool atOutlet = outlet[off + c];
                xy[(off + c) * 2] = atOutlet ? graph.Origin.East + (((c % nx) + 0.5) * dx) : pos[c * 3];
                xy[((off + c) * 2) + 1] = atOutlet ? graph.Origin.North - (((c / nx) + 0.5) * dx) : pos[(c * 3) + 1];
            }
            for (int pass = 0; pass < SmoothPasses; pass++)
            {
                double[] sumX = new double[n];
                double[] sumY = new double[n];
                int[] count = new int[n];
                for (int c = 0; c < n; c++)
                {
                    int q = parent[off + c];
                    if (flow[off + c] > 0.0 && q >= 0)
                    {
                        sumX[q] += xy[(off + c) * 2];
                        sumY[q] += xy[((off + c) * 2) + 1];
                        count[q]++;
                    }
                }
                double[] next = new double[n * 2];
                for (int c = 0; c < n; c++)
                {
                    double x = xy[(off + c) * 2];
                    double y = xy[((off + c) * 2) + 1];
                    int q = parent[off + c];
                    // 샘 칸, 싱크홀 칸, 상류 끝은 그대로 둡니다 (입구 굴과 수직굴이 그 자리에 이어집니다).
                    if (flow[off + c] > 0.0 && q >= 0 && count[c] > 0 && !isSink[c])
                    {
                        x = ((2.0 * x) + xy[(off + q) * 2] + (sumX[c] / count[c])) / 4.0;
                        y = ((2.0 * y) + xy[((off + q) * 2) + 1] + (sumY[c] / count[c])) / 4.0;
                    }
                    next[c * 2] = x;
                    next[(c * 2) + 1] = y;
                }
                Array.Copy(next, 0, xy, off * 2, n * 2);
            }
        }

        // --- 5. 통로 조각과 수직굴
        var segA = new List<double>();
        var segB = new List<double>();
        var segR = new List<double>();
        var segShaft = new List<bool>();
        double[] passageM = new double[n];
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                int q = parent[off + c];
                if (!(flow[off + c] > 0.0) || q < 0)
                {
                    continue;
                }
                double r = Radius(flow[off + c], p.RadiusMaxM, p.FullSizeAreaM2);
                double lift = k == 0 ? StreamLift * r : 0.0;
                double ax = xy[(off + c) * 2];
                double ay = xy[((off + c) * 2) + 1];
                double az = levels[off + c] + lift;
                double bx = xy[(off + q) * 2];
                double by = xy[((off + q) * 2) + 1];
                double bz = levels[off + q] + lift;
                segA.AddRange([ax, ay, az]);
                segB.AddRange([bx, by, bz]);
                segR.Add(r);
                segShaft.Add(false);
                passageM[c] += Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)) + ((bz - az) * (bz - az)));
            }
        }
        foreach (int s in sinks)
        {
            double bottom = double.PositiveInfinity;
            for (int k = 0; k < kN; k++)
            {
                if (Feeds(s, k))
                {
                    bottom = Math.Min(bottom, levels[(k * n) + s]);
                }
            }
            double r = Radius(drainageAreaM2[s], p.RadiusMaxM, p.FullSizeAreaM2);
            // 수직굴은 지표 위 반지름만큼에서 시작해 지표를 뚫고, 가장 낮은 층의 통로까지 내려갑니다.
            segA.AddRange([pos[s * 3], pos[(s * 3) + 1], z[s] + r]);
            segB.AddRange([pos[s * 3], pos[(s * 3) + 1], bottom]);
            segR.Add(r);
            segShaft.Add(true);
        }

        // --- 6. 동굴 호수가 생기는 방 (아래층만)
        // 물이 흐르는 통로의 몇 칸을 골라, 그 칸에서 이웃 칸까지의 절반씩을 넓고 깊은 방으로 만듭니다. 방은 원래 통로에 겹쳐 놓는
        // 굵은 조각이라 통로는 그대로 이어집니다. 물 높이는 그대로이고 방의 바닥만 낮아서, 방에는 물이 깊게 고입니다.
        int nPassage = segR.Count;
        int[] lakes = [];
        var lakeXyz = new List<double>();
        if (p.LakeSpacingM > 0.0 && p.ChamberRadiusM > 0.0)
        {
            double rc = p.ChamberRadiusM;
            int[] nUpstream = new int[n];
            for (int c = 0; c < n; c++)
            {
                int q = parent[c];
                if (flow[c] > 0.0 && q >= 0)
                {
                    nUpstream[q]++;
                }
            }
            var lakeCandidates = new List<int>();
            for (int c = 0; c < n; c++)
            {
                // 후보: 위에서 물이 들어오고 아래로 나가는 통로 칸. 싱크홀·샘 칸이 아니고, 방이 통로보다 굵고,
                // 지표에서 물 높이까지가 방 반지름의 ChamberMinCoverRadii 배 이상인 칸.
                if (flow[c] > 0.0 && parent[c] >= 0 && nUpstream[c] >= 1 && !isSink[c] && !outlet[c]
                    && rc > Radius(flow[c], p.RadiusMaxM, p.FullSizeAreaM2) && z[c] - levels[c] >= ChamberMinCoverRadii * rc)
                {
                    lakeCandidates.Add(c);
                }
            }
            lakes = Thin(lakeCandidates, pos, p.LakeSpacingM, p.Seed, StreamLake);
            bool[] isLake = new bool[n];
            foreach (int c in lakes)
            {
                isLake[c] = true;
                lakeXyz.AddRange([xy[c * 2], xy[(c * 2) + 1], levels[c]]);
            }
            double drop = ChamberLift * rc;
            for (int c = 0; c < n; c++)
            {
                int q = parent[c];
                if (!(flow[c] > 0.0) || q < 0)
                {
                    continue;
                }
                // 방 칸 c 에서 하류 칸 q 쪽 절반, 그리고 방 칸 q 로 들어오는 상류 칸 c 쪽 절반.
                if (isLake[c])
                {
                    AddHalf(c, q);
                }
                if (isLake[q])
                {
                    AddHalf(q, c);
                }
            }

            void AddHalf(int room, int other)
            {
                double x0 = xy[room * 2];
                double y0 = xy[(room * 2) + 1];
                double x1 = 0.5 * (x0 + xy[other * 2]);
                double y1 = 0.5 * (y0 + xy[(other * 2) + 1]);
                segA.AddRange([x0, y0, levels[room] + drop]);
                segB.AddRange([x1, y1, (0.5 * (levels[room] + levels[other])) + drop]);
                segR.Add(rc);
                segShaft.Add(false);
            }
        }
        bool[] segChamber = new bool[segR.Count];
        for (int e = nPassage; e < segChamber.Length; e++)
        {
            segChamber[e] = true;
        }
        return new CaveNetwork
        {
            NLevels = kN,
            SegA = segA.ToArray(),
            SegB = segB.ToArray(),
            SegRadius = segR.ToArray(),
            SegIsShaft = segShaft.ToArray(),
            SegIsChamber = segChamber,
            Sinks = sinks,
            Lakes = lakes,
            LakeXyz = lakeXyz.ToArray(),
            Flow = flow,
            Parent = parent,
            PassageM = passageM,
        };

        // 칸 c 의 물이 층 k 로 들어갈 수 있는지: 그 층이 있고, 샘까지 길이 닿고, 샘 칸이 아니고, 지표에서 충분히 깊을 때.
        bool Feeds(int c, int k)
        {
            int i = (k * n) + c;
            return double.IsFinite(levels[i]) && reached[i] && parent[i] >= 0 && z[c] - levels[i] >= SinkMinDepthM;
        }

        bool FeedsAnyLevel(int c)
        {
            for (int k = 0; k < kN; k++)
            {
                if (Feeds(c, k))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 싱크홀 후보를 솎습니다: spacing [m] 안에 우선순위가 더 높은 후보가 있으면 뺍니다. 우선순위는 시드와 칸 번호의 해시입니다.
    /// 남은 칸끼리는 spacing 넘게 떨어집니다. 반환: 남은 칸 번호 (오름차순).
    /// </summary>
    internal static int[] ThinSinks(List<int> candidates, double[] pos, double spacing, long seed) =>
        Thin(candidates, pos, spacing, seed, StreamSink);

    /// <summary>
    /// 후보 칸을 솎습니다 (싱크홀, 동굴 호수의 방). stream 은 우선순위 해시의 용도 번호라, 용도가 다르면 다른 칸이 남습니다.
    /// </summary>
    internal static int[] Thin(List<int> candidates, double[] pos, double spacing, long seed, long stream)
    {
        if (spacing <= 0.0 || candidates.Count == 0)
        {
            return candidates.ToArray();
        }
        var bucket = new Dictionary<(long, long), List<int>>();
        (long, long) Key(int c) => ((long)Math.Floor(pos[c * 3] / spacing), (long)Math.Floor(pos[(c * 3) + 1] / spacing));
        foreach (int c in candidates)
        {
            (long, long) key = Key(c);
            if (!bucket.TryGetValue(key, out List<int>? list))
            {
                list = [];
                bucket[key] = list;
            }
            list.Add(c);
        }
        ulong Priority(int c) => Hashing.Hash3(seed, stream, c);
        double s2 = spacing * spacing;
        var keep = new List<int>();
        foreach (int c in candidates)
        {
            ulong pc = Priority(c);
            (long bx, long by) = Key(c);
            bool dropped = false;
            for (long ix = -1; ix <= 1 && !dropped; ix++)
            {
                for (long iy = -1; iy <= 1 && !dropped; iy++)
                {
                    if (!bucket.TryGetValue((bx + ix, by + iy), out List<int>? list))
                    {
                        continue;
                    }
                    foreach (int v in list)
                    {
                        if (v == c)
                        {
                            continue;
                        }
                        double ex = pos[v * 3] - pos[c * 3];
                        double ey = pos[(v * 3) + 1] - pos[(c * 3) + 1];
                        if ((ex * ex) + (ey * ey) > s2)
                        {
                            continue;
                        }
                        ulong pv = Priority(v);
                        if (pv > pc || (pv == pc && v < c))
                        {
                            dropped = true;
                            break;
                        }
                    }
                }
            }
            if (!dropped)
            {
                keep.Add(c);
            }
        }
        return keep.ToArray();
    }
}
