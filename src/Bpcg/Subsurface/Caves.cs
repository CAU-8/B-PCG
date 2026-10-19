using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 동굴 층과 입구 (docs/pipeline.md 8.4).
/// </summary>
/// <remarks>
/// 0층은 녹는 암석 안의 지금 물 높이(아래 지하수면), 위층은 가까운 강 수면 + k·f_k·골짜기 깊이(옛 강 높이)입니다.
/// 위층은 그 물 높이보다 통로 반지름 넘게 높아야(마른 층) 남깁니다. 두 층 모두 지표 아래 groundwater.max_depth_m
/// 안에만 둡니다(회랑 재질 부피를 굽는 깊이). 입구 칸은 entrance_spacing_m 안에 하나만 남깁니다.
/// </remarks>
public static class Caves
{
    public const int MaxLevels = 8;

    // 입구를 솎을 때 칸마다 매기는 우선순위의 해시 용도 번호.
    private const long StreamEntrance = 7251;

    /// <summary>층 k 의 FIELDS 이름 (cave_level_0_m, ...).</summary>
    public static string LevelFieldName(int k) => $"cave_level_{k}_m";

    private static void EntranceKernel(
        int[] nbr, double[] z, double[] zk, bool[] soluble, int m, double twoR, double band, double[] levelOut, byte[] entrance)
    {
        const int nSlots = CellGraph.NSlots;
        Parallelism.For(0, z.Length, c =>
        {
            int bits = 0;
            for (int k = 0; k < m; k++)
            {
                int ck = (c * m) + k;
                levelOut[ck] = NpMath.NaN;
                if (!soluble[ck])
                {
                    continue;
                }
                double d = z[c] - zk[ck];
                bool covered = d > twoR;
                bool inBand = Math.Abs(d) <= band;
                bool isEntrance = false;
                if (inBand)
                {
                    if (covered)
                    {
                        isEntrance = true;
                    }
                    else
                    {
                        for (int s = 0; s < nSlots; s++)
                        {
                            int v = nbr[(c * nSlots) + s];
                            if (v >= 0 && soluble[(v * m) + k] && z[v] - zk[(v * m) + k] > twoR)
                            {
                                isEntrance = true;
                                break;
                            }
                        }
                    }
                }
                else if (covered)
                {
                    double lim = zk[ck] + band;
                    for (int s = 0; s < nSlots; s++)
                    {
                        int v = nbr[(c * nSlots) + s];
                        if (v < 0)
                        {
                            continue;
                        }
                        double dv = z[v] - zk[(v * m) + k];
                        bool inCave = soluble[(v * m) + k] && (dv > twoR || Math.Abs(dv) <= band);
                        if (!inCave && z[v] <= lim)
                        {
                            isEntrance = true;
                            break;
                        }
                    }
                }
                if (isEntrance)
                {
                    bits |= 1 << k;
                }
                if (covered || isEntrance)
                {
                    levelOut[ck] = zk[ck];
                }
            }
            entrance[c] = (byte)bits;
        });
    }

    /// <summary>
    /// 층마다 입구 칸 사이를 spacing 넘게 띄웁니다. 칸마다 시드로 정한 우선순위를 매기고, spacing 안에 더 높은
    /// 우선순위의 입구가 있으면 그 칸의 입구를 지웁니다(같으면 번호가 작은 칸이 이김). 지운 칸이 땅속에 덮이지
    /// 않은 칸이면 그 층 높이도 NaN 으로 두어 동굴이 그 칸에서 지표로 나오지 않게 합니다.
    /// 칸마다 판정이 다른 칸의 판정과 상관없어 같은 시드면 같은 결과입니다.
    /// </summary>
    private static void ThinEntrances(
        CellGraph graph, double[] z, double[] zk, int m, double twoR, double spacing, long seed, double[] levelOut,
        byte[] entrance)
    {
        if (!(spacing > 0.0))
        {
            return;
        }
        double[] pos = graph.Pos;
        double s2 = spacing * spacing;
        for (int k = 0; k < m; k++)
        {
            var cand = new List<int>();
            for (int c = 0; c < z.Length; c++)
            {
                if (((entrance[c] >> k) & 1) != 0)
                {
                    cand.Add(c);
                }
            }
            if (cand.Count < 2)
            {
                continue;
            }
            var bucket = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Key(int c) => (
                (long)Math.Floor(pos[c * 3] / spacing), (long)Math.Floor(pos[(c * 3) + 1] / spacing),
                (long)Math.Floor(pos[(c * 3) + 2] / spacing));
            foreach (int c in cand)
            {
                (long, long, long) key = Key(c);
                if (!bucket.TryGetValue(key, out List<int>? list))
                {
                    list = [];
                    bucket[key] = list;
                }
                list.Add(c);
            }
            ulong Priority(int c) => Hashing.Hash3(seed, StreamEntrance, ((long)c * m) + k);
            bool[] drop = new bool[cand.Count];
            int kk = k;
            Parallelism.For(0, cand.Count, i =>
            {
                int c = cand[i];
                ulong pc = Priority(c);
                (long bx, long by, long bz) = Key(c);
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            if (!bucket.TryGetValue((bx + dx, by + dy, bz + dz), out List<int>? list))
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
                                double ez = pos[(v * 3) + 2] - pos[(c * 3) + 2];
                                if ((ex * ex) + (ey * ey) + (ez * ez) > s2)
                                {
                                    continue;
                                }
                                ulong pv = Priority(v);
                                if (pv > pc || (pv == pc && v < c))
                                {
                                    drop[i] = true;
                                    return;
                                }
                            }
                        }
                    }
                }
            });
            for (int i = 0; i < cand.Count; i++)
            {
                if (!drop[i])
                {
                    continue;
                }
                int c = cand[i];
                int ck = (c * m) + kk;
                entrance[c] = (byte)(entrance[c] & ~(1 << kk));
                if (!(z[c] - zk[ck] > twoR))
                {
                    levelOut[ck] = NpMath.NaN;
                }
            }
        }
    }

    /// <summary>
    /// 동굴 층 높이와 입구 비트 (cave_levels). 반환: cave_level_&lt;k&gt;_m (동굴 없으면 NaN), cave_entrance.
    /// </summary>
    public static FieldSet CaveLevels(
        CellGraph graph, double[] z, double[] zGw, double[] baseLevel, double[] valleyDepth, LayerColumns columns, Config cfg)
    {
        int n = graph.NCells;
        double[] zz = Water.AsVector(z, n, "z");
        double[] gw = Water.AsVector(zGw, n, "z_gw");
        if (valleyDepth.Length != n)
        {
            throw new ArgumentException($"valley_depth 는 ({n},) 배열이어야 합니다 (받은 길이: {valleyDepth.Length})");
        }
        if (baseLevel.Length != n)
        {
            throw new ArgumentException($"base_level 은 ({n},) 배열이어야 합니다 (받은 길이: {baseLevel.Length})");
        }
        foreach (double v in valleyDepth)
        {
            if (double.IsInfinity(v) || v < 0.0)
            {
                throw new ArgumentException("valley_depth 는 0 이상이어야 합니다 (강이 없는 칸만 NaN)");
            }
        }
        if (columns.NCells != n)
        {
            throw new ArgumentException($"columns 의 칸 수 {columns.NCells} 가 그래프 칸 수 {n} 과 다릅니다");
        }
        int levels = (int)cfg.I("caves.levels");
        double fK = cfg.F("caves.incision_fraction");
        double r = cfg.F("caves.passage_radius_m");
        double tol = cfg.F("caves.entrance_tolerance_m");
        double spacing = cfg.F("caves.entrance_spacing_m");
        double maxDepth = cfg.F("groundwater.max_depth_m");
        if (!(levels >= 1 && levels <= MaxLevels))
        {
            throw new ArgumentException($"caves.levels 는 1..{MaxLevels} 이어야 합니다: {levels}");
        }
        if (!(fK >= 0.0 && r > 0.0 && tol >= 0.0 && spacing >= 0.0))
        {
            throw new ArgumentException(
                "caves 설정은 incision_fraction ≥ 0, passage_radius_m > 0, entrance_tolerance_m ≥ 0, entrance_spacing_m ≥ 0 "
                + $"이어야 합니다. 받은 값: {fK}, {r}, {tol}, {spacing}");
        }
        var names = new List<string>();
        for (int k = 0; k < levels; k++)
        {
            names.Add(LevelFieldName(k));
        }
        Fields.CheckFields(names);

        // 0층: 녹는 암석 안의 지금 물 높이(zGw 로 받는 아래 지하수면). 지표 아래 최대 깊이 안일 때만.
        // 위층: 옛 강 높이 = 가까운 강 수면 + k·f_k·골짜기 깊이. 0층 물 높이보다 r 넘게 높고(마른 층) 최대 깊이 안일 때만.
        double[] zk = new double[n * levels];
        for (int c = 0; c < n; c++)
        {
            zk[c * levels] = zz[c] - gw[c] <= maxDepth ? gw[c] : NpMath.NaN;
            for (int k = 1; k < levels; k++)
            {
                double v = baseLevel[c] + (k * fK * valleyDepth[c]);
                zk[(c * levels) + k] = v > gw[c] + r && zz[c] - v <= maxDepth ? v : NpMath.NaN;
            }
        }
        byte[] rock = Model.RockAt(columns.Bottom, columns.Rock, columns.NLayers, zk);
        bool[] soluble = new bool[zk.Length];
        for (int i = 0; i < zk.Length; i++)
        {
            soluble[i] = Rocks.Soluble[rock[i]] && double.IsFinite(zk[i]);
        }
        double[] levelOut = new double[n * levels];
        byte[] entrance = new byte[n];
        EntranceKernel(graph.Nbr, zz, zk, soluble, levels, 2.0 * r, tol + r, levelOut, entrance);
        ThinEntrances(graph, zz, zk, levels, 2.0 * r, spacing, cfg.I("planet.seed"), levelOut, entrance);
        var o = new FieldSet();
        for (int k = 0; k < levels; k++)
        {
            double[] col = new double[n];
            for (int c = 0; c < n; c++)
            {
                col[c] = levelOut[(c * levels) + k];
            }
            o[names[k]] = col;
        }
        o["cave_entrance"] = entrance;
        return o;
    }
}
