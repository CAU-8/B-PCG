using System;
using Bpcg.Core;
using Bpcg.Hydro;

namespace Bpcg.Geology;

/// <summary>
/// 광상 가능성 값: 쓸모 있는 광물이 모여 있을 만한 자리를 칸마다 0~1 로 적은 지도.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가.</b> 광물을 아무 데나 흩어 놓으면 지형을 읽어 광물을 찾는 일이 생기지 않습니다. 실제 광상은 판과 강이 만든 자리에 모입니다.
/// 그래서 광물을 직접 놓지 않고, 지금 있는 판·지질·물길 값으로 '여기에 있을 만하다'는 값만 셈합니다. 광물을 3D 로 놓는 일은 이 값을 읽는 쪽(게임)이 합니다.
/// </para>
/// <para>
/// <b>원래 광맥 (<c>prospect_lode</c>).</b> 구리와 금은 바다 판이 다른 판 밑으로 파고드는 곳(섭입대)의 위쪽 판에서, 화산이 줄지어 서는 띠(화산호) 아래에 생깁니다.
/// 칸이 육지이고 위쪽 판에 있으면, 화산호 띠의 한가운데에서 벗어난 거리 x 로 exp(−(x ÷ w)²) 를 셈합니다. w 는 화산호 띠 너비의 절반입니다.
/// 예: 띠 너비가 100 km 면 w = 50 km 이고, 띠 한가운데는 1, 50 km 벗어난 칸은 exp(−1) ≈ 0.37 입니다.
/// 광상은 띠 전체가 아니라 군데군데 모여 있으므로, 땅을 한 변 <c>minerals.cluster_m</c> 인 상자로 나눠 상자마다 해시로 '있음/없음'을 정하고,
/// 없는 상자의 칸은 0 으로 둡니다. 있는 상자의 비율은 <c>minerals.cluster_fraction</c> 입니다.
/// </para>
/// <para>
/// <b>사금 (<c>prospect_placer</c>).</b> 사금은 광맥이 깎여 나온 금 알갱이가 강을 타고 내려가다, 물살이 느려지는 곳에 가라앉아 모인 것입니다. 두 값을 곱합니다.
/// 첫째는 공급: 그 칸으로 물이 모이는 땅(유역) 전체에서 원래 광맥 값의 넓이 평균입니다. 물길을 따라 '광맥 값 × 칸 넓이'를 더한 뒤 상류 면적으로 나눕니다.
/// 둘째는 덫: 물살이 느려지는 정도입니다. 바로 위쪽 본줄기 칸보다 경사가 줄어든 비율(1 − 이 칸 경사 ÷ 위쪽 칸 경사)이고, 선상지 칸은 1, 충적층이 쌓인 칸은 적어도 0.7 입니다.
/// 사금 값은 강 칸과 선상지 칸에서만 min(공급 × <c>minerals.placer_gain</c>, 1) × 덫 이고, 그 밖의 칸은 0 입니다.
/// 예: 유역의 10% 가 광맥 값 1 인 땅이면 공급은 0.1, 배율 5 를 곱해 0.5 이고, 경사가 위쪽 칸의 절반으로 줄었으면 덫 0.5 를 곱해 0.25 입니다.
/// 상류에 광맥이 없으면 공급이 0 이라 사금 값도 정확히 0 입니다.
/// </para>
/// <para>
/// 숫자(띠 모양, 상자 크기, 배율)는 근거 논문 없이 정한 값입니다. 이 지도는 '어디를 찾아볼 만한가'를 정하는 놀이용 값이고, 실제 매장량을 뜻하지 않습니다.
/// </para>
/// </remarks>
public static class Prospects
{
    /// <summary>광맥이 모여 있는 상자의 한 변 (m). 설정에 <c>minerals.cluster_m</c> 이 없을 때 씁니다 (지구 크기).</summary>
    public const double ClusterM = 60_000.0;

    /// <summary>광맥이 있는 상자의 비율. 설정에 <c>minerals.cluster_fraction</c> 이 없을 때 씁니다.</summary>
    public const double ClusterFraction = 0.35;

    /// <summary>사금의 공급 값에 곱하는 배율. 설정에 <c>minerals.placer_gain</c> 이 없을 때 씁니다.</summary>
    public const double PlacerGain = 5.0;

    /// <summary>충적층이 쌓인 강 칸의 덫 값 (적어도 이 값).</summary>
    public const double AlluviumTrap = 0.7;

    private const long ClusterStream = 9301;

    /// <summary>
    /// 행성 단계의 필드로 광상 가능성 값 둘을 셈합니다. 돌려주는 필드: <c>prospect_lode</c>, <c>prospect_placer</c> (float32, 0~1).
    /// </summary>
    /// <param name="graph">행성 격자.</param>
    /// <param name="fields">1~4단계가 끝난 필드. 판(subduction_side, dist_convergent_m, dist_arc_m, is_ocean)과 물길(receiver, drainage_area_m2, slope, is_river, fan, alluvium_m)을 읽습니다.</param>
    /// <param name="cfg">설정. geology.arc_belt_width_m, planet.seed, minerals.* 를 읽습니다.</param>
    public static FieldSet Compute(CellGraph graph, FieldSet fields, Config cfg)
    {
        double[] lode = Lode(
            graph, fields.Get<sbyte>("subduction_side"), fields.GetFloat64("dist_convergent_m"), fields.GetFloat64("dist_arc_m"),
            fields.Get<bool>("is_ocean"), 0.5 * cfg.F("geology.arc_belt_width_m"), cfg.FOr("minerals.cluster_m", ClusterM),
            cfg.FOr("minerals.cluster_fraction", ClusterFraction), cfg.I("planet.seed"));
        double[] placer = Placer(
            fields.Get<long>("receiver"), graph.Area, fields.GetFloat64("drainage_area_m2"), fields.GetFloat64("slope"), fields.Get<bool>("is_river"),
            fields.Get<bool>("fan"), fields.GetFloat64("alluvium_m"), lode, cfg.FOr("minerals.placer_gain", PlacerGain));
        return new FieldSet
        {
            ["prospect_lode"] = lode,
            ["prospect_placer"] = placer,
        };
    }

    /// <summary>원래 광맥 가능성 (0~1). 육지이고 섭입대의 위쪽 판이며, 광맥이 있는 상자에 든 칸만 0 보다 큽니다.</summary>
    /// <param name="graph">격자. 칸의 자리(pos)로 상자를 나눕니다.</param>
    /// <param name="side">칸마다 +1 위쪽 판, −1 파고드는 판, 0 해당 없음.</param>
    /// <param name="distConvergentM">칸마다 가장 가까운 수렴 경계까지 거리 (m).</param>
    /// <param name="distArcM">칸마다 수렴 경계에서 화산호 띠의 한가운데까지 거리 (m). 위쪽 판이 아니면 NaN.</param>
    /// <param name="isOcean">칸마다 바다인지.</param>
    /// <param name="halfWidthM">화산호 띠 너비의 절반 (m).</param>
    /// <param name="clusterM">상자의 한 변 (m).</param>
    /// <param name="clusterFraction">광맥이 있는 상자의 비율 (0~1).</param>
    /// <param name="seed">행성 시드.</param>
    public static double[] Lode(
        CellGraph graph, sbyte[] side, double[] distConvergentM, double[] distArcM, bool[] isOcean,
        double halfWidthM, double clusterM, double clusterFraction, long seed)
    {
        if (!(halfWidthM > 0.0) || !(clusterM > 0.0) || !(clusterFraction >= 0.0 && clusterFraction <= 1.0))
        {
            throw new ArgumentException(
                $"화산호 띠 너비의 절반({halfWidthM} m)과 minerals.cluster_m({clusterM} m)은 0 보다 커야 하고, "
                + $"minerals.cluster_fraction({clusterFraction})은 0~1 이어야 합니다.");
        }
        int n = graph.NCells;
        double[] lode = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (isOcean[c] || side[c] != 1 || !double.IsFinite(distArcM[c]))
            {
                continue;
            }
            // 상자 번호: 칸의 자리를 상자 한 변으로 나눈 몫 셋. 한 줄에 100만 개까지 들어가게 섞어 번호 하나로 만듭니다.
            long bx = (long)Math.Floor(graph.Pos[3 * c] / clusterM);
            long by = (long)Math.Floor(graph.Pos[(3 * c) + 1] / clusterM);
            long bz = (long)Math.Floor(graph.Pos[(3 * c) + 2] / clusterM);
            long box = (((bx * 1_000_003L) + by) * 1_000_003L) + bz;
            if (Hashing.HashUnit(seed, box, ClusterStream) >= clusterFraction)
            {
                continue;
            }
            double x = (distConvergentM[c] - distArcM[c]) / halfWidthM;
            lode[c] = Math.Exp(-(x * x));
        }
        return lode;
    }

    /// <summary>사금 가능성 (0~1). 강 칸과 선상지 칸에서만 0 보다 클 수 있고, 상류에 광맥이 없으면 0 입니다.</summary>
    /// <param name="rcv">칸마다 물을 보내는 이웃 (출구는 자기 자신).</param>
    /// <param name="area">칸 넓이 (m²).</param>
    /// <param name="drainageAreaM2">칸마다 상류 면적 (m², 자기 칸 포함).</param>
    /// <param name="slope">칸마다 물을 보내는 이웃까지의 경사.</param>
    /// <param name="isRiver">칸마다 강인지.</param>
    /// <param name="fan">칸마다 선상지인지.</param>
    /// <param name="alluviumM">칸마다 충적층 두께 (m).</param>
    /// <param name="lode">칸마다 원래 광맥 가능성.</param>
    /// <param name="gain">공급 값에 곱하는 배율.</param>
    public static double[] Placer(
        long[] rcv, double[] area, double[] drainageAreaM2, double[] slope, bool[] isRiver, bool[] fan, double[] alluviumM,
        double[] lode, double gain)
    {
        int n = rcv.Length;
        double[] weight = new double[n];
        for (int c = 0; c < n; c++)
        {
            weight[c] = lode[c] * area[c];
        }
        double[] supplied = AccumulateModule.Accumulate(rcv, Routing.TopoOrder(rcv), weight);

        // 칸마다 바로 위쪽 본줄기(상류 면적이 가장 큰 기여 칸)의 경사. 같으면 번호가 작은 칸입니다.
        double[] upSlope = new double[n];
        double[] upArea = new double[n];
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r != c && drainageAreaM2[c] > upArea[r])
            {
                upArea[r] = drainageAreaM2[c];
                upSlope[r] = slope[c];
            }
        }
        double[] placer = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!(isRiver[c] || fan[c]) || !(supplied[c] > 0.0) || !(drainageAreaM2[c] > 0.0))
            {
                continue;
            }
            double supply = Math.Min(supplied[c] / drainageAreaM2[c] * gain, 1.0);
            double trap = upSlope[c] > 0.0 ? Math.Clamp(1.0 - (slope[c] / upSlope[c]), 0.0, 1.0) : 0.0;
            if (fan[c])
            {
                trap = 1.0;
            }
            else if (alluviumM[c] > 0.0)
            {
                trap = Math.Max(trap, AlluviumTrap);
            }
            placer[c] = supply * trap;
        }
        return placer;
    }
}
