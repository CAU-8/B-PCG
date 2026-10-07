using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 동굴 층과 입구 (src/bpcg/subsurface/caves.py, docs/pipeline.md 8.4).
/// </summary>
public static class Caves
{
    public const int MaxLevels = 8;

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
    /// 동굴 층 높이와 입구 비트 (cave_levels). 반환: cave_level_&lt;k&gt;_m (동굴 없으면 NaN), cave_entrance.
    /// </summary>
    public static FieldSet CaveLevels(CellGraph graph, double[] z, double[] zGw, double[] valleyDepth, LayerColumns columns, Config cfg)
    {
        int n = graph.NCells;
        double[] zz = Water.AsVector(z, n, "z");
        double[] gw = Water.AsVector(zGw, n, "z_gw");
        if (valleyDepth.Length != n)
        {
            throw new ArgumentException($"valley_depth 는 ({n},) 배열이어야 합니다");
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
        if (!(levels >= 1 && levels <= MaxLevels))
        {
            throw new ArgumentException($"caves.levels 는 1..{MaxLevels} 이어야 합니다: {levels}");
        }
        if (!(fK >= 0.0 && r > 0.0 && tol >= 0.0))
        {
            throw new ArgumentException(
                $"caves 설정은 incision_fraction ≥ 0, passage_radius_m > 0, entrance_tolerance_m ≥ 0 이어야 합니다: {fK}, {r}, {tol}");
        }
        var names = new List<string>();
        for (int k = 0; k < levels; k++)
        {
            names.Add(LevelFieldName(k));
        }
        Fields.CheckFields(names);

        double[] zk = new double[n * levels];
        for (int c = 0; c < n; c++)
        {
            zk[c * levels] = gw[c];
            for (int k = 1; k < levels; k++)
            {
                zk[(c * levels) + k] = gw[c] + (k * fK * valleyDepth[c]);
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
