using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Geology;

namespace Bpcg.Subsurface;

/// <summary>KarstStreams.Apply 의 결과.</summary>
/// <param name="Exchange">(N,) 지표의 물이 땅속과 주고받는 양 [m³/yr]. 음수는 개울이 그 칸에서 땅속으로 사라지는 양, 양수는 샘으로 솟는 양.</param>
/// <param name="Discharge">(N,) 주고받은 뒤 지표를 흐르는 물 [m³/yr].</param>
/// <param name="NSinks">개울이 사라지는 칸 수.</param>
/// <param name="NSprings">그 물이 다시 솟는 칸 수.</param>
/// <param name="LostM3PerYr">사라지는 물의 합 [m³/yr].</param>
/// <param name="RisenM3PerYr">다시 솟는 물의 합 [m³/yr]. 사라진 물은 모두 한 샘에서 솟으므로 LostM3PerYr 와 같습니다. 더하는 차례가 달라 끝자리만 다를 수 있습니다.</param>
/// <param name="Big">(N,) 큰 물: 바다, 호수, 통로가 다 삼키지 못하는 강. 석회암 지하수면의 기준입니다.</param>
/// <param name="NLoopsCut">사라진 물이 샘에서 솟아 다시 같은 칸으로 흘러드는 고리가 생겨, 개울을 사라지지 않게 한 칸 수. 샘은 사라지는 칸보다 10 m 넘게 낮아 보통 0 입니다.</param>
public sealed record KarstStreamResult(
    double[] Exchange, double[] Discharge, int NSinks, int NSprings, double LostM3PerYr, double RisenM3PerYr, bool[] Big,
    int NLoopsCut = 0);

/// <summary>
/// 석회암 땅에서 땅속으로 사라지는 개울 (docs/pipeline.md 8.5). 석회암 지대의 작은 개울은 어느 자리에서 바닥의 구멍으로
/// 사라지고, 그 아래 골짜기는 물 없이 남습니다(마른 골짜기). 사라진 물은 땅속 통로를 지나 큰 강가의 샘으로 다시 나옵니다.
/// </summary>
/// <remarks>
/// 왜 생기나: 석회암 안의 물은 통로를 타고 큰 강 높이까지 빠져, 지하수면이 큰 강에서 거의 평평하게 이어집니다
/// (기울기 karst_gradient, 지구 0.001~0.005). 작은 개울의 바닥은 그보다 훨씬 가파르게 올라가므로, 큰 강에서 조금만 멀어져도
/// 개울 바닥이 지하수면보다 높이 떠 있습니다. 떠 있는 개울은 바닥으로 물을 잃습니다.
/// 얼마나 큰 개울까지 사라지나: 통로가 큰비 때의 물을 다 흘릴 만큼 굵어질 수 있으면 개울이 통째로 사라집니다. 통로는 물에 잠겨
/// 있는 동안만 굵어지므로(CaveNetwork.TimeLimitedRadius), 강이 빨리 파 내려가는 산에서는 통로가 가늘어 큰 개울은 지표에 남고,
/// 천천히 파 내려가는 땅에서는 꽤 큰 개울도 땅속으로 들어갑니다. 석회암 유역에서 지표 물의 40~90% 가 땅속으로 빠진다는 관측
/// (크레타, Ott 외 2019)과 같은 방향입니다.
/// 규칙:
/// 1. 큰 물: 바다, 호수, 그리고 유량이 그 자리의 통로가 흘릴 수 있는 양보다 많은 강. 석회암 지하수면 = 가장 가까운 큰 물의
///    수면 + karst_gradient × 거리 (지하수면 단계 Groundwater.WaterTable 도 같은 큰 물을 기준으로 씁니다).
/// 2. 떠 있는 칸: 큰 물이 아닌 강 칸 가운데 지표 암석이 물에 녹는 암석이고, 바닥이 그 지하수면보다 PerchMinM 넘게 높고
///    (지표 아래 max_depth_m 안), 그 사이에 물을 막는 층이 없고, noSink 로 막지 않은 칸.
/// 3. 위쪽 칸부터 물을 모으다가, 떠 있는 칸에 강만큼의 물(rivers.min_discharge_m3_per_s 이상)이 닿고 그 양이 통로가 흘릴 수 있는
///    양 이하면 모두 땅속으로 보냅니다. 그 물은 그 칸의 지하수면을 정한 큰 물 칸에서 다시 솟아 그 아래로 흐릅니다.
/// 4. 칸을 고르는 차례: 지표 물길과 '떠 있는 칸 → 그 샘' 땅속 길을 함께 화살표로 보고, 들어오는 화살표가 모두 끝난 칸부터
///    고릅니다(위상 정렬). 그래서 칸마다 물을 보낼지 정할 때 그 칸에 올 물(위쪽 개울, 위쪽 샘에서 솟은 물)이 다 와 있습니다.
///    한 번 모으면 끝나고, 사라진 물과 솟은 물의 합이 늘 같습니다.
/// 5. 사라진 물은 아래층 동굴을 타고 샘까지 가야 합니다. 굽기는 그런 길이 있는 자리에만 싱크홀과 통로를 둡니다
///    (CaveNetwork.Build). 히어로 단계는 동굴 층을 만든 뒤 같은 판단(DrainsToSpring)으로 샘까지 갈 수 없는 칸을 모두 noSink 로
///    막고 다시 계산합니다. 샘으로 빠질 길이 없는 석회암에서는 물이 차올라 개울이 사라지지 않습니다. 길은 지표에서 300 m 보다
///    깊어 굽지 않는 석회암도 지날 수 있고(Caves.RouteLevel0), 샘 쪽에 턱이 있으면 물이 넘침 높이까지 차오른다고 봅니다
///    (CaveNetwork.SpillHead). 개울 바닥이 그 높이보다 PerchMinM 넘게 높을 때만 개울이 사라집니다.
/// 지형은 바꾸지 않습니다. 솔버가 판 골짜기는 그대로 남고 물만 사라집니다.
/// 한계: 지하수면이 지표 아래 max_depth_m 보다 깊은 곳의 개울은 그대로 흐릅니다 (그 깊이의 동굴은 굽지 않아 이을 통로가 없음).
/// </remarks>
public static class KarstStreams
{
    /// <summary>개울 바닥이 석회암 지하수면보다 이만큼 넘게 높아야 물을 잃습니다 [m] (싱크홀을 두는 가장 얕은 깊이와 같은 값).</summary>
    public const double PerchMinM = CaveNetwork.SinkMinDepthM;

    /// <summary>
    /// 히어로 단계가 '사라지는 개울 → 흙 → 지하수면 → 동굴 층'을 계산하는 횟수의 상한. 동굴 층을 만든 뒤 샘까지 갈 수 없는 자리에서
    /// 개울이 사라졌으면, 샘까지 갈 수 없는 칸을 모두 막고 다시 계산합니다(DrainsToSpring). 다시 만든 동굴 층이 조금 달라 새로 막을
    /// 자리가 생기면 한 번 더 돕니다.
    /// </summary>
    public const int MaxCaveRounds = 4;

    /// <summary>
    /// 그 자리의 통로가 흘릴 수 있는 물의 한 해 평균 [m³/yr]: 반지름이 min(잠겨 있는 동안 벽이 물러난 거리, 그리는 한계)인 관이
    /// 큰비 때 가득 차 흘리는 양 ÷ FloodRatio.
    /// </summary>
    public static double Capacity(double upliftMPerYr, CaveNetworkParams p)
    {
        double r = Math.Min(CaveNetwork.TimeLimitedRadius(upliftMPerYr, p.WallRetreatMPerYr, p.ActiveZoneM), p.RadiusMaxM);
        return CaveNetwork.FullPipeDischarge(r, p.HydraulicGradient, p.GravityMS2) / CaveNetwork.FloodRatio * Constants.SecondsPerYear;
    }

    /// <summary>
    /// 사라지는 개울을 정하고 지표의 유량을 다시 모읍니다.
    /// z: (N,) 지표 [m]. receiver·order: 지표 물길 (order 는 하류가 먼저). source: (N,) 칸마다 생기는 물 [m³/yr] (유출 × 넓이 + 바깥에서
    /// 들어오는 물). discharge: (N,) 지금의 유량 [m³/yr] (source 를 모은 값). isRiver·isLake·isOcean·waterLevel: 지금의 물.
    /// surfaceRock: (N,) 지표 암석. uplift: (N,) 솟는 속도 [m/yr].
    /// noSink: (N,) 개울을 땅속으로 보내지 않을 칸. 아래층 동굴로 샘까지 갈 수 없는 칸(DrainsToSpring 이 거짓)을 히어로 단계가 넘깁니다. 없으면 null.
    /// 설정에 통로 굵기 키가 없거나, 평면 그래프가 아니거나, 사라지는 칸이 없으면 null.
    /// </summary>
    public static KarstStreamResult? Apply(
        CellGraph graph, double[] z, long[] receiver, long[] order, double[] source, double[] discharge, bool[] isRiver,
        bool[] isLake, bool[] isOcean, double[] waterLevel, byte[] surfaceRock, double[] uplift, LayerColumns columns, Config cfg,
        bool[]? noSink = null)
    {
        int n = graph.NCells;
        Section caves = cfg.Sec("caves");
        if (graph.Kind != "flat" || !caves.Contains("wall_retreat_m_per_yr") || !caves.Contains("active_zone_m"))
        {
            return null;
        }
        if (z.Length != n || receiver.Length != n || order.Length != n || source.Length != n || discharge.Length != n
            || isRiver.Length != n || isLake.Length != n || isOcean.Length != n || waterLevel.Length != n
            || surfaceRock.Length != n || uplift.Length != n || columns.NCells != n || (noSink is not null && noSink.Length != n))
        {
            throw new ArgumentException(
                $"KarstStreams.Apply 의 배열은 모두 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: z {z.Length}, receiver {receiver.Length}, "
                + $"order {order.Length}, source {source.Length}, discharge {discharge.Length}, isRiver {isRiver.Length}, "
                + $"isLake {isLake.Length}, isOcean {isOcean.Length}, waterLevel {waterLevel.Length}, surfaceRock {surfaceRock.Length}, "
                + $"uplift {uplift.Length}, columns {columns.NCells}, noSink {noSink?.Length.ToString() ?? "없음"}");
        }
        var p = new CaveNetworkParams(
            RadiusMaxM: cfg.F("caves.passage_radius_m"), SinkSpacingM: 0.0, SinkMinAreaM2: 0.0, PathWavelengthM: 1.0, Seed: 0,
            WallRetreatMPerYr: cfg.F("caves.wall_retreat_m_per_yr"), ActiveZoneM: cfg.F("caves.active_zone_m"),
            HydraulicGradient: cfg.F("groundwater.karst_gradient"),
            GravityMS2: Constants.Gravity(cfg.F("planet.radius_m"), cfg.F("planet.mean_density_kg_m3")));
        if (!(p.WallRetreatMPerYr > 0.0 && p.ActiveZoneM > 0.0 && p.HydraulicGradient > 0.0))
        {
            return null;
        }
        double qMin = cfg.F("rivers.min_discharge_m3_per_s") * Constants.SecondsPerYear;
        double maxDepth = cfg.F("groundwater.max_depth_m");
        double aquitardLogK = cfg.F("groundwater.aquitard_log10_perm_m2");
        double karstGrad = p.HydraulicGradient;

        // --- 1. 큰 물: 바다, 호수, 통로가 다 삼키지 못하는 강
        double[] capacity = new double[n];
        bool[] big = new bool[n];
        bool anyBig = false;
        bool anySmall = false;
        for (int c = 0; c < n; c++)
        {
            bool river = isRiver[c] && !isLake[c] && !isOcean[c];
            if (river)
            {
                capacity[c] = Capacity(uplift[c], p);
            }
            bool swallowable = river && receiver[c] != c && discharge[c] <= capacity[c];
            big[c] = (isOcean[c] || isLake[c] || (isRiver[c] && !swallowable)) && double.IsFinite(waterLevel[c]);
            anyBig |= big[c];
            anySmall |= swallowable;
        }
        if (!anyBig || !anySmall)
        {
            return null;
        }

        // --- 2. 떠 있는 칸. 물길의 끝 칸(receiver 가 자기 자신)과 noSink 칸은 물을 보내지 않습니다.
        (double[] dist, long[] src, double[] baseLevel) = Distance.NearestSourceValues(graph, big, waterLevel);
        double[] zKarst = new double[n];
        for (int c = 0; c < n; c++)
        {
            zKarst[c] = baseLevel[c] + (karstGrad * dist[c]);
        }
        byte[] rockAt = Model.RockAt(columns.Bottom, columns.Rock, columns.NLayers, zKarst);
        bool[] perched = new bool[n];
        long[] spring = src;
        for (int c = 0; c < n; c++)
        {
            if (big[c] || !isRiver[c] || !Rocks.Soluble[surfaceRock[c]] || src[c] < 0 || !double.IsFinite(zKarst[c])
                || receiver[c] == c || (noSink is not null && noSink[c]))
            {
                continue;
            }
            double drop = z[c] - zKarst[c];
            perched[c] = drop >= PerchMinM && drop <= maxDepth && Rocks.Soluble[rockAt[c]]
                && Groundwater.AquitardTop(columns, c, z[c], aquitardLogK) < zKarst[c];
        }

        // --- 3. 위쪽 칸부터 물을 모으며 떠 있는 칸에서 땅속으로 보냅니다(Route). 땅속 길이 고리를 만들면 고리의 칸을 막고 다시 모읍니다.
        int nLoopsCut = 0;
        while (true)
        {
            (double[] q, double[] loss, double[] rise, List<int> stuck) = Route(receiver, order, source, perched, spring, capacity, qMin);
            if (stuck.Count > 0)
            {
                // 남은 칸은 고리 위에 있거나 그 아래쪽입니다. 그 가운데 떠 있는 칸은 모두 물을 보내지 않게 하고 처음부터 다시 모읍니다.
                int cut = 0;
                foreach (int c in stuck)
                {
                    if (perched[c])
                    {
                        perched[c] = false;
                        cut++;
                    }
                }
                if (cut == 0)
                {
                    throw new ArgumentException(
                        $"지표 물길(receiver)에 고리가 있어 물을 모으지 못합니다. 고리에 걸린 칸 {stuck.Count}개, 처음 칸 {stuck[0]}. "
                        + "물길은 Routing 으로 다시 만드세요");
                }
                nLoopsCut += cut;
                continue;
            }
            double[] exchange = new double[n];
            int nSinks = 0;
            int nSprings = 0;
            double lost = 0.0;
            double risen = 0.0;
            for (int c = 0; c < n; c++)
            {
                exchange[c] = rise[c] - loss[c];
                nSinks += loss[c] > 0.0 ? 1 : 0;
                nSprings += rise[c] > 0.0 ? 1 : 0;
                lost += loss[c];
                risen += rise[c];
            }
            return nSinks == 0 ? null : new KarstStreamResult(exchange, q, nSinks, nSprings, lost, risen, big, nLoopsCut);
        }
    }

    /// <summary>
    /// 물을 위쪽 칸부터 한 번 모읍니다 (route). 화살표는 둘입니다: 지표 물길(칸 → receiver)과, 떠 있는 칸에서 그 샘으로 가는 땅속 길
    /// (칸 → spring). 들어오는 화살표가 모두 끝난 칸부터 고르므로(위상 정렬), 칸에 닿는 물은 그 칸을 고를 때 이미 다 와 있습니다.
    /// 같은 때에 고를 수 있는 칸은 order 를 거꾸로 읽은 차례(상류 먼저)로 고릅니다.
    /// 떠 있는 칸에 닿은 물 q 가 qMin 이상이고 capacity 이하면 모두 땅속으로 보내고(loss), 그 칸의 샘에서 솟게 합니다(rise).
    /// 샘으로 간 물은 샘에서 다시 지표 물길을 따라 흐릅니다.
    /// 반환: q (N,) 지표를 흐르는 물 (사라지는 칸은 0), loss, rise, 그리고 고리 때문에 고르지 못한 칸 (보통 비어 있음).
    /// </summary>
    internal static (double[] Q, double[] Loss, double[] Rise, List<int> Stuck) Route(
        long[] receiver, long[] order, double[] source, bool[] perched, long[] spring, double[] capacity, double qMin)
    {
        int n = source.Length;
        int[] pending = new int[n];
        for (int c = 0; c < n; c++)
        {
            int r = (int)receiver[c];
            if (r != c)
            {
                pending[r]++;
            }
            if (perched[c])
            {
                pending[(int)spring[c]]++;
            }
        }
        var ready = new Queue<int>();
        bool[] queued = new bool[n];
        for (int k = n - 1; k >= 0; k--)
        {
            int c = (int)order[k];
            if (pending[c] == 0 && !queued[c])
            {
                queued[c] = true;
                ready.Enqueue(c);
            }
        }
        double[] q = (double[])source.Clone();
        double[] loss = new double[n];
        double[] rise = new double[n];
        int done = 0;
        while (ready.TryDequeue(out int c))
        {
            done++;
            int r = (int)receiver[c];
            if (perched[c] && q[c] >= qMin && q[c] <= capacity[c])
            {
                int s = (int)spring[c];
                loss[c] = q[c];
                rise[s] += q[c];
                q[s] += q[c];
                q[c] = 0.0;
            }
            else if (r != c)
            {
                q[r] += q[c];
            }
            if (r != c && --pending[r] == 0)
            {
                ready.Enqueue(r);
            }
            if (perched[c] && --pending[(int)spring[c]] == 0)
            {
                ready.Enqueue((int)spring[c]);
            }
        }
        var stuck = new List<int>();
        if (done < n)
        {
            for (int c = 0; c < n; c++)
            {
                if (pending[c] > 0)
                {
                    stuck.Add(c);
                }
            }
        }
        return (q, loss, rise, stuck);
    }

    /// <summary>
    /// 칸마다 개울이 그 칸에서 땅속으로 사라질 수 있는지 (drains_to_spring): 그 칸의 아래층 물이 동굴을 타고 샘까지 가고, 개울 바닥이
    /// 그 물 높이보다 떠 있으면 참입니다. 굽기가 개울이 사라지는 칸에 싱크홀을 둘지 정하는 판단(CaveNetwork.Build)과 같습니다.
    /// - 샘: 그린 아래층(level0)으로 정한 강가 샘(CaveNetwork.BankSprings).
    /// - 길: route0 을 따라 샘까지 갑니다. route0 은 그린 아래층에, 지표에서 300 m(groundwater.max_depth_m)보다 깊어 그리지 않은
    ///   석회암의 아래 지하수면 높이를 더한 값입니다(Caves.RouteLevel0). 굽지 않는 깊이에도 물길은 있기 때문입니다.
    /// - 물 높이: 그 칸의 넘침 높이(CaveNetwork.SpillHead)입니다. 샘 쪽 길이 더 높은 자리를 넘어야 하면, 물은 그 높이까지 차올라야
    ///   빠집니다. 넘침 높이가 그 칸의 아래층과 같으면(차오르지 않아도 빠지면) 참이고, 더 높으면 개울 바닥(z)이 넘침 높이보다
    ///   KarstStreams.PerchMinM(10 m) 넘게 높을 때만 참입니다. 차오른 물이 바닥 가까이 닿으면 개울은 물을 잃지 않기 때문입니다.
    /// - 그 칸에 그린 아래층이 있어야 합니다(싱크홀의 수직굴이 내려갈 곳).
    /// 굽기는 히어로 묶음에 32비트 실수로 저장한 값을 읽으므로, 높이를 모두 같은 정밀도로 줄여 견줍니다. 굽기는 산비탈로 열리는
    /// 입구도 샘으로 더 쓰므로 넘침 높이가 같거나 낮습니다. 그래서 여기서 참인 칸은 굽기에서도 샘까지 길이 있습니다.
    /// level0: (N,) 아래층 높이 [m] (cave_level_0_m, 없으면 NaN). route0: (N,) Caves.RouteLevel0 의 결과 [m]. z: (N,) 지표 [m].
    /// isWater·waterLevel: (N,) 사라지는 개울을 넣은 뒤의 물 칸(강·호수·바다)과 수면 [m]. 반환: (N,).
    /// 예: 아래층 350.4 m, 개울 바닥 402.1 m 인 칸에서 샘 쪽 길이 351.15 m 를 넘어야 하면, 바닥이 넘침 높이보다 51 m 높아 참입니다.
    /// 샘 쪽 길이 403.3 m 를 넘어야 하면 물이 바닥보다 높이 차올라야 하므로 거짓입니다.
    /// </summary>
    public static bool[] DrainsToSpring(
        CellGraph graph, double[] level0, double[] route0, double[] z, bool[] isWater, double[] waterLevel)
    {
        int n = graph.NCells;
        if (level0.Length != n || route0.Length != n || z.Length != n || isWater.Length != n || waterLevel.Length != n)
        {
            throw new ArgumentException(
                $"level0, route0, z, isWater, waterLevel 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {level0.Length}, "
                + $"{route0.Length}, {z.Length}, {isWater.Length}, {waterLevel.Length}");
        }
        double[] lv = new double[n];
        double[] rt = new double[n];
        double[] wl = new double[n];
        bool[] wet = new bool[n];
        for (int c = 0; c < n; c++)
        {
            lv[c] = (float)level0[c];
            rt[c] = (float)route0[c];
            wl[c] = (float)waterLevel[c];
            wet[c] = isWater[c] && double.IsFinite(wl[c]);
            if (double.IsFinite(lv[c]) && lv[c] != rt[c])
            {
                throw new ArgumentException($"route0 은 아래층이 있는 칸에서 아래층 높이와 같아야 합니다. 칸 {c}: 아래층 {lv[c]}, route0 {rt[c]}");
            }
        }
        (bool[] spring, _) = CaveNetwork.BankSprings(graph, lv, wet, wl);
        double[] head = CaveNetwork.SpillHead(graph, rt, spring);
        bool[] drains = new bool[n];
        for (int c = 0; c < n; c++)
        {
            drains[c] = double.IsFinite(lv[c]) && double.IsFinite(head[c])
                && (head[c] <= lv[c] || (double)(float)z[c] - head[c] >= PerchMinM);
        }
        return drains;
    }

    /// <summary>
    /// 개울이 사라지는 칸(exchange 가 음수) 가운데, 그 물이 아래층 동굴을 타고 샘까지 갈 수 없는 칸 (without_spring).
    /// drains: DrainsToSpring 의 결과. 반환: 그런 칸 번호 (오름차순).
    /// </summary>
    public static int[] WithoutSpring(double[] exchange, bool[] drains)
    {
        if (exchange.Length != drains.Length)
        {
            throw new ArgumentException(
                $"exchange 와 drains 는 같은 길이여야 합니다. 받은 길이: {exchange.Length}, {drains.Length}");
        }
        var stray = new List<int>();
        for (int c = 0; c < exchange.Length; c++)
        {
            if (exchange[c] < 0.0 && !drains[c])
            {
                stray.Add(c);
            }
        }
        return stray.ToArray();
    }
}
