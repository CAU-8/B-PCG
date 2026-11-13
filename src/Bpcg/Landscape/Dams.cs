using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Landscape;

/// <summary>[dams] 설정 값 (DamSettings). 유량은 m³/yr 로 바꿔 둡니다.</summary>
public sealed record DamSettings(
    double SpacingM, double MinHeightM, double MaxHeightM, double Slope, double MinDischargeM3PerYr, double MaxDischargeM3PerYr,
    double MinSlopeRatio, long Seed);

/// <summary>
/// 산사태 둑 (docs/pipeline.md 7.4). 가파른 산비탈이 무너져 골짜기를 막으면 그 위쪽에 물이 고여 호수가 됩니다.
/// 솔버가 만든 지형에는 물이 고일 우묵한 자리가 없어서, 강 위 몇 곳에 돌무더기 둔덕을 얹어 그 자리를 만듭니다.
/// 호수 자체는 뒤의 물 단계(Water.WaterBodies)가 우묵한 자리를 넘칠 높이까지 채워서 정합니다.
/// </summary>
public static class Dams
{
    // 둑 자리를 고르는 우선순위와 둑 높이를 만드는 해시의 용도 번호.
    private const long StreamSite = 7111;
    private const long StreamHeight = 7112;

    /// <summary>
    /// 설정 [dams] 를 읽어 검사합니다. 절이 없거나(이 절이 생기기 전의 설정) enabled 가 false 면 null.
    /// </summary>
    public static DamSettings? Settings(Config cfg)
    {
        if (!cfg.Contains("dams") || !cfg.B("dams.enabled"))
        {
            return null;
        }
        double spacing = cfg.F("dams.spacing_m");
        double hMin = cfg.F("dams.min_height_m");
        double hMax = cfg.F("dams.max_height_m");
        double slope = cfg.F("dams.slope");
        double qMin = cfg.F("dams.min_discharge_m3_per_s");
        double qMax = cfg.F("dams.max_discharge_m3_per_s");
        double ratio = cfg.F("dams.min_slope_ratio");
        if (!(spacing > 0.0 && hMin > 0.0 && hMax >= hMin && slope > 0.0))
        {
            throw new ArgumentException(
                "dams 는 spacing_m > 0, 0 < min_height_m ≤ max_height_m, slope > 0 이어야 합니다. "
                + $"받은 값: spacing_m {spacing}, min_height_m {hMin}, max_height_m {hMax}, slope {slope}");
        }
        if (!(qMin >= 0.0 && qMax >= qMin && ratio >= 0.0))
        {
            throw new ArgumentException(
                "dams 는 0 ≤ min_discharge_m3_per_s ≤ max_discharge_m3_per_s, min_slope_ratio ≥ 0 이어야 합니다. "
                + $"받은 값: min_discharge_m3_per_s {qMin}, max_discharge_m3_per_s {qMax}, min_slope_ratio {ratio}");
        }
        return new DamSettings(
            spacing, hMin, hMax, slope, qMin * Constants.SecondsPerYear, qMax * Constants.SecondsPerYear, ratio, cfg.I("planet.seed"));
    }

    /// <summary>
    /// 둑을 놓을 강 칸을 고릅니다 (우선순위가 높은 것부터). 후보는 유량이 범위 안이고, 바로 옆에 무너지기 직전의
    /// 비탈(경사 ≥ MinSlopeRatio × S_crit)이 있는 칸입니다. 우선순위는 시드와 칸 번호의 해시이고, 이미 고른 둑에서
    /// SpacingM 안에 있는 후보는 버립니다. exclude 가 true 인 칸(선상지)은 후보에서 뺍니다.
    /// </summary>
    public static int[] FindDamSites(CellGraph graph, double[] z, SolverResult result, DamSettings st, bool[]? exclude = null)
    {
        int n = graph.NCells;
        long[] rcv = result.Receiver;
        double[] q = result.Discharge;
        double[] slope = result.Slope;
        double[] sCrit = result.SCritSurface;
        int[] nbr = graph.Nbr;
        var cand = new List<int>();
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r == c || rcv[r] == r || (exclude is not null && exclude[c]))
            {
                continue;
            }
            if (!(q[c] >= st.MinDischargeM3PerYr && q[c] <= st.MaxDischargeM3PerYr))
            {
                continue;
            }
            bool steepSide = false;
            for (int s = 0; s < CellGraph.NSlots && !steepSide; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                steepSide = v >= 0 && v != r && z[v] > z[c] && sCrit[v] > 0.0 && slope[v] >= st.MinSlopeRatio * sCrit[v];
            }
            if (steepSide)
            {
                cand.Add(c);
            }
        }
        if (cand.Count == 0)
        {
            return [];
        }
        // 우선순위 오름차순, 같으면 칸 번호 오름차순.
        ulong[] prio = new ulong[cand.Count];
        for (int k = 0; k < prio.Length; k++)
        {
            prio[k] = Hashing.Hash3(st.Seed, StreamSite, cand[k]);
        }
        int[] byPrio = new int[cand.Count];
        for (int k = 0; k < byPrio.Length; k++)
        {
            byPrio[k] = k;
        }
        Array.Sort(byPrio, (a, b) =>
        {
            int cmp = prio[a].CompareTo(prio[b]);
            return cmp != 0 ? cmp : cand[a].CompareTo(cand[b]);
        });
        double[] pos = graph.Pos;
        double s2 = st.SpacingM * st.SpacingM;
        var keep = new List<int>();
        foreach (int k in byPrio)
        {
            int c = cand[k];
            bool near = false;
            foreach (int d in keep)
            {
                double ex = pos[d * 3] - pos[c * 3];
                double ey = pos[(d * 3) + 1] - pos[(c * 3) + 1];
                double ez = pos[(d * 3) + 2] - pos[(c * 3) + 2];
                if ((ex * ex) + (ey * ey) + (ez * ez) < s2)
                {
                    near = true;
                    break;
                }
            }
            if (!near)
            {
                keep.Add(c);
            }
        }
        return keep.ToArray();
    }

    /// <summary>둑 높이 [m]: 칸 번호와 시드의 해시로 MinHeightM ~ MaxHeightM 사이에서 정합니다.</summary>
    public static double Height(int cell, DamSettings st) =>
        st.MinHeightM + ((st.MaxHeightM - st.MinHeightM) * Hashing.HashUnit(st.Seed, StreamHeight, cell));

    /// <summary>
    /// 강 위 몇 곳에 산사태 둑을 얹습니다 (add_dams). 둑은 둑 자리의 강바닥에서 높이 H 만큼 솟고 사방으로 경사 Slope 로
    /// 낮아지는 원뿔 모양 둔덕이고, 둔덕이 원래 땅보다 높은 칸만 올립니다. 반환: (새 고도, 둑 칸, 둑 목록).
    /// 구면 그래프(행성 지도)는 칸이 둑보다 훨씬 커서 둑을 놓지 않습니다.
    /// </summary>
    public static (double[] ZNew, bool[] Dam, List<object?> Dams) AddDams(
        CellGraph graph, double[] z, SolverResult result, Config cfg, bool[]? exclude = null)
    {
        int n = graph.NCells;
        if (z.Length != n || result.Receiver.Length != n)
        {
            throw new ArgumentException($"z 와 result 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: z {z.Length}, receiver {result.Receiver.Length}");
        }
        if (exclude is not null && exclude.Length != n)
        {
            throw new ArgumentException($"exclude 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이 {exclude.Length}");
        }
        double[] zNew = (double[])z.Clone();
        bool[] dam = new bool[n];
        var dams = new List<object?>();
        DamSettings? st = Settings(cfg);
        if (st is null || graph.Kind == "sphere")
        {
            return (zNew, dam, dams);
        }
        int[] sites = FindDamSites(graph, z, result, st, exclude);
        if (sites.Length == 0)
        {
            return (zNew, dam, dams);
        }
        var tree = new KdTree(graph.Pos);
        double[] pos = graph.Pos;
        long[] rcv = result.Receiver;
        foreach (int a in sites)
        {
            double h = Height(a, st);
            double radius = h / st.Slope;
            ReadOnlySpan<double> pa = pos.AsSpan(a * 3, 3);
            long nRaised = 0;
            double volume = 0.0;
            List<int> ball = tree.QueryBallPoint(pa, radius);
            ball.Sort();
            foreach (int i in ball)
            {
                if (rcv[i] == i)
                {
                    continue;
                }
                double ex = pos[i * 3] - pa[0];
                double ey = pos[(i * 3) + 1] - pa[1];
                double ez = pos[(i * 3) + 2] - pa[2];
                double top = z[a] + h - (st.Slope * Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez)));
                if (top > zNew[i])
                {
                    volume += (top - zNew[i]) * graph.Area[i];
                    zNew[i] = top;
                    dam[i] = true;
                    nRaised++;
                }
            }
            if (nRaised == 0)
            {
                continue;
            }
            dams.Add(new OrderedDictionary<string, object?>
            {
                ["cell"] = (long)a,
                ["height_m"] = h,
                ["discharge_m3_per_yr"] = result.Discharge[a],
                ["z_m"] = z[a],
                ["n_raised"] = nRaised,
                ["volume_m3"] = volume,
                ["pos"] = new List<object?> { pa[0], pa[1], pa[2] },
            });
        }
        return (zNew, dam, dams);
    }
}
