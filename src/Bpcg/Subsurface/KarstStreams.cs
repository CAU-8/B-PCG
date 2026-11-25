using System;
using Bpcg.Core;
using Bpcg.Geology;

namespace Bpcg.Subsurface;

/// <summary>KarstStreams.Apply 의 결과.</summary>
/// <param name="Exchange">(N,) 지표의 물이 땅속과 주고받는 양 [m³/yr]. 음수는 개울이 그 칸에서 땅속으로 사라지는 양, 양수는 샘으로 솟는 양.</param>
/// <param name="Discharge">(N,) 주고받은 뒤 지표를 흐르는 물 [m³/yr].</param>
/// <param name="NSinks">개울이 사라지는 칸 수.</param>
/// <param name="NSprings">그 물이 다시 솟는 칸 수.</param>
/// <param name="LostM3PerYr">사라지는 물의 합 [m³/yr].</param>
/// <param name="Passes">다시 계산한 횟수.</param>
/// <param name="Big">(N,) 큰 물: 바다, 호수, 통로가 다 삼키지 못하는 강. 석회암 지하수면의 기준입니다.</param>
/// <param name="Settled">물을 다시 모으는 횟수(최대 6번) 안에 솟는 자리가 더 바뀌지 않았는지. false 면 사라진 물과 솟은 물의 합이 다를 수 있습니다.</param>
public sealed record KarstStreamResult(
    double[] Exchange, double[] Discharge, int NSinks, int NSprings, double LostM3PerYr, int Passes, bool[] Big, bool Settled = true);

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
///    (지표 아래 max_depth_m 안), 그 사이에 물을 막는 층이 없는 칸.
/// 3. 상류부터 물을 모으다가, 떠 있는 칸에 강만큼의 물(rivers.min_discharge_m3_per_s 이상)이 닿고 그 양이 통로가 흘릴 수 있는
///    양 이하면 모두 땅속으로 보냅니다. 그 물은 그 칸의 지하수면을 정한 큰 물 칸에서 다시 솟습니다.
/// 지형은 바꾸지 않습니다. 솔버가 판 골짜기는 그대로 남고 물만 사라집니다.
/// 한계: 지하수면이 지표 아래 max_depth_m 보다 깊은 곳의 개울은 그대로 흐릅니다 (그 깊이의 동굴은 굽지 않아 이을 통로가 없음).
/// </remarks>
public static class KarstStreams
{
    /// <summary>개울 바닥이 석회암 지하수면보다 이만큼 넘게 높아야 물을 잃습니다 [m] (싱크홀을 두는 가장 얕은 깊이와 같은 값).</summary>
    public const double PerchMinM = CaveNetwork.SinkMinDepthM;

    /// <summary>다시 솟은 물이 아래쪽 개울을 바꾸면 다시 모으는 횟수의 상한.</summary>
    public const int MaxFlowPasses = 6;

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
    /// surfaceRock: (N,) 지표 암석. uplift: (N,) 솟는 속도 [m/yr]. 설정에 통로 굵기 키가 없거나 평면 그래프가 아니면 null.
    /// </summary>
    public static KarstStreamResult? Apply(
        CellGraph graph, double[] z, long[] receiver, long[] order, double[] source, double[] discharge, bool[] isRiver,
        bool[] isLake, bool[] isOcean, double[] waterLevel, byte[] surfaceRock, double[] uplift, LayerColumns columns, Config cfg)
    {
        int n = graph.NCells;
        Section caves = cfg.Sec("caves");
        if (graph.Kind != "flat" || !caves.Contains("wall_retreat_m_per_yr") || !caves.Contains("active_zone_m"))
        {
            return null;
        }
        if (z.Length != n || receiver.Length != n || order.Length != n || source.Length != n || discharge.Length != n
            || isRiver.Length != n || isLake.Length != n || isOcean.Length != n || waterLevel.Length != n
            || surfaceRock.Length != n || uplift.Length != n || columns.NCells != n)
        {
            throw new ArgumentException($"KarstStreams.Apply 의 배열은 모두 칸 수 ({n},) 와 같은 길이여야 합니다");
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

        // --- 2. 떠 있는 칸
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
            if (big[c] || !isRiver[c] || !Rocks.Soluble[surfaceRock[c]] || src[c] < 0 || !double.IsFinite(zKarst[c]))
            {
                continue;
            }
            double drop = z[c] - zKarst[c];
            perched[c] = drop >= PerchMinM && drop <= maxDepth && Rocks.Soluble[rockAt[c]]
                && Groundwater.AquitardTop(columns, c, z[c], aquitardLogK) < zKarst[c];
        }

        // --- 3. 상류부터 물을 모으며 떠 있는 칸에서 땅속으로 보냅니다. 솟는 자리가 더 위쪽의 흐름을 바꾸면 다시 모읍니다.
        double[] exchange = new double[n];
        double[] rise = new double[n];
        double[] q = new double[n];
        double[] loss = new double[n];
        int passes = 0;
        bool settled = false;
        for (int pass = 0; pass < MaxFlowPasses; pass++)
        {
            passes = pass + 1;
            Array.Clear(loss);
            for (int c = 0; c < n; c++)
            {
                q[c] = source[c] + rise[c];
            }
            // order 는 하류가 먼저라 뒤에서부터 돌면 상류가 먼저입니다.
            for (int k = n - 1; k >= 0; k--)
            {
                int c = (int)order[k];
                int r = (int)receiver[c];
                if (r == c)
                {
                    continue;
                }
                if (perched[c] && q[c] >= qMin && q[c] <= capacity[c])
                {
                    loss[c] = q[c];
                    q[c] = 0.0;
                    continue;
                }
                q[r] += q[c];
            }
            // 이번에 사라진 물이 솟는 자리. 지난번과 같으면 끝입니다.
            double[] riseNew = new double[n];
            for (int c = 0; c < n; c++)
            {
                if (loss[c] > 0.0)
                {
                    riseNew[spring[c]] += loss[c];
                }
            }
            bool same = true;
            for (int c = 0; c < n && same; c++)
            {
                same = riseNew[c] == rise[c];
            }
            if (same)
            {
                settled = true;
                break;
            }
            // 마지막 번에는 rise 를 바꾸지 않습니다. 돌려주는 유량(q)이 이번 번의 rise 로 모은 값이라, 주고받는 물(exchange)도
            // 같은 rise 와 짝지어야 칸마다 물수지가 맞습니다. 대신 사라진 물과 솟은 물의 합이 맞지 않을 수 있어 Settled 로 알립니다.
            if (pass < MaxFlowPasses - 1)
            {
                rise = riseNew;
            }
        }
        int nSinks = 0;
        int nSprings = 0;
        double lost = 0.0;
        for (int c = 0; c < n; c++)
        {
            exchange[c] = rise[c] - loss[c];
            nSinks += loss[c] > 0.0 ? 1 : 0;
            nSprings += rise[c] > 0.0 ? 1 : 0;
            lost += loss[c];
        }
        if (nSinks == 0)
        {
            return null;
        }
        return new KarstStreamResult(exchange, q, nSinks, nSprings, lost, passes, big, settled);
    }
}
