using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>
/// 지각: 대륙 마스크, 해양저 나이, 지각 두께, 기준 고도 (src/bpcg/planet/crust.py, docs/pipeline.md 4.2).
/// </summary>
public static class Crust
{
    public const long StreamContinentNoise = 311;
    public const long StreamContinentBias = 312;
    public const double ContinentNoiseFrequency = 1.2;
    public const int ContinentNoiseOctaves = 4;
    public const double ContinentPlateBias = 0.3;
    public const double ContinentBiasSharpness = 12.0;
    public const double MarginEdgeFraction = 0.55;
    public const double TransitionHalfWidthM = 50_000.0;
    public const double PsSqrtCoefM = 350.0;
    public const double PsBreakMyr = 70.0;
    public const double PsDeepM = 6400.0;
    public const double PsDeepAmpM = 3200.0;
    public const double PsTauMyr = 62.8;
    public const byte Continental = 1;
    public const byte Oceanic = 0;

    /// <summary>smoothstep: 3t² − 2t³, t = clip(x, 0, 1).</summary>
    public static double Smoothstep(double x)
    {
        double t = NpMath.Clip(x, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    /// <summary>out[c] = Σ_k b_k·w_k / Σ_k w_k, w_k = exp(β(p·s_k − max_j p·s_j)) (_soft_plate_bias_kernel).</summary>
    internal static void SoftPlateBiasKernel(double[] unit, double[] seeds, double[] bias, double sharp, double[] output)
    {
        int m = seeds.Length / 3;
        Parallel.For(0, unit.Length / 3, c =>
        {
            double best = double.NegativeInfinity;
            for (int k = 0; k < m; k++)
            {
                double d = (unit[c * 3] * seeds[k * 3]) + (unit[(c * 3) + 1] * seeds[(k * 3) + 1])
                    + (unit[(c * 3) + 2] * seeds[(k * 3) + 2]);
                if (d > best)
                {
                    best = d;
                }
            }
            double num = 0.0;
            double den = 0.0;
            for (int k = 0; k < m; k++)
            {
                double d = (unit[c * 3] * seeds[k * 3]) + (unit[(c * 3) + 1] * seeds[(k * 3) + 1])
                    + (unit[(c * 3) + 2] * seeds[(k * 3) + 2]);
                double w = Math.Exp(sharp * (d - best));
                num += w * bias[k];
                den += w;
            }
            output[c] = num / den;
        });
    }

    /// <summary>대륙 점수 (N,) = 낮은 주파수 fbm + 판별 치우침 (continent_score).</summary>
    public static double[] ContinentScore(CellGraph graph, Config cfg)
    {
        Plates.CheckSphereGraph(graph);
        long seed = cfg.I("planet.seed");
        double[] unit = graph.Unit();
        double[] noise = Noise.Fbm3(
            unit, Plates.SubSeed(seed, StreamContinentNoise), ContinentNoiseOctaves, frequency: ContinentNoiseFrequency);
        double[] seeds = Plates.PlateSeeds(cfg);
        long bSeed = Plates.SubSeed(seed, StreamContinentBias);
        int m = seeds.Length / 3;
        double[] bias = new double[m];
        for (int k = 0; k < m; k++)
        {
            bias[k] = ContinentPlateBias * ((2.0 * Hashing.HashUnit(bSeed, k, 0)) - 1.0);
        }
        double[] output = new double[graph.NCells];
        SoftPlateBiasKernel(unit, seeds, bias, ContinentBiasSharpness, output);
        for (int c = 0; c < output.Length; c++)
        {
            output[c] = noise[c] + output[c];
        }
        return output;
    }

    /// <summary>
    /// 대륙 지각 마스크 (continent_mask): 점수가 큰 칸부터(같으면 번호순) 면적을 더해 continental_area_fraction 에
    /// 가장 가깝게 자릅니다.
    /// </summary>
    public static bool[] ContinentMask(CellGraph graph, Config cfg)
    {
        double frac = cfg.F("plates.continental_area_fraction");
        if (!(0.0 <= frac && frac <= 1.0))
        {
            throw new ArgumentException($"continental_area_fraction 은 0~1 이어야 합니다: {frac}");
        }
        double[] score = ContinentScore(graph, cfg);
        int n = graph.NCells;
        // np.lexsort((arange(n), -score)): −score 오름차순, 같으면 번호순 (NaN 은 뒤)
        int[] order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (x, y) =>
        {
            double a = -score[x];
            double b = -score[y];
            bool an = double.IsNaN(a);
            bool bn = double.IsNaN(b);
            if (an || bn)
            {
                return an && bn ? x.CompareTo(y) : (an ? 1 : -1);
            }
            int cmp = a.CompareTo(b);
            return cmp != 0 ? cmp : x.CompareTo(y);
        });
        double[] csum = new double[n];
        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            acc += graph.Area[order[i]];
            csum[i] = acc;
        }
        double target = frac * csum[n - 1];
        int k = SearchSortedLeft(csum, target); // csum[k] ≥ target 인 첫 번호
        int nTake;
        if (k >= n)
        {
            nTake = n;
        }
        else if (k == 0)
        {
            nTake = Math.Abs(csum[0] - target) < target ? 1 : 0;
        }
        else
        {
            nTake = Math.Abs(csum[k] - target) <= Math.Abs(csum[k - 1] - target) ? k + 1 : k;
        }
        bool[] mask = new bool[n];
        for (int i = 0; i < nTake; i++)
        {
            mask[order[i]] = true;
        }
        return mask;
    }

    // np.searchsorted(a, v, side='left'): a[i] ≥ v 인 첫 i (정렬된 a)
    private static int SearchSortedLeft(double[] a, double v)
    {
        int lo = 0;
        int hi = a.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (a[mid] < v)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>Parsons &amp; Sclater 1977 해양저 깊이 d [m] (ocean_depth_m).</summary>
    public static double OceanDepthM(double ageMyr, double ridgeDepthM)
    {
        double t = Math.Max(ageMyr, 0.0);
        double young = ridgeDepthM + (PsSqrtCoefM * Math.Sqrt(t));
        double old = PsDeepM - (PsDeepAmpM * Math.Exp(-t / PsTauMyr));
        return t < PsBreakMyr ? young : old;
    }

    /// <summary>대륙 지각 두께 H [m] → Airy 기준 고도 z [m] (airy_elevation_m).</summary>
    public static double[] AiryElevationM(double[] thicknessM, Config cfg)
    {
        double rhoM = cfg.F("crust.rho_mantle");
        double rhoC = cfg.F("crust.rho_crust");
        double rhoW = cfg.F("crust.rho_water");
        if (!(rhoM > NpMath.PyMax(rhoC, rhoW)))
        {
            throw new ArgumentException("맨틀 밀도는 지각·물 밀도보다 커야 합니다");
        }
        double platform = cfg.F("crust.continental_platform_m");
        double hc = cfg.F("crust.continental_thickness_m");
        double ratio = (rhoM - rhoC) / rhoM;
        double wet = rhoM / (rhoM - rhoW);
        double[] z = new double[thicknessM.Length];
        for (int c = 0; c < z.Length; c++)
        {
            double zAir = platform + ((thicknessM[c] - hc) * ratio);
            z[c] = zAir >= 0.0 ? zAir : zAir * wet;
        }
        return z;
    }

    /// <summary>해구 변위 [m] (≤ 0, trench_offset_m): 섭입판 쪽(side −1, kind 1·2)에서 −D·exp(−(δ/W)²).</summary>
    public static double[] TrenchOffsetM(double[] distConvergentM, sbyte[] subductionSide, byte[] convergenceKind, Config cfg)
    {
        double w = cfg.F("ocean.trench_width_m");
        double depth = cfg.F("ocean.trench_depth_m");
        double[] output = new double[distConvergentM.Length];
        for (int c = 0; c < output.Length; c++)
        {
            byte k = convergenceKind[c];
            if (subductionSide[c] == -1 && (k == Plates.KindOceanContinent || k == Plates.KindOceanOcean))
            {
                double x = distConvergentM[c] / w;
                output[c] = -depth * Math.Exp(-(x * x));
            }
        }
        return output;
    }

    /// <summary>region 칸 중 바깥 이웃이 있는 칸과 그 바깥 이웃까지 거리의 절반 [m] (_edge_cells).</summary>
    internal static (bool[] Edge, double[] Half) EdgeCells(int[] nbr, bool[] region, double[] dist)
    {
        const int nSlots = CellGraph.NSlots;
        int n = region.Length;
        bool[] edge = new bool[n];
        double[] half = new double[n];
        for (int c = 0; c < n; c++)
        {
            double mn = double.PositiveInfinity;
            for (int s = 0; s < nSlots; s++)
            {
                int v = nbr[(c * nSlots) + s];
                bool nbIn = v < 0 || region[v];
                bool cross = v >= 0 && nbIn != region[c];
                if (cross)
                {
                    edge[c] = true;
                    double d = dist[(c * nSlots) + s];
                    if (d < mn)
                    {
                        mn = d;
                    }
                }
            }
            half[c] = edge[c] ? 0.5 * mn : 0.0;
        }
        return (edge, half);
    }

    /// <summary>
    /// 지각 필드를 만듭니다 (generate_crust, docs/pipeline.md 4.2). 반환 fields: crust_type, crust_thickness_m,
    /// ocean_age_myr (대륙 NaN), z_platform_m. info: z_platform_base_m, trench_m, coast_signed_m.
    /// </summary>
    public static (FieldSet Fields, OrderedDictionary<string, object?> Info) GenerateCrust(
        CellGraph graph, FieldSet plateFields, bool[] continental, Config cfg)
    {
        Plates.CheckSphereGraph(graph);
        int n = graph.NCells;
        bool[] cont = Plates.CheckBoolMask(continental, n, "continental");
        foreach (string name in new[] { "dist_divergent_m", "spreading_m_per_yr", "dist_convergent_m", "convergence_kind", "subduction_side" })
        {
            if (!plateFields.Contains(name) || plateFields[name].Length != n)
            {
                throw new ArgumentException($"plate_fields 에 (N,) 모양의 '{name}' 이 있어야 합니다");
            }
        }
        double hCont = cfg.F("crust.continental_thickness_m");
        double hOcean = cfg.F("crust.oceanic_thickness_m");
        double margin = cfg.F("crust.margin_width_m");
        double cap = cfg.F("ocean.age_cap_myr");
        double[] dConv = plateFields.Get<double>("dist_convergent_m");
        byte[] kind = plateFields.Get<byte>("convergence_kind");
        sbyte[] side = plateFields.Get<sbyte>("subduction_side");

        // 해양저 나이 (설계도 5장 17번): 해령 거리 / 반확장 속도, 상한은 안전장치.
        double[] age = Plates.SeafloorAgeMyr(
            plateFields.Get<double>("dist_divergent_m"), plateFields.Get<double>("spreading_m_per_yr"), cap);
        for (int c = 0; c < n; c++)
        {
            if (cont[c])
            {
                age[c] = NpMath.NaN;
            }
        }

        double reach = NpMath.PyMax(margin, TransitionHalfWidthM) + (2.0 * graph.Spacing);
        double[] signed = new double[n];
        for (int c = 0; c < n; c++)
        {
            signed[c] = cont[c] ? double.PositiveInfinity : double.NegativeInfinity;
        }
        long[]? srcC = null;
        long[]? srcO = null;
        bool anyCont = Array.IndexOf(cont, true) >= 0;
        bool anyOcean = Array.IndexOf(cont, false) >= 0;
        if (anyCont && anyOcean)
        {
            bool[] notCont = Array.ConvertAll(cont, b => !b);
            (bool[] edgeC, double[] halfC) = EdgeCells(graph.Nbr, cont, graph.Dist);
            (bool[] edgeO, double[] halfO) = EdgeCells(graph.Nbr, notCont, graph.Dist);
            (double[] dC, long[] sC) = Distance.NearestSource(graph, edgeC, reach);
            (double[] dO, long[] sO) = Distance.NearestSource(graph, edgeO, reach);
            srcC = sC;
            srcO = sO;
            for (int c = 0; c < n; c++)
            {
                if (cont[c] && sC[c] >= 0)
                {
                    signed[c] = dC[c] + halfC[sC[c]];
                }
                else if (!cont[c] && sO[c] >= 0)
                {
                    signed[c] = -(dO[c] + halfO[sO[c]]);
                }
            }
        }

        // 지각 두께
        double collThick = cfg.F("crust.collision_thickening_m");
        double thickWidth = cfg.F("crust.thickening_width_m");
        double[] thick = new double[n];
        for (int c = 0; c < n; c++)
        {
            thick[c] = cont[c] ? hCont : hOcean;
            if (cont[c] && kind[c] == Plates.KindCollision)
            {
                double x = dConv[c] / thickWidth;
                thick[c] += collThick * Math.Exp(-(x * x));
            }
        }
        double hEdge = MarginEdgeFraction * hCont;
        for (int c = 0; c < n; c++)
        {
            if (cont[c] && signed[c] < margin)
            {
                thick[c] = hEdge + ((thick[c] - hEdge) * Smoothstep(signed[c] / margin));
            }
        }

        // 기준 고도: 대륙 Airy, 해양 Parsons & Sclater
        double[] zCont = AiryElevationM(thick, cfg);
        double ridge = cfg.F("ocean.ridge_depth_m");
        double[] zOcean = new double[n];
        double[] z = new double[n];
        for (int c = 0; c < n; c++)
        {
            zOcean[c] = -OceanDepthM(cont[c] ? 0.0 : age[c], ridge);
            z[c] = cont[c] ? zCont[c] : zOcean[c];
        }
        // 전이: 경계 양쪽 50 km 에서 smoothstep 으로 섞습니다. 반대쪽 값은 가장 가까운 가장자리 칸 값.
        if (srcC is not null && srcO is not null)
        {
            for (int c = 0; c < n; c++)
            {
                bool band = Math.Abs(signed[c]) < TransitionHalfWidthM;
                if (!band)
                {
                    continue;
                }
                double w = Smoothstep((signed[c] + TransitionHalfWidthM) / (2.0 * TransitionHalfWidthM));
                if (!cont[c] && srcC[c] >= 0)
                {
                    z[c] = (w * zCont[srcC[c]]) + ((1.0 - w) * zOcean[c]);
                }
                else if (cont[c] && srcO[c] >= 0)
                {
                    z[c] = (w * zCont[c]) + ((1.0 - w) * zOcean[srcO[c]]);
                }
            }
        }

        double[] trench = TrenchOffsetM(dConv, side, kind, cfg);
        byte[] crustType = new byte[n];
        double[] zPlatform = new double[n];
        for (int c = 0; c < n; c++)
        {
            crustType[c] = cont[c] ? (byte)1 : (byte)0;
            zPlatform[c] = z[c] + trench[c];
        }
        var fields = new FieldSet
        {
            ["crust_type"] = crustType,
            ["crust_thickness_m"] = thick,
            ["ocean_age_myr"] = age,
            ["z_platform_m"] = zPlatform,
        };
        var info = new OrderedDictionary<string, object?>
        {
            ["z_platform_base_m"] = z,
            ["trench_m"] = trench,
            ["coast_signed_m"] = signed,
        };
        return (fields, info);
    }
}
