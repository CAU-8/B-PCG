using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hydro;

namespace Bpcg.Landscape;

/// <summary>[lakes] 설정 값 (LakeSettings).</summary>
public sealed record LakeSettings(
    double MinDepthM, double MinAreaM2, double HollowDepthM, double HollowWavelengthM, double HollowFraction, double HollowAgeYr, long Seed);

/// <summary>
/// 호수가 생길 우묵한 자리 (docs/pipeline.md 7.5). 솔버가 만든 지형은 모든 칸이 아랫칸보다 높아서 물이 고일 자리가 없습니다.
/// 행성 지도에서는 땅 몇 군데를 넓고 얕게 낮춰(빙하가 긁었거나 땅이 가라앉은 자리) 그 자리를 만들고,
/// 물이 실제로 고이는 칸만 낮춘 채로 둡니다. 그리고 가장 깊은 곳이 MinDepthM 에 못 미치거나 넓이가 MinAreaM2 에 못 미치는
/// 작은 웅덩이는 흙으로 메웁니다.
/// 호수 자체는 뒤의 물 단계(Water.WaterBodies)가 우묵한 자리를 넘칠 높이까지 채워서 정합니다.
/// </summary>
public static class Hollows
{
    /// <summary>낮출 깊이를 정하는 노이즈의 해시 용도 번호.</summary>
    public const long StreamHollow = 7121;

    /// <summary>노이즈를 겹치는 횟수. 큰 덩어리 위에 절반·4분의 1 크기의 굴곡을 얹습니다.</summary>
    public const int NoiseOctaves = 3;

    /// <summary>낮추는 땅 가운데 노이즈 값이 가장 큰 이 몫(10분의 1)은 HollowDepthM 까지 다 내려갑니다.</summary>
    public const double FullDepthShare = 0.1;

    /// <summary>물이 고였다고 보는 깊이의 아래 한계 [m]. Water.LakeMinDepthM 과 같은 값입니다.</summary>
    public const double PondedM = 1e-3;

    /// <summary>낮춘 바닥이 바다(높이 0 m)보다 이만큼은 높게 남게 합니다 [m]. 바다보다 낮은 땅은 바다에 잠긴 땅이 되기 때문입니다.</summary>
    public const double BedAboveSeaM = 1.0;

    /// <summary>설정 [lakes] 를 읽어 검사합니다. 절이 없으면(이 절이 생기기 전의 설정) null.</summary>
    public static LakeSettings? Settings(Config cfg)
    {
        if (!cfg.Contains("lakes"))
        {
            return null;
        }
        double minDepth = cfg.F("lakes.min_depth_m");
        double minArea = cfg.F("lakes.min_area_m2");
        double depth = cfg.F("lakes.hollow_depth_m");
        double wavelength = cfg.F("lakes.hollow_wavelength_m");
        double fraction = cfg.F("lakes.hollow_fraction");
        double age = cfg.F("lakes.hollow_age_yr");
        if (!(minDepth >= 0.0 && minArea >= 0.0 && depth >= 0.0 && wavelength > 0.0 && fraction >= 0.0 && fraction <= 1.0 && age >= 0.0))
        {
            throw new ArgumentException(
                "lakes 는 min_depth_m ≥ 0, min_area_m2 ≥ 0, hollow_depth_m ≥ 0, hollow_wavelength_m > 0, 0 ≤ hollow_fraction ≤ 1, "
                + $"hollow_age_yr ≥ 0 이어야 합니다. 받은 값: min_depth_m {minDepth}, min_area_m2 {minArea}, hollow_depth_m {depth}, "
                + $"hollow_wavelength_m {wavelength}, hollow_fraction {fraction}, hollow_age_yr {age}");
        }
        return new LakeSettings(minDepth, minArea, depth, wavelength, fraction, age, cfg.I("planet.seed"));
    }

    /// <summary>
    /// 칸마다 낮출 깊이 [m] (출구·바다 칸은 0). 노이즈 값이 큰 쪽부터 땅의 HollowFraction 만큼을 고르고, 그 안에서
    /// 노이즈가 클수록 깊게(가장 깊은 곳 HollowDepthM) 낮춥니다. 그다음 강이 실어 온 흙이 HollowAgeYr 동안 쌓인 두께
    /// (흙 흐름 × 햇수 ÷ 칸 넓이)를 뺍니다. 흙을 많이 나르는 큰 강 위의 자리는 다 메워져 0 이 됩니다.
    /// noise 는 칸마다의 노이즈 값 (N,) 입니다.
    /// </summary>
    public static double[] Depth(double[] noise, bool[] isOutlet, double[] sedimentFlux, double[] cellArea, LakeSettings st)
    {
        int n = noise.Length;
        if (isOutlet.Length != n || sedimentFlux.Length != n || cellArea.Length != n)
        {
            throw new ArgumentException(
                $"noise, isOutlet, sedimentFlux, cellArea 는 같은 길이여야 합니다. 받은 길이: {n}, {isOutlet.Length}, {sedimentFlux.Length}, {cellArea.Length}");
        }
        double[] depth = new double[n];
        var land = new List<double>();
        for (int c = 0; c < n; c++)
        {
            if (!isOutlet[c])
            {
                land.Add(noise[c]);
            }
        }
        int nLand = land.Count;
        int nLow = (int)Math.Floor(st.HollowFraction * nLand);
        if (nLand == 0 || nLow == 0 || st.HollowDepthM <= 0.0)
        {
            return depth;
        }
        land.Sort();
        // 낮추기 시작하는 노이즈 값(edge)과 최대 깊이에 닿는 노이즈 값(full).
        double edge = land[nLand - nLow];
        int nFull = Math.Max((int)Math.Floor(FullDepthShare * nLow), 1);
        double full = land[nLand - nFull];
        double span = full - edge;
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c] || noise[c] < edge)
            {
                continue;
            }
            double t = span > 0.0 ? Math.Min((noise[c] - edge) / span, 1.0) : 1.0;
            double infill = st.HollowAgeYr * Math.Max(sedimentFlux[c], 0.0) / cellArea[c];
            depth[c] = Math.Max((st.HollowDepthM * t) - infill, 0.0);
        }
        return depth;
    }

    /// <summary>
    /// 행성 지도에 우묵한 자리를 만듭니다 (add_hollows). 낮춘 땅에서 물이 고이는 칸(넘칠 높이가 땅보다 높은 칸)만 낮춘 채로 두고,
    /// 나머지는 원래 높이로 되돌립니다. 그래서 호수가 되지 않는 땅은 솔버 결과 그대로입니다.
    /// 평면 그래프(걷는 유역)에는 만들지 않습니다: 파장이 유역보다 커서 유역 전체가 함께 내려갈 뿐입니다.
    /// 반환: (새 고도, 낮춘 채로 둔 칸).
    /// </summary>
    public static (double[] ZNew, bool[] Hollow) AddHollows(
        CellGraph graph, double[] z, bool[] isOutlet, double[] sedimentFlux, Config cfg)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n || sedimentFlux.Length != n)
        {
            throw new ArgumentException(
                $"z, isOutlet, sedimentFlux 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, {isOutlet.Length}, {sedimentFlux.Length}");
        }
        double[] zNew = (double[])z.Clone();
        bool[] hollow = new bool[n];
        LakeSettings? st = Settings(cfg);
        if (st is null || graph.Kind != "sphere" || st.HollowDepthM <= 0.0 || st.HollowFraction <= 0.0)
        {
            return (zNew, hollow);
        }
        long seed = (long)(Hashing.Hash3(st.Seed, StreamHollow, 0) >> 1);
        double[] noise = Noise.Fbm3(graph.Unit(), seed, NoiseOctaves, frequency: graph.R!.Value / st.HollowWavelengthM);
        double[] depth = Depth(noise, isOutlet, sedimentFlux, graph.Area, st);
        double[] zLow = new double[n];
        for (int c = 0; c < n; c++)
        {
            // 바닥이 바다 높이 아래로 내려가지 않게 깊이를 줄입니다 (바닷가의 낮은 땅).
            zLow[c] = z[c] - Math.Min(depth[c], Math.Max(z[c] - BedAboveSeaM, 0.0));
        }
        double[] filled = Depressions.FillDepressions(zLow, graph.Nbr, isOutlet);
        for (int c = 0; c < n; c++)
        {
            if (!isOutlet[c] && filled[c] - zLow[c] > PondedM)
            {
                hollow[c] = true;
                zNew[c] = zLow[c];
            }
        }
        return (zNew, hollow);
    }

    /// <summary>
    /// 작은 웅덩이를 흙으로 메웁니다 (fill_shallow). 웅덩이 하나(서로 이웃한 고인 칸의 묶음)에서 가장 깊은 곳이 minDepthM 에
    /// 못 미치거나 넓이가 minAreaM2 에 못 미치면 그 칸들을 넘칠 높이까지 올립니다. 깊고 넓은 웅덩이는 그대로 두어 호수가 됩니다.
    /// 반환: (새 고도, 메운 칸, 메운 웅덩이 수, 남긴 웅덩이 수).
    /// </summary>
    public static (double[] ZNew, bool[] Filled, int NFilled, int NKept) FillShallow(
        CellGraph graph, double[] z, bool[] isOutlet, double minDepthM, double minAreaM2 = 0.0)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n)
        {
            throw new ArgumentException($"z, isOutlet 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, {isOutlet.Length}");
        }
        double[] zNew = (double[])z.Clone();
        bool[] filledMask = new bool[n];
        double[] zHat = Depressions.FillDepressions(z, graph.Nbr, isOutlet);
        bool[] ponded = new bool[n];
        for (int c = 0; c < n; c++)
        {
            ponded[c] = !isOutlet[c] && zHat[c] - z[c] > PondedM;
        }
        bool[] seen = new bool[n];
        var stack = new Stack<int>();
        var group = new List<int>();
        int nFilled = 0;
        int nKept = 0;
        int[] nbr = graph.Nbr;
        for (int c0 = 0; c0 < n; c0++)
        {
            if (!ponded[c0] || seen[c0])
            {
                continue;
            }
            group.Clear();
            double deepest = 0.0;
            double area = 0.0;
            seen[c0] = true;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                group.Add(c);
                deepest = Math.Max(deepest, zHat[c] - z[c]);
                area += graph.Area[c];
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && ponded[v] && !seen[v])
                    {
                        seen[v] = true;
                        stack.Push(v);
                    }
                }
            }
            if (deepest >= minDepthM && area >= minAreaM2)
            {
                nKept++;
                continue;
            }
            nFilled++;
            foreach (int c in group)
            {
                zNew[c] = zHat[c];
                filledMask[c] = true;
            }
        }
        return (zNew, filledMask, nFilled, nKept);
    }
}
