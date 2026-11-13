using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hero;
using Bpcg.IO;
using Bpcg.Numerics;
using Bpcg.Volume;

namespace Bpcg.Bake;

/// <summary>
/// 회랑 굽기: 히어로에서 걸어 다닐 회랑을 골라 엔진 파일로 씁니다 (src/bpcg/bake/corridor.py, docs/pipeline.md 11장).
/// </summary>
public static class Corridor
{
    public const double StrataDxM = 4.0;
    public const double StrataDzM = 2.0;
    public const double WaterNoneM = -10_000.0;
    public const byte StrataWater = 254;

    // 층 안의 얇은 층(층리). 지층 하나가 150~800 m 로 두꺼워 단면에 암석마다 한 가지 색만 보이므로,
    // 층 바닥에서 잰 높이를 BedThicknessM 간격의 띠로 나누고 띠마다 해시로 '어두운 띠'를 고릅니다.
    // 어두운 띠는 재질 번호에 BedIdOffset 을 더해 굽고, 범례에 같은 암석의 어두운 색으로 올립니다.
    // 보기용입니다: 침식 계산(솔버)은 이 띠를 모르고, 암석 종류도 바뀌지 않습니다.
    public const double BedThicknessM = 15.0;   // 띠 하나의 두께. 어두운 띠가 이어 나오면 30 m, 45 m 띠가 됩니다
    public const double BedDarkFraction = 0.4;  // 어두운 띠의 비율
    public const double BedDarkShade = 0.82;    // 어두운 띠의 색 = 암석 색 × 이 값
    public const byte BedIdOffset = 16;         // 어두운 띠의 재질 번호 = 암석 번호 + 16 (17..26)
    // 지표에서 이만큼(층 수)보다 얕은 곳에는 어두운 띠를 굽지 않습니다. 엔진의 지표 셰이더가 지표 아래 10 m 의
    // 재질 번호(0..11)로 땅 색을 정하므로, 그 안에는 원래 번호만 둡니다.
    public const int BedMinLayer = 8;
    private const long StreamBeds = 7301;

    /// <summary>층리를 그리는 암석인지. 쌓여서 생긴 암석과 줄무늬가 있는 변성암만입니다. 화강암은 덩어리 암석이라 층이 없고, 흙·충적층·물·공기도 뺍니다.</summary>
    public static bool HasBeds(byte rock) => rock >= Rocks.Sandstone && rock <= Rocks.Quartzite && rock != Rocks.Granite;
    public const byte StrataAir = HeroVolume.MaterialEmpty;
    public const int MaxCandidates = 256;
    // 본류 밖의 강(지류) 위에 놓는 회랑 시작점 후보 수의 상한.
    public const int MaxTributaryCandidates = 1024;
    public const int StrataChunkPoints = 4_000_000;
    public const double WaterSurfaceTolM = 0.01;
    public const double CaveMouthClipM = 20.0;
    public static readonly string[] DetailStems = ["heightmap_detail", "cave_mouth_detail"];
    public const int DetailMinSamples = 8;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    private static void RemoveStems(string folder, IEnumerable<string> stems)
    {
        foreach (string stem in stems)
        {
            foreach (string ext in new[] { ".bin", ".json" })
            {
                string p = Path.Combine(folder, stem + ext);
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    private static (double[] X, double[] Y) Centers(CellGraph graph)
    {
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        double[] x = new double[ny * nx];
        double[] y = new double[ny * nx];
        for (int c = 0; c < x.Length; c++)
        {
            x[c] = graph.Origin.East + (((c % nx) + 0.5) * dx);
            y[c] = graph.Origin.North - (((c / nx) + 0.5) * dx);
        }
        return (x, y);
    }

    /// <summary>칸마다 유량이 가장 큰 기여 셀 (없으면 −1) (best_donor). 같은 유량이면 칸 번호가 작은 쪽.</summary>
    public static long[] BestDonor(long[] receiver, double[] discharge)
    {
        int n = receiver.Length;
        long[] o = new long[n];
        Array.Fill(o, -1L);
        for (int d = 0; d < n; d++)
        {
            long r = receiver[d];
            if (r == d)
            {
                continue;
            }
            long cur = o[r];
            if (cur < 0 || discharge[d] > discharge[cur] || (discharge[d] == discharge[cur] && d < cur))
            {
                o[r] = d;
            }
        }
        return o;
    }

    /// <summary>start 를 지나는 본류 (하류 끝 출구 → 상류 끝) 칸 번호와 그 안의 start 위치 (main_stem).</summary>
    public static (long[] Path, int StartIndex) MainStem(long[] receiver, double[] discharge, long start)
    {
        int n = receiver.Length;
        var down = new List<long> { start };
        var seen = new HashSet<long> { start };
        while (receiver[down[^1]] != down[^1] && down.Count <= n)
        {
            long nxt = receiver[down[^1]];
            if (!seen.Add(nxt))
            {
                throw new ArgumentException("receiver 에 순환이 있습니다");
            }
            down.Add(nxt);
        }
        long[] donor = BestDonor(receiver, discharge);
        var up = new List<long>();
        long c = start;
        while (donor[c] >= 0 && up.Count <= n)
        {
            c = donor[c];
            up.Add(c);
        }
        down.Reverse();
        long[] path = down.Concat(up).ToArray();
        return (path, down.Count - 1);
    }

    /// <summary>회랑 직사각형 고르기의 결과.</summary>
    /// <remarks>
    /// Score 는 후보끼리 비교하는 점수(0~5)이고, 다섯 항목의 합입니다: 지표 암석이 고루 섞인 정도 RockDiversity,
    /// 완만한 땅부터 가파른 비탈까지 고루 섞인 정도 SlopeDiversity, 동굴이 있는 정도 Caves, 강이 있는 정도 Stem,
    /// 호수가 있는 정도 Lake (모두 0~1).
    /// Stem 은 회랑을 길이 방향으로 히어로 칸 간격마다 자른 조각 가운데 강이나 호수 칸이 있는 조각의 몫입니다
    /// (강이 회랑을 끝에서 끝까지 따라가면 1). Lake 는 회랑 가장자리에서 LakeEdgeMarginM 넘게 안쪽에 든 호수 넓이를
    /// EnoughLakeAreaM2 로 나눈 값입니다 (1 에서 자름). NLakeCells 는 그렇게 센 호수 칸 수입니다.
    /// StartCell 은 고른 후보의 시작 칸(본류나 지류 위의 강 칸)이고, ApexCell 은 본류를 따라가기 시작한 칸입니다.
    /// NRocks 는 회랑 넓이의 2% 이상을 차지하는 지표 암석의 종류 수입니다.
    /// </remarks>
    public sealed record Choice(
        (double XMin, double XMax, double YMin, double YMax) Rect, string Axis, int Sign, long StartCell, (double X, double Y) StartXy,
        long ApexCell, string ApexKind, long NEntranceCells, long NStemCells, double LengthM, double WidthM, long NCandidates,
        double Score = 0.0, double RockDiversity = 0.0, double SlopeDiversity = 0.0, double Caves = 0.0, double Stem = 0.0,
        long NRocks = 0, double Lake = 0.0, long NLakeCells = 0)
    {
        public OrderedDictionary<string, object?> AsDict() => new()
        {
            ["rect"] = new List<object?> { Rect.XMin, Rect.XMax, Rect.YMin, Rect.YMax },
            ["axis"] = Axis,
            ["sign"] = (long)Sign,
            ["start_cell"] = StartCell,
            ["start_xy"] = new List<object?> { StartXy.X, StartXy.Y },
            ["apex_cell"] = ApexCell,
            ["apex_kind"] = ApexKind,
            ["n_entrance_cells"] = NEntranceCells,
            ["n_stem_cells"] = NStemCells,
            ["length_m"] = LengthM,
            ["width_m"] = WidthM,
            ["n_candidates"] = NCandidates,
            ["score"] = Score,
            ["rock_diversity"] = RockDiversity,
            ["slope_diversity"] = SlopeDiversity,
            ["caves"] = Caves,
            ["stem"] = Stem,
            ["lake"] = Lake,
            ["n_lake_cells"] = NLakeCells,
            ["n_rocks"] = NRocks,
        };
    }

    private static double Num(object? v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    // 회랑 점수의 기준값들.
    // 경사 등급의 경계 [m/m]: 완만(약 6° 아래), 보통(약 19° 아래), 가파름(약 33° 아래), 매우 가파름.
    private static readonly double[] SlopeClassEdges = [0.1, 0.35, 0.65];
    private const int ScoreClasses = 4;          // 고루 섞인 정도를 잴 때 '충분히 다양함'으로 보는 종류 수
    private const double EnoughEntrances = 3.0;  // 통로에 닿는 동굴 입구가 이만큼이면 동굴 항의 입구 몫이 가득 참
    private const double EnoughPassagePerLength = 1.0;  // 회랑 안의 동굴 통로 길이가 회랑 길이의 이 배수이면 통로 몫이 가득 참
    private const double RockShareMin = 0.02;    // 암석 종류 수를 셀 때 넓이 몫의 아래 한계
    private const double EnoughLakeAreaM2 = 3.0e4;  // 회랑 안 호수 넓이가 이만큼(3 ha, 100 × 300 m)이면 호수 항이 가득 참
    private const double LakeEdgeMarginM = 100.0;   // 호수 항은 회랑 가장자리에서 이만큼 안쪽에 든 호수 칸만 셈 (귀퉁이에 걸친 호수는 세지 않음)
    // 직사각형을 폭 방향으로 옮겨 보는 자리 (회랑 폭의 배수). 0 이면 시작점이 폭의 한가운데, ±0.4 면 가장자리 가까이에 놓입니다.
    private static readonly double[] LateralOffsets = [0.0, -0.25, 0.25, -0.4, 0.4];

    // 몫들이 고루 섞인 정도 (0~1). 섀넌 엔트로피 −Σ p·ln p 를 ln(ScoreClasses) 로 나눕니다.
    // 예: 한 종류뿐이면 0, 네 종류가 4분의 1씩이면 1, 두 종류가 반반이면 0.5.
    internal static double Evenness(long[] counts)
    {
        long total = 0;
        foreach (long v in counts)
        {
            total += v;
        }
        if (total == 0)
        {
            return 0.0;
        }
        double h = 0.0;
        foreach (long v in counts)
        {
            if (v > 0)
            {
                double pShare = (double)v / total;
                h -= pShare * Math.Log(pShare);
            }
        }
        return Math.Min(h / Math.Log(ScoreClasses), 1.0);
    }

    /// <summary>
    /// 회랑 직사각형을 고릅니다 (choose_corridor). 본류(유역에서 가장 큰 강 줄기)와 지류 위에 후보 직사각형을 놓고 점수가 가장 높은 후보를 고릅니다.
    /// </summary>
    /// <remarks>
    /// 점수 = 지표 암석이 고루 섞인 정도 + 경사가 고루 섞인 정도 + 동굴 + 강 (각 0~1, <see cref="Choice"/>).
    /// 네 항목을 더하는 이유: 동굴 입구 수만으로 고르면 회랑이 석회암 언덕 한가운데에 놓여, 6 km 를 걸어도
    /// 암석 한 종류와 같은 모양의 비탈만 보입니다. passageM (N,) 은 칸마다 그 칸을 지나는 동굴 통로의 길이 [m]
    /// (HeroVolume.PassageM)이고, 주지 않으면 동굴 층이 있는 칸마다 칸 간격만큼의 통로가 있다고 봅니다.
    /// openEntrance (N,) 는 지표에서 동굴로 들어갈 수 있는 칸(입구 굴을 판 칸과 싱크홀 칸)이고,
    /// 주지 않으면 히어로 묶음의 입구 후보 칸(cave_entrance)을 모두 입구로 셉니다.
    /// </remarks>
    public static Choice ChooseCorridor(HeroState heroState, Config cfg, double[]? passageM = null, bool[]? openEntrance = null)
    {
        CellGraph graph = heroState.Graph;
        FieldSet f = heroState.Fields;
        if (graph.Kind != "flat")
        {
            throw new ArgumentException("회랑은 평면 히어로에서만 고릅니다");
        }
        double length = cfg.F("profile.corridor.length_m");
        double width = cfg.F("profile.corridor.width_m");
        if (!(length > 0 && width > 0))
        {
            throw new ArgumentException("profile.corridor 의 length_m, width_m 은 0 보다 커야 합니다");
        }
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        (double[] xc, double[] yc) = Centers(graph);
        double loX = graph.Origin.East + (0.5 * dx);
        double hiX = graph.Origin.East + ((nx - 0.5) * dx);
        double loY = graph.Origin.North - ((ny - 0.5) * dx);
        double hiY = graph.Origin.North - (0.5 * dx);
        double[] q = f.GetFloat64("discharge_m3_per_yr");
        long[] rcv = (long[])FieldSet.CastTo(f["receiver"], typeof(long));
        bool[] isRiver = (bool[])FieldSet.CastTo(f["is_river"], typeof(bool));
        bool[] isLake = f.Contains("is_lake") ? (bool[])FieldSet.CastTo(f["is_lake"], typeof(bool)) : new bool[isRiver.Length];
        bool[] ent = Array.ConvertAll((long[])FieldSet.CastTo(f["cave_entrance"], typeof(long)), v => v > 0);
        if (openEntrance is not null)
        {
            if (openEntrance.Length != ent.Length)
            {
                throw new ArgumentException($"openEntrance 는 칸 수 ({ent.Length},) 와 같은 길이여야 합니다. 받은 길이 {openEntrance.Length}");
            }
            for (int c = 0; c < ent.Length; c++)
            {
                ent[c] = openEntrance[c];
            }
        }
        byte[] rockId = (byte[])FieldSet.CastTo(f["surface_rock"], typeof(byte));
        double[] slopeAll = f.GetFloat64("slope");
        // 칸마다의 동굴 통로 길이 [m]. 통로 그물을 받지 않았으면 동굴 층이 있는 칸마다 칸 간격만큼으로 어림합니다.
        int nLevels = (int)cfg.I("caves.levels");
        double[] caveLen = new double[xc.Length];
        if (passageM is not null)
        {
            if (passageM.Length != xc.Length)
            {
                throw new ArgumentException($"passageM 은 칸 수 ({xc.Length},) 와 같은 길이여야 합니다. 받은 길이 {passageM.Length}");
            }
            Array.Copy(passageM, caveLen, caveLen.Length);
        }
        else
        {
            for (int k = 0; k < nLevels; k++)
            {
                string name = $"cave_level_{k}_m";
                if (!f.Contains(name))
                {
                    continue;
                }
                double[] lv = f.GetFloat64(name);
                for (int c = 0; c < caveLen.Length; c++)
                {
                    if (double.IsFinite(lv[c]))
                    {
                        caveLen[c] = dx;
                    }
                }
            }
        }

        var apexes = heroState.FanApexes.OfType<OrderedDictionary<string, object?>>().Where(a => a.ContainsKey("cell")).ToList();
        long start;
        string kind;
        if (apexes.Count > 0)
        {
            OrderedDictionary<string, object?> best0 = apexes[0];
            foreach (OrderedDictionary<string, object?> a in apexes.Skip(1))
            {
                double qa = a.TryGetValue("discharge_m3_per_yr", out object? va) ? Num(va) : 0.0;
                double qb = best0.TryGetValue("discharge_m3_per_yr", out object? vb) ? Num(vb) : 0.0;
                long ca = Convert.ToInt64(a["cell"], System.Globalization.CultureInfo.InvariantCulture);
                long cb = Convert.ToInt64(best0["cell"], System.Globalization.CultureInfo.InvariantCulture);
                if (qa > qb || (qa == qb && -ca > -cb))
                {
                    best0 = a;
                }
            }
            start = Convert.ToInt64(best0["cell"], System.Globalization.CultureInfo.InvariantCulture);
            kind = "fan";
        }
        else if (isRiver.Any(b => b))
        {
            long bestC = -1;
            for (int c = 0; c < isRiver.Length; c++)
            {
                if (isRiver[c] && (bestC < 0 || q[c] > q[bestC]))
                {
                    bestC = c;
                }
            }
            start = bestC;
            kind = "river";
        }
        else
        {
            long bestC = 0;
            for (int c = 1; c < q.Length; c++)
            {
                if (q[c] > q[bestC])
                {
                    bestC = c;
                }
            }
            start = bestC;
            kind = "discharge";
        }
        (long[] path, int p0) = MainStem(rcv, q, start);
        int np = path.Length;
        double[] px = path.Select(c => xc[c]).ToArray();
        double[] py = path.Select(c => yc[c]).ToArray();
        double[] s = new double[np];
        for (int i = 1; i < np; i++)
        {
            double ddx = px[i] - px[i - 1];
            double ddy = py[i] - py[i - 1];
            s[i] = s[i - 1] + Math.Sqrt((ddx * ddx) + (ddy * ddy));
        }
        int[] cand = Enumerable.Range(0, np).Where(i => isRiver[path[i]]).ToArray();
        if (cand.Length == 0)
        {
            cand = Enumerable.Range(0, np).ToArray();
        }
        if (cand.Length > MaxCandidates)
        {
            double[] lin = NpGrid.Linspace(0, cand.Length - 1, MaxCandidates);
            cand = lin.Select(v => cand[(int)Math.Round(v)]).ToArray();
        }
        // 후보마다 시작 칸, 강이 흘러가는 방향 (vx, vy), 선상지 꼭짓점까지의 거리를 적어 둡니다.
        var anchorCell = new List<long>();
        var anchorVx = new List<double>();
        var anchorVy = new List<double>();
        var anchorFar = new List<double>();
        foreach (int a in cand)
        {
            int b = Math.Min(SciPy.SearchSorted(s, s[a] + length, false), np - 1);
            double vx = px[b] - px[a];
            double vy = py[b] - py[a];
            if (vx == 0 && vy == 0)
            {
                double ex = px[^1] - px[a];
                double ey = py[^1] - py[a];
                (vx, vy) = ex != 0 || ey != 0 ? (ex, ey) : (0.0, 1.0);
            }
            anchorCell.Add(path[a]);
            anchorVx.Add(vx);
            anchorVy.Add(vy);
            anchorFar.Add(Math.Abs(s[a] - s[p0]));
        }
        // 본류 밖의 강(지류) 칸도 후보로 넣습니다. 본류가 암석 띠와 나란히 흐르는 유역에서는 본류 위의 후보만으로는
        // 암석 여러 종류와 강을 한 회랑에 담지 못합니다. 암석 띠를 가로질러 본류로 내려오는 지류를 따라가면 둘 다 담깁니다.
        // 지류 칸은 칸 번호 순서로 일정한 간격마다 하나씩 골라 MaxTributaryCandidates 개를 넘지 않게 합니다.
        bool[] isStem = new bool[xc.Length];
        foreach (long c in path)
        {
            isStem[c] = true;
        }
        int nTributary = 0;
        for (int c = 0; c < isRiver.Length; c++)
        {
            nTributary += isRiver[c] && !isStem[c] ? 1 : 0;
        }
        int every = Math.Max((int)Math.Ceiling((double)nTributary / MaxTributaryCandidates), 1);
        int seen = 0;
        for (int c = 0; c < isRiver.Length; c++)
        {
            if (!isRiver[c] || isStem[c] || seen++ % every != 0)
            {
                continue;
            }
            // 강이 흘러가는 방향: 이 칸에서 물길을 회랑 길이만큼 따라 내려간 칸까지의 방향.
            long end = c;
            double run = 0.0;
            while (run < length && rcv[end] != end && rcv[end] >= 0)
            {
                long next = rcv[end];
                run += double.Hypot(xc[next] - xc[end], yc[next] - yc[end]);
                end = next;
            }
            double vx = xc[end] - xc[c];
            double vy = yc[end] - yc[c];
            if (vx == 0 && vy == 0)
            {
                vy = 1.0;
            }
            anchorCell.Add(c);
            anchorVx.Add(vx);
            anchorVy.Add(vy);
            anchorFar.Add(double.Hypot(xc[c] - xc[start], yc[c] - yc[start]));
        }
        int nAnchor = anchorCell.Count;
        // 시작점 후보마다 직사각형을 여러 모양으로 놓아 봅니다: 길게 뻗는 방향 둘(동서, 남북) × 뻗는 쪽 둘 × 옆으로 옮긴 자리 다섯.
        // 본류가 뻗는 방향 하나만 보면, 강을 따라가면서 암석 경계도 지나는 자리를 놓칩니다.
        // 직사각형마다 점수의 재료(입구 수, 줄기 칸 수, 강이나 호수가 있는 조각 수, 호수 칸 수, 암석·경사의 칸 수, 동굴 통로 길이)를 따로 구한 뒤,
        // 후보 순서, 모양 순서대로 점수가 가장 큰 직사각형을 고릅니다. 서로 독립이라 병렬로 구해도 고르는 결과가 같습니다.
        int nVar = 4 * LateralOffsets.Length;
        var evals = new (string Axis, double Sign, (double, double, double, double) Rect, double L, double Wd, long NEnt, long NStem,
            long[] Rocks, long[] Slopes, double Passage, long NCells, long NRiverSlices, long NLake)[nAnchor * nVar];
        Parallelism.ForTasks(nAnchor, ci =>
        {
            double vx = anchorVx[ci];
            double vy = anchorVy[ci];
            // 모양 0번은 본류가 뻗는 방향과 쪽입니다. 점수가 같으면 이 모양이 먼저 뽑힙니다.
            string stemAxis = Math.Abs(vx) >= Math.Abs(vy) ? "east" : "north";
            double stemSign = (stemAxis == "east" ? vx : vy) >= 0 ? 1.0 : -1.0;
            string otherAxis = stemAxis == "east" ? "north" : "east";
            (string Axis, double Sign)[] shapes =
                [(stemAxis, stemSign), (stemAxis, -stemSign), (otherAxis, 1.0), (otherAxis, -1.0)];
            double ax = xc[anchorCell[ci]];
            double ay = yc[anchorCell[ci]];
            for (int v = 0; v < nVar; v++)
            {
                (string axis, double sign) = shapes[v / LateralOffsets.Length];
                double offset = LateralOffsets[v % LateralOffsets.Length];
                double l;
                double wd;
                double xa;
                double xb;
                double ya;
                double yb;
                if (axis == "east")
                {
                    l = NpMath.PyMin(length, hiX - loX);
                    wd = NpMath.PyMin(width, hiY - loY);
                    double e2 = ax + (sign * l);
                    (xa, xb) = ax <= e2 ? (ax, e2) : (e2, ax);
                    ya = ay - (0.5 * wd) + (offset * wd);
                    yb = ay + (0.5 * wd) + (offset * wd);
                }
                else
                {
                    l = NpMath.PyMin(length, hiY - loY);
                    wd = NpMath.PyMin(width, hiX - loX);
                    double e2 = ay + (sign * l);
                    (ya, yb) = ay <= e2 ? (ay, e2) : (e2, ay);
                    xa = ax - (0.5 * wd) + (offset * wd);
                    xb = ax + (0.5 * wd) + (offset * wd);
                }
                double sx = NpMath.PyMax(loX - xa, 0.0) - NpMath.PyMax(xb - hiX, 0.0);
                double sy = NpMath.PyMax(loY - ya, 0.0) - NpMath.PyMax(yb - hiY, 0.0);
                (double, double, double, double) rect = (xa + sx, xb + sx, ya + sy, yb + sy);
                bool Inside(int c) => xc[c] >= rect.Item1 && xc[c] <= rect.Item2 && yc[c] >= rect.Item3 && yc[c] <= rect.Item4;
                // 직사각형이 덮는 칸 번호 범위만 돕니다 (한 칸씩 넉넉히 잡고, 안에 드는지는 칸 중심으로 다시 확인).
                int iLo = Math.Max((int)Math.Floor((rect.Item1 - loX) / dx) - 1, 0);
                int iHi = Math.Min((int)Math.Ceiling((rect.Item2 - loX) / dx) + 1, nx - 1);
                int jLo = Math.Max((int)Math.Floor((hiY - rect.Item4) / dx) - 1, 0);
                int jHi = Math.Min((int)Math.Ceiling((hiY - rect.Item3) / dx) + 1, ny - 1);
                long nEnt = 0;
                double passage = 0.0;
                long nCells = 0;
                long nStem = 0;
                long nLake = 0;
                long[] rocks = new long[Rocks.NRocks];
                long[] slopes = new long[SlopeClassEdges.Length + 1];
                // 회랑을 길이 방향으로 칸 간격마다 자른 조각에 강이나 호수 칸이 있는지 적어 둡니다.
                bool[] sliceHasRiver = new bool[axis == "east" ? iHi - iLo + 1 : jHi - jLo + 1];
                for (int jj = jLo; jj <= jHi; jj++)
                {
                    for (int ii = iLo; ii <= iHi; ii++)
                    {
                        int c = (jj * nx) + ii;
                        if (!Inside(c))
                        {
                            continue;
                        }
                        nCells++;
                        nEnt += ent[c] ? 1 : 0;
                        passage += caveLen[c];
                        if (isRiver[c] || isLake[c])
                        {
                            sliceHasRiver[axis == "east" ? ii - iLo : jj - jLo] = true;
                        }
                        bool inner = xc[c] >= rect.Item1 + LakeEdgeMarginM && xc[c] <= rect.Item2 - LakeEdgeMarginM
                            && yc[c] >= rect.Item3 + LakeEdgeMarginM && yc[c] <= rect.Item4 - LakeEdgeMarginM;
                        nLake += isLake[c] && inner ? 1 : 0;
                        nStem += isStem[c] ? 1 : 0;
                        rocks[Math.Min((int)rockId[c], Rocks.NRocks - 1)]++;
                        int cls = 0;
                        while (cls < SlopeClassEdges.Length && slopeAll[c] >= SlopeClassEdges[cls])
                        {
                            cls++;
                        }
                        slopes[cls]++;
                    }
                }
                long nRiverSlices = sliceHasRiver.Count(b => b);
                evals[(ci * nVar) + v] = (axis, sign, rect, l, wd, nEnt, nStem, rocks, slopes, passage, nCells, nRiverSlices, nLake);
            }
        });
        (double, double, long)? bestKey = null;
        Choice? best = null;
        for (int e = 0; e < evals.Length; e++)
        {
            int a = e / nVar;
            (string axis, double sign, (double, double, double, double) rect, double l, double wd, long nEnt, long nStem,
                long[] rocks, long[] slopes, double passage, long nCells, long nRiverSlices, long nLake) = evals[e];
            double rockDiv = Evenness(rocks);
            double slopeDiv = Evenness(slopes);
            // 동굴 항목: 지표에서 들어갈 수 있는 곳의 수와, 회랑 안 통로 길이를 회랑 길이와 비교한 값을 반씩 봅니다.
            double caves = (0.5 * Math.Min(nEnt / EnoughEntrances, 1.0)) + (0.5 * Math.Min(passage / (EnoughPassagePerLength * l), 1.0));
            // 강 항목: 강이나 호수가 회랑 길이의 얼마를 따라가는지. 강이 회랑을 가로질러 지나가기만 하면 조각 한두 개만 셉니다.
            double stem = Math.Min(nRiverSlices * dx / l, 1.0);
            // 호수 항목: 회랑 가장자리에서 LakeEdgeMarginM 넘게 안쪽에 든 호수 넓이.
            // 호수는 유역에 몇 곳뿐이라, 이 항목이 없으면 회랑에 호수가 들어가는 일이 드뭅니다.
            double lake = Math.Min(nLake * dx * dx / EnoughLakeAreaM2, 1.0);
            double score = rockDiv + slopeDiv + caves + stem + lake;
            // 점수가 같으면 선상지 꼭짓점에 가까운 후보, 그래도 같으면 본류 칸이 많은 직사각형, 그래도 같으면 앞 순서.
            (double, double, long) key = (score, -anchorFar[a], nStem);
            if (bestKey is null || key.CompareTo(bestKey.Value) > 0)
            {
                bestKey = key;
                long nRocks = rocks.Count(v => nCells > 0 && (double)v / nCells >= RockShareMin);
                best = new Choice(rect, axis, (int)sign, anchorCell[a], (xc[anchorCell[a]], yc[anchorCell[a]]), start, kind, nEnt, nStem, l, wd, evals.Length,
                    score, rockDiv, slopeDiv, caves, stem, nRocks, lake, nLake);
            }
        }
        return best!;
    }

    private static (double[] Xs, double[] Ys) Grid((double XMin, double XMax, double YMin, double YMax) rect, double step)
    {
        int ncol = (int)Math.Floor(((rect.XMax - rect.XMin) / step) + 1e-9) + 1;
        int nrow = (int)Math.Floor(((rect.YMax - rect.YMin) / step) + 1e-9) + 1;
        return (Enumerable.Range(0, ncol).Select(i => rect.XMin + (i * step)).ToArray(),
            Enumerable.Range(0, nrow).Select(j => rect.YMax - (j * step)).ToArray());
    }

    private static (double[] Gx, double[] Gy) Mesh2(double[] xs, double[] ys)
    {
        double[] gx = new double[xs.Length * ys.Length];
        double[] gy = new double[gx.Length];
        for (int r = 0; r < ys.Length; r++)
        {
            for (int c = 0; c < xs.Length; c++)
            {
                gx[(r * xs.Length) + c] = xs[c];
                gy[(r * xs.Length) + c] = ys[r];
            }
        }
        return (gx, gy);
    }

    private static OrderedDictionary<string, object?> SiteFrame(HeroState heroState, Config cfg, double cx, double cy)
    {
        HeroSite? site = heroState.Site;
        if (site is null)
        {
            return new OrderedDictionary<string, object?> { ["kind"] = "flat_hero", ["note"] = "행성 없이 만든 평면 히어로라 행성 위 위치가 없습니다" };
        }
        double r = cfg.F("planet.radius_m");
        double[] p = Domain.LocalToUnit(site, [cx], [cy], r);
        double[] axis0 = cfg.FArray("planet.axis");
        double na = LinAlg.Norm3(axis0);
        double[] axis = [axis0[0] / na, axis0[1] / na, axis0[2] / na];
        double lat = Math.Asin(NpMath.Clip(LinAlg.Dot3Np(p, axis), -1.0, 1.0)) * (180.0 / Math.PI);
        double[] rf = new double[3];
        rf[Domain.ArgMinAbs(axis)] = 1.0;
        double ra = LinAlg.Dot3Np(rf, axis);
        rf = [rf[0] - (ra * axis[0]), rf[1] - (ra * axis[1]), rf[2] - (ra * axis[2])];
        double nr = LinAlg.Norm3(rf);
        rf = [rf[0] / nr, rf[1] / nr, rf[2] / nr];
        double[] cr = new double[3];
        LinAlg.Cross3(rf, p, cr);
        double lon = Math.Atan2(LinAlg.Dot3Np(cr, axis), LinAlg.Dot3Np(p, rf)) * (180.0 / Math.PI);
        static List<object?> L(double[] v) => v.Select(x => (object?)x).ToList();
        return new OrderedDictionary<string, object?>
        {
            ["kind"] = "planet",
            ["origin_unit"] = L(p),
            ["east"] = L(site.East),
            ["north"] = L(site.North),
            ["lat_deg"] = lat,
            ["lon_deg"] = lon,
            ["hero_center_unit"] = L(site.CenterUnit),
            ["hero_lat_deg"] = site.LatDeg,
            ["hero_lon_deg"] = site.LonDeg,
            ["radius_m"] = r,
        };
    }

    /// <summary>재질 부피 strata.u8/.json 과 윗면 strata_top.bin/.json 을 씁니다 (bake_strata).</summary>
    // 동굴 메시를 만들어 caves.glb 로 씁니다. 메시 배열(수백 MB)이 이 메서드 안에서 끝나, 뒤의 재질 부피 굽기 동안 남지 않습니다.
    private static (OrderedDictionary<string, object?> Diag, string? Note) BakeCaves(
        HeroVolume vol, (double XMin, double XMax, double YMin, double YMax) rect, double voxel, EngineFrame frame, string outDir,
        OrderedDictionary<string, object?> files)
    {
        (double[] verts, int[] faces, bool[] anchor, OrderedDictionary<string, object?> cdiag) = Mesh.CaveSurface(vol, rect, voxel);
        string? cavesNote = null;
        // 입구와도 회랑 가장자리와도 이어지지 않는 조각은 BuildMesh 가 지웁니다. 남은 삼각형 수를 기록에 씁니다.
        CaveMesh? mesh = faces.Length > 0 ? Mesh.BuildMesh(verts, faces, vol, frame, voxel, anchor) : null;
        if (mesh is not null)
        {
            cdiag["faces_unreachable"] = (long)(faces.Length / 3) - mesh.FaceCount;
            cdiag["faces_kept"] = (long)mesh.FaceCount;
        }
        if (mesh is not null && mesh.FaceCount > 0)
        {
            long size = Mesh.ExportGlb(mesh, Path.Combine(outDir, "caves.glb"));
            files["caves"] = new OrderedDictionary<string, object?>
            {
                ["file"] = "caves.glb",
                ["bytes"] = size,
                ["vertices"] = (long)mesh.VertexCount,
                ["faces"] = (long)mesh.FaceCount,
                ["color_0"] = "rgb = rock color, alpha = material id (0..11) / 255",
                ["normals"] = "point into the cave void (front faces seen from inside)",
            };
        }
        else
        {
            cavesNote = "회랑 안에 동굴이 없어 caves.glb 를 쓰지 않았습니다";
            string stale = Path.Combine(outDir, "caves.glb");
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }
        return (cdiag, cavesNote);
    }

    public static OrderedDictionary<string, object?> BakeStrata(
        HeroVolume volume, (double XMin, double XMax, double YMin, double YMax) rect, EngineFrame frame, Config cfg, string outDir)
    {
        (double[] xs, double[] ys) = Grid(rect, StrataDxM);
        (double[] cx, double[] cy) = Mesh2(xs, ys);
        double[] top = volume.SurfaceHeight(cx, cy);
        double depth = cfg.F("groundwater.max_depth_m") + (2.0 * cfg.F("caves.passage_radius_m"));
        int nz = (int)Math.Ceiling(depth / StrataDzM);
        double[] offs = Enumerable.Range(0, nz).Select(k => (k + 0.5) * StrataDzM).ToArray();
        byte[] mat = new byte[cx.Length * nz];
        int nL = volume.NLayers;
        long bedSeed = (long)(Hashing.Hash3(cfg.I("planet.seed"), StreamBeds, 0) >> 1);
        int step = Math.Max(StrataChunkPoints / nz, 1);
        for (int s = 0; s < cx.Length; s += step)
        {
            int e = Math.Min(s + step, cx.Length);
            // 아래 반복은 모두 칸(또는 점)마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
            double[] up = new double[(e - s) * nz];
            int s0 = s;
            Parallelism.For(s, e, c =>
            {
                for (int k = 0; k < nz; k++)
                {
                    up[((c - s0) * nz) + k] = top[c] - offs[k];
                }
            });
            double[] sx = cx[s..e];
            double[] sy = cy[s..e];
            // 재질 부피의 칸(가로 StrataDxM, 세로 StrataDzM)은 가는 통로보다 크므로, 통로가 토막 나지 않게 칠합니다.
            Dictionary<string, Array> ev = volume.EvaluateGridCoarse(sx, sy, up, nz, StrataDxM, StrataDzM, "material", "water");
            byte[] m = (byte[])ev["material"];
            bool[] w = (bool[])ev["water"];
            // 층리: 기반암 칸이 제 지층의 바닥에서 얼마나 높은지로 띠 번호를 정합니다 (맨 아래 기반암은 윗면에서 잰 깊이).
            double[] sb = volume.Columns(sx, sy).StrataBottom;
            Parallelism.For(0, m.Length, q =>
            {
                byte id = w[q] ? StrataWater : m[q];
                int k = q % nz;
                if (HasBeds(id) && k >= BedMinLayer && nL > 0)
                {
                    int c = q / nz;
                    double zq = up[q];
                    int li = nL;
                    for (int i = 0; i < nL; i++)
                    {
                        if (sb[(c * nL) + i] < zq)
                        {
                            li = i;
                            break;
                        }
                    }
                    double h = li < nL ? zq - sb[(c * nL) + li] : sb[(c * nL) + nL - 1] - zq;
                    long band = (long)Math.Floor(h / BedThicknessM);
                    if (Hashing.HashUnit(bedSeed, li, band) < BedDarkFraction)
                    {
                        id += BedIdOffset;
                    }
                }
                mat[(s0 * nz) + q] = id;
            });
        }
        int nrow = ys.Length;
        int ncol = xs.Length;
        byte[] vol = new byte[mat.Length];
        Parallelism.For(0, nrow, r =>
        {
            for (int c = 0; c < ncol; c++)
            {
                for (int k = 0; k < nz; k++)
                {
                    vol[(((r * nz) + k) * ncol) + c] = mat[(((r * ncol) + c) * nz) + k];
                }
            }
        });
        double[] originTop = [xs[0] - frame.Cx, -frame.YOffset, -(ys[0] - frame.Cy)];
        OrderedDictionary<string, object?> topMeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "strata_top"), top, nrow, ncol, StrataDxM, originTop);
        string tmp = Path.Combine(outDir, "strata.u8.part");
        File.WriteAllBytes(tmp, vol);
        File.Move(tmp, Path.Combine(outDir, "strata.u8"), true);
        // 덩어리마다 센 뒤 더합니다 (정수라 더하는 순서와 상관없이 같음).
        const int countChunk = 1 << 20;
        long[][] partCounts = new long[(vol.Length + countChunk - 1) / countChunk][];
        Parallelism.ForChunks(0, vol.Length, countChunk, (lo, hi) =>
        {
            long[] part = new long[256];
            for (int q = lo; q < hi; q++)
            {
                part[vol[q]]++;
            }
            partCounts[lo / countChunk] = part;
        });
        long[] counts = new long[256];
        foreach (long[] part in partCounts)
        {
            for (int i = 0; i < 256; i++)
            {
                counts[i] += part[i];
            }
        }
        var legend = new List<object?>();
        for (int r = 0; r < Rocks.NRocks; r++)
        {
            legend.Add(new OrderedDictionary<string, object?>
            {
                ["id"] = (long)r,
                ["name"] = Rocks.RockNames[r],
                ["rgb"] = new List<object?> { (long)Rocks.ColorRgb[r * 3], (long)Rocks.ColorRgb[(r * 3) + 1], (long)Rocks.ColorRgb[(r * 3) + 2] },
            });
        }
        // 층리의 어두운 띠 (기반암 1..10 만): 같은 암석의 어두운 색.
        for (int r = Rocks.Sandstone; r <= Rocks.Quartzite; r++)
        {
            if (!HasBeds((byte)r))
            {
                continue;
            }
            legend.Add(new OrderedDictionary<string, object?>
            {
                ["id"] = (long)(r + BedIdOffset),
                ["name"] = Rocks.RockNames[r] + "_dark_bed",
                ["rgb"] = new List<object?>
                {
                    (long)Math.Round(Rocks.ColorRgb[r * 3] * BedDarkShade), (long)Math.Round(Rocks.ColorRgb[(r * 3) + 1] * BedDarkShade),
                    (long)Math.Round(Rocks.ColorRgb[(r * 3) + 2] * BedDarkShade),
                },
            });
        }
        legend.Add(new OrderedDictionary<string, object?> { ["id"] = (long)StrataWater, ["name"] = "water", ["rgb"] = new List<object?> { 40L, 110L, 200L } });
        legend.Add(new OrderedDictionary<string, object?> { ["id"] = (long)StrataAir, ["name"] = "air", ["rgb"] = new List<object?> { 0L, 0L, 0L } });
        var countDict = new OrderedDictionary<string, object?>();
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] != 0)
            {
                countDict[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = counts[i];
            }
        }
        var meta = new OrderedDictionary<string, object?>
        {
            ["format"] = "uint8",
            ["layout"] = "row_layer_col",
            ["axes"] = "x_east_y_up_z_south",
            ["shape"] = new List<object?> { (long)nrow, (long)nz, (long)ncol },
            ["shape_names"] = new List<object?> { "rows (+Z, north to south)", "layers (down from top)", "cols (+X)" },
            ["spacing_m"] = new OrderedDictionary<string, object?> { ["x"] = StrataDxM, ["y"] = StrataDzM, ["z"] = StrataDxM },
            ["origin_xz"] = new List<object?> { originTop[0], originTop[2] },
            ["vertical"] = "surface_following",
            ["top_stem"] = "strata_top",
            ["layer_center_rule"] = "Y = (strata_top value + origin.y) - (layer + 0.5) * spacing_m.y",
            ["depth_m"] = nz * StrataDzM,
            ["legend"] = legend,
            ["counts"] = countDict,
            ["bpcg_version"] = Package.Version,
        };
        Bundle.WriteJson(Path.Combine(outDir, "strata.json"), meta);
        return new OrderedDictionary<string, object?> { ["strata"] = meta, ["strata_top"] = topMeta };
    }

    private static double Round3(double x) => Math.Round(x * 1000.0, MidpointRounding.ToEven) / 1000.0;

    /// <summary>회랑을 골라 엔진 파일을 굽습니다 (bake_corridor). 반환: manifest.</summary>
    public static OrderedDictionary<string, object?> BakeCorridor(HeroState heroState, Config cfg, string outDir, string? engineDir = null, Action<string>? log = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(outDir);
        var sec = new OrderedDictionary<string, object?>();
        double voxel = cfg.F("profile.corridor.voxel_m");
        if (!(double.IsFinite(voxel) && voxel > 0))
        {
            throw new ArgumentException($"profile.corridor.voxel_m 은 0 보다 커야 합니다: {voxel}");
        }

        long t = Stopwatch.GetTimestamp();
        var vol = new HeroVolume(heroState, cfg);
        sec["volume"] = Seconds(t);
        t = Stopwatch.GetTimestamp();
        (double[] hx, double[] hy) = vol.CellCenters();
        // 지표에서 동굴로 들어갈 수 있는 칸: 입구 굴을 판 칸과 싱크홀 칸.
        bool[] openings = new bool[vol.EntranceOpen.Length];
        for (int c = 0; c < openings.Length; c++)
        {
            openings[c] = vol.EntranceOpen[c] || vol.SinkOpen[c];
        }
        Choice cor = ChooseCorridor(heroState, cfg, vol.PassageM, openings);
        (double XMin, double XMax, double YMin, double YMax) rect = cor.Rect;
        sec["choose"] = Seconds(t);
        string apex = cor.ApexKind switch { "fan" => "선상지 꼭짓점", "river" => "출구 쪽 큰 강", _ => cor.ApexKind };
        double lakeHa = cor.NLakeCells * heroState.Graph.Spacing * heroState.Graph.Spacing / 1.0e4;
        log?.Invoke($"[굽기] 회랑: {LogText.Axis(cor.Axis)} 방향으로 긴 {cor.LengthM:F0} × {cor.WidthM:F0} m, 시작 칸 {cor.StartCell} (본류 기준점: {apex}), "
            + $"동굴 입구와 싱크홀 {LogText.N(cor.NEntranceCells)}곳, 지표 암석 {cor.NRocks}종, 호수 {lakeHa:F1} ha, 점수 {cor.Score:F2}");

        // --- 높이맵 (회랑 지표)
        t = Stopwatch.GetTimestamp();
        (double[] xs, double[] ys) = Grid(rect, voxel);
        int nrow = ys.Length;
        int ncol = xs.Length;
        (double[] gx, double[] gy) = Mesh2(xs, ys);
        double[] surf = vol.SurfaceHeight(gx, gy);
        double cx = 0.5 * (rect.XMin + rect.XMax);
        double cy = 0.5 * (rect.YMin + rect.YMax);
        var frame = new EngineFrame(cx, cy, Math.Floor(surf.Min()));
        double[] origin = [xs[0] - cx, -frame.YOffset, -(ys[0] - cy)];
        var files = new OrderedDictionary<string, object?>();
        files["heightmap"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "heightmap"), surf, nrow, ncol, voxel, origin);
        sec["heightmap"] = Seconds(t);
        log?.Invoke($"[굽기] 높이맵 {ncol} × {nrow} 표본 (간격 {PyFormat.General(voxel)} m), {(double)sec["heightmap"]!:F2} s");

        // --- 수면·지하수면
        t = Stopwatch.GetTimestamp();
        double[] hw = vol.WaterSurface(gx, gy);
        bool[] wet = new bool[hw.Length];
        double[] waterMap = new double[hw.Length];
        for (int q = 0; q < hw.Length; q++)
        {
            wet[q] = double.IsFinite(hw[q]) && hw[q] >= surf[q] - WaterSurfaceTolM;
            waterMap[q] = wet[q] ? hw[q] : WaterNoneM;
        }
        OrderedDictionary<string, object?> wmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "water"), waterMap, nrow, ncol, voxel, origin);
        wmeta["n_wet"] = (long)wet.Count(b => b);
        files["water"] = wmeta;
        double[] zgw = vol.WaterTable(gx, gy);
        files["water_table"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "water_table"), zgw, nrow, ncol, voxel, origin);
        // 아래 지하수면: 막는 층 아래 녹는 암석의 물 높이. 단면은 이 아래를 젖은 땅으로 칠하고, 동굴 안 물도 이 높이입니다.
        double[] zgwLower = vol.WaterTableLower(gx, gy);
        files["water_table_lower"] = Heightmap.WriteHeightmap(
            Path.Combine(outDir, "water_table_lower"), zgwLower, nrow, ncol, voxel, origin);
        sec["water"] = Seconds(t);

        // --- 동굴 입구 구멍
        t = Stopwatch.GetTimestamp();
        double[] dMouth = (double[])vol.EvaluateGrid(gx, gy, surf, 1, double.PositiveInfinity, "d_cave")["d_cave"];
        double[] mouth = Array.ConvertAll(dMouth, v => NpMath.Clip(v, -CaveMouthClipM, CaveMouthClipM));
        OrderedDictionary<string, object?> mmeta = Heightmap.WriteHeightmap(Path.Combine(outDir, "cave_mouth"), mouth, nrow, ncol, voxel, origin);
        mmeta["n_open"] = (long)mouth.Count(v => v < 0.0);
        files["cave_mouth"] = mmeta;
        sec["cave_mouth"] = Seconds(t);

        // --- 프랙탈 디테일 (보기용)
        OrderedDictionary<string, double>? det = Detail.DetailSettings(cfg);
        if (det is not null)
        {
            (double kLo, double kHi) = Detail.BandLimits(voxel, det["min_wavelength_m"], det["max_wavelength_m"]);
            if (kHi <= kLo || Math.Min(nrow, ncol) < DetailMinSamples)
            {
                log?.Invoke($"[굽기] 프랙탈 디테일: 간격 {PyFormat.General(voxel)} m 회랑에는 더할 파장이 없습니다");
                det = null;
            }
        }
        double[]? surfD = null;
        OrderedDictionary<string, object?>? dmeta = null;
        if (det is not null)
        {
            t = Stopwatch.GetTimestamp();
            try
            {
                (surfD, dmeta) = Detail.AddFractalDetail(surf, gx, gy, nrow, ncol, voxel, wet, vol, cfg, mouth);
            }
            catch (ArgumentException e)
            {
                log?.Invoke($"[굽기] 프랙탈 디테일을 건너뜁니다: {e.Message}");
                det = null;
            }
        }
        if (det is not null && surfD is not null && dmeta is not null)
        {
            OrderedDictionary<string, object?> hd = Heightmap.WriteHeightmap(Path.Combine(outDir, "heightmap_detail"), surfD, nrow, ncol, voxel, origin);
            hd["detail"] = dmeta;
            files["heightmap_detail"] = hd;
            double[] dcd = (double[])vol.EvaluateGrid(gx, gy, surfD, 1, double.PositiveInfinity, "d_cave")["d_cave"];
            double[] mouthD = Array.ConvertAll(dcd, v => NpMath.Clip(v, -CaveMouthClipM, CaveMouthClipM));
            OrderedDictionary<string, object?> md = Heightmap.WriteHeightmap(Path.Combine(outDir, "cave_mouth_detail"), mouthD, nrow, ncol, voxel, origin);
            md["n_open"] = (long)mouthD.Count(v => v < 0.0);
            files["cave_mouth_detail"] = md;
            sec["detail"] = Seconds(t);
            var bw = (List<object?>)dmeta["beta_wavelength_m"]!;
            log?.Invoke($"[굽기] 프랙탈 디테일: 더한 거칠기 평균 크기(RMS) {(double)dmeta["rms_m"]!:F2} m (가장 큰 곳 {(double)dmeta["max_abs_m"]!:F1} m), "
                + $"스펙트럼 기울기 β {(double)dmeta["beta_before"]!:F2} → {(double)dmeta["beta_after"]!:F2} "
                + $"(파장 {PyFormat.General((double)bw[0]!)}–{PyFormat.General((double)bw[1]!)} m), 새로 생긴 웅덩이를 메운 칸 {LogText.N(Convert.ToInt64(dmeta["n_filled"], CultureInfo.InvariantCulture))}개, {(double)sec["detail"]!:F2} s");
        }
        else
        {
            RemoveStems(outDir, DetailStems);
        }

        // --- 히어로 전체 25 m 지표
        CellGraph g = heroState.Graph;
        int hny = (int)g.Shape[0];
        int hnx = (int)g.Shape[1];
        double dxh = g.Spacing;
        double[] oS = [g.Origin.East + (0.5 * dxh) - cx, -frame.YOffset, -((g.Origin.North - (0.5 * dxh)) - cy)];
        // 회랑 지표(vol)와 같은 칸 중심 높이를 씁니다. 격자점 높이(z_m)를 그대로 쓰면 둘레 지형에만 칸 크기의 바둑판 무늬가 남습니다.
        files["surround25"] = Heightmap.WriteHeightmap(Path.Combine(outDir, "surround25"), vol.Z, hny, hnx, dxh, oS);

        // --- 동굴 메시
        t = Stopwatch.GetTimestamp();
        (OrderedDictionary<string, object?> cdiag, string? cavesNote) = BakeCaves(vol, rect, voxel, frame, outDir, files);
        sec["caves"] = Seconds(t);
        log?.Invoke($"[굽기] 동굴 메시: 삼각형 {LogText.N(Convert.ToInt64(cdiag.GetValueOrDefault("faces_kept") ?? 0L, CultureInfo.InvariantCulture))}개 (동굴이 든 조각 {cdiag["tiles_with_caves"]}/{cdiag["tiles"]}), {(double)sec["caves"]!:F2} s");

        // --- 재질 부피
        t = Stopwatch.GetTimestamp();
        OrderedDictionary<string, object?> st = BakeStrata(vol, rect, frame, cfg, outDir);
        foreach (KeyValuePair<string, object?> kv in st)
        {
            files[kv.Key] = kv.Value;
        }
        sec["strata"] = Seconds(t);
        var shp = (List<object?>)((OrderedDictionary<string, object?>)st["strata"]!)["shape"]!;
        log?.Invoke($"[굽기] 재질 부피 {shp[2]} × {shp[0]} × {shp[1]} 상자 (열 × 행 × 층), {(double)sec["strata"]!:F2} s");

        // --- 입구 위치 (엔진 좌표)
        var entIn = new List<object?>();
        var entOut = new List<object?>();
        foreach (int e in vol.EntranceCapsules)
        {
            double mx = 0.5 * (vol.CapA[e * 3] + vol.CapB[e * 3]);
            double my = 0.5 * (vol.CapA[(e * 3) + 1] + vol.CapB[(e * 3) + 1]);
            if (mx >= rect.XMin && mx <= rect.XMax && my >= rect.YMin && my <= rect.YMax)
            {
                double[] ai = frame.ToEngine(vol.CapA[(e * 3)..((e * 3) + 3)]);
                double[] bo = frame.ToEngine(vol.CapB[(e * 3)..((e * 3) + 3)]);
                entIn.Add(ai.Select(v => (object?)Round3(v)).ToList());
                entOut.Add(bo.Select(v => (object?)Round3(v)).ToList());
            }
        }
        Bundle.WriteJson(Path.Combine(outDir, "entrances.json"), new OrderedDictionary<string, object?>
        {
            ["format"] = "bpcg-entrances",
            ["axes"] = "x_east_y_up_z_south",
            ["count"] = (long)entIn.Count,
            ["inside"] = entIn,
            ["outside"] = entOut,
            ["rule"] = "inside = 통로가 시작하는 샘 칸, outside = 산비탈 밖 공중 끝 (같은 동굴 층 높이)",
        }, maxArray: null);

        var fileNames = new List<string>
        {
            "heightmap.bin", "heightmap.json", "surround25.bin", "surround25.json",
            "water.bin", "water.json", "water_table.bin", "water_table.json", "water_table_lower.bin", "water_table_lower.json",
            "strata.u8", "strata.json", "strata_top.bin", "strata_top.json", "entrances.json",
            "cave_mouth.bin", "cave_mouth.json",
        };
        if (files.ContainsKey("caves"))
        {
            fileNames.Add("caves.glb");
        }
        foreach (string stem in DetailStems)
        {
            if (files.ContainsKey(stem))
            {
                fileNames.Add($"{stem}.bin");
                fileNames.Add($"{stem}.json");
            }
        }
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> corDict = cor.AsDict();
        corDict["rect_local_m"] = new OrderedDictionary<string, object?>
        {
            ["x_min"] = rect.XMin,
            ["x_max"] = rect.XMax,
            ["y_min"] = rect.YMin,
            ["y_max"] = rect.YMax,
        };
        corDict["rect_engine_m"] = new OrderedDictionary<string, object?>
        {
            ["x_min"] = rect.XMin - cx,
            ["x_max"] = rect.XMax - cx,
            ["z_min"] = -(rect.YMax - cy),
            ["z_max"] = -(rect.YMin - cy),
        };
        corDict["voxel_m"] = voxel;
        var caves = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, object?> kv in cdiag)
        {
            if (kv.Key != "seconds")
            {
                caves[kv.Key] = kv.Value;
            }
        }
        caves["note"] = cavesNote;
        caves["n_capsules"] = (long)vol.NCapsules;
        caves["n_entrance_candidates"] = vol.NEntranceCandidates;
        caves["n_unopened_entrances"] = vol.NUnopened;
        caves["n_entrances_without_passage"] = vol.NNoPassage;
        caves["n_entrances_dug"] = (long)vol.EntranceCapsules.Length;
        caves["n_entrances"] = (long)entIn.Count;
        caves["n_sinkholes"] = (long)vol.Network.Sinks.Length;
        caves["n_sinkholes_in_corridor"] = (long)vol.Network.Sinks.Count(
            c => hx[c] >= rect.XMin && hx[c] <= rect.XMax && hy[c] >= rect.YMin && hy[c] <= rect.YMax);
        caves["passage_km"] = vol.PassageM.Sum() / 1000.0;
        caves["entrances_file"] = "entrances.json";
        var manifest = new OrderedDictionary<string, object?>
        {
            ["format"] = "bpcg-corridor",
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = Bundle.GitCommit(),
            ["config_digest"] = cfg.Digest(),
            ["seed"] = cfg.I("planet.seed"),
            ["profile"] = cfg.Sec("profile").Get("name"),
            ["frame"] = frame.AsDict(),
            ["site"] = SiteFrame(heroState, cfg, cx, cy),
            ["corridor"] = corDict,
            ["hero"] = new OrderedDictionary<string, object?>
            {
                ["shape"] = new List<object?> { (long)hny, (long)hnx },
                ["spacing_m"] = dxh,
                ["origin_local_m"] = new List<object?> { g.Origin.East, g.Origin.North },
            },
            ["files"] = fileNames.Cast<object?>().ToList(),
            ["file_meta"] = files,
            ["caves"] = caves,
            ["seconds"] = sec,
        };
        Bundle.WriteJson(Path.Combine(outDir, "manifest.json"), manifest);
        fileNames.Add("manifest.json");
        if (engineDir is not null)
        {
            Directory.CreateDirectory(engineDir);
            foreach (string name in fileNames)
            {
                File.Copy(Path.Combine(outDir, name), Path.Combine(engineDir, name), true);
            }
            if (!files.ContainsKey("caves") && File.Exists(Path.Combine(engineDir, "caves.glb")))
            {
                File.Delete(Path.Combine(engineDir, "caves.glb"));
            }
            RemoveStems(engineDir, DetailStems.Where(sx => !files.ContainsKey(sx)));
            log?.Invoke($"[굽기] 엔진 폴더에도 복사했습니다: {engineDir}");
        }
        log?.Invoke($"[굽기] 끝: {(double)sec["total"]!:F2} s → {outDir}");
        return manifest;
    }
}
