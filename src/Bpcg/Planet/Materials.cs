using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>
/// 1단계 재료 묶음: 거친 격자에서 사슬 1과 기후를 만들고 더 고운 격자(L0)로 옮깁니다 (src/bpcg/planet/materials.py).
/// </summary>
/// <remarks>
/// build_materials: continent_mask → plates → crust → ocean(해수면, 바다) → uplift → climate.
/// transfer_materials: 거친 격자 → 고운 격자 (docs/pipeline.md 1·4장). 옮기는 규칙은 Python 모듈 docstring 과 같고, 발산 경계
/// 거리만 다릅니다: 수렴 경계 거리처럼 경계 양쪽에 반대 부호를 붙여 옮깁니다 (<see cref="DivergentDistanceToFine"/>).
/// info 의 seconds 는 걸린 시간이라 대조하지 않습니다.
/// </remarks>
public static class Materials
{
    public static readonly string[] CategoricalFields =
        ["plate_id", "boundary_type", "convergence_kind", "subduction_side", "crust_type"];

    public static readonly string[] LinearFields =
        ["convergence_m_per_yr", "spreading_m_per_yr", "crust_thickness_m", "ocean_age_myr"];

    /// <summary>
    /// 그대로 보간하지 않는 필드. 두 경계 거리는 경계 양쪽에 반대 부호를 붙여 보간하고(<see cref="DivergentDistanceToFine"/>,
    /// 수렴 경계는 TransferMaterials 본문), 나머지는 L0 에서 다시 계산합니다.
    /// </summary>
    public static readonly string[] RecomputedFields =
    [
        "dist_convergent_m", "dist_divergent_m", "z_platform_m", "is_ocean", "uplift_m_per_yr", "exhumation_m", "dist_arc_m",
        "precip_m_per_yr", "temperature_c", "pet_m_per_yr", "runoff_m_per_yr", "runoff_eff_m_per_yr",
    ];

    /// <summary>부호 거리 보간을 믿는 범위: 거친 칸 2개 안.</summary>
    public const double SignedDistRangeCells = 2.0;

    // 해수면: 설정에 ocean.target_fraction(바다가 덮는 비율)이 있으면 그 비율로, 없으면 ocean.water_volume_m3(물의 부피)로 정합니다.
    private static (double H, bool[] Ocean) SeaLevelAndMask(CellGraph graph, double[] zPlatform, Config cfg)
    {
        double h = UsesOceanFraction(cfg)
            ? Ocean.SeaLevelForFraction(graph, zPlatform, cfg.F("ocean.target_fraction"))
            : Ocean.SeaLevel(zPlatform, graph.Area, cfg.F("ocean.water_volume_m3"));
        return (h, Ocean.OceanMask(graph, zPlatform, h));
    }

    /// <summary>설정이 해수면을 바다가 덮는 비율(ocean.target_fraction)로 정하는지. 거짓이면 물의 부피로 정합니다.</summary>
    public static bool UsesOceanFraction(Config cfg)
    {
        Section ocean = cfg.Sec("ocean");
        return ocean.Contains("target_fraction") && ocean.Get("target_fraction") is not null;
    }

    // graph.area[mask].sum() / graph.area.sum() (둘 다 numpy pairwise 합)
    internal static double AreaFraction(CellGraph graph, bool[] mask)
    {
        var sel = new List<double>();
        for (int c = 0; c < mask.Length; c++)
        {
            if (mask[c])
            {
                sel.Add(graph.Area[c]);
            }
        }
        return NpReduce.Sum(sel.ToArray()) / NpReduce.Sum(graph.Area);
    }

    private static double[] Bathymetry(double[] z, double h)
    {
        double[] o = new double[z.Length];
        for (int c = 0; c < o.Length; c++)
        {
            o[c] = z[c] - h;
        }
        return o;
    }

    private static double[] PositivePart(double[] x) => Array.ConvertAll(x, v => Math.Max(v, 0.0));

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>
    /// 한 구면 그래프(보통 거친 격자)에서 1단계 재료(사슬 1 + 기후)를 만듭니다 (build_materials).
    /// info: sea_level_m, ocean_fraction, continental_fraction, bathymetry_m, z_platform_base_m, trench_m,
    /// coast_signed_m, plates, seconds.
    /// </summary>
    public static (FieldSet Fields, OrderedDictionary<string, object?> Info) BuildMaterials(CellGraph graph, Config cfg)
    {
        Plates.CheckSphereGraph(graph);
        var sec = new OrderedDictionary<string, object?>();
        long t0 = Stopwatch.GetTimestamp();
        bool[] continental = Crust.ContinentMask(graph, cfg);
        sec["continent_mask"] = Seconds(t0);

        long t = Stopwatch.GetTimestamp();
        (FieldSet plateFields, OrderedDictionary<string, object?> plateInfo) = Plates.GeneratePlates(graph, continental, cfg);
        sec["plates"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        (FieldSet crustFields, OrderedDictionary<string, object?> crustInfo) =
            Crust.GenerateCrust(graph, plateFields, continental, cfg);
        sec["crust"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        double[] zPlatform = crustFields.Get<double>("z_platform_m");
        (double h, bool[] isOcean) = SeaLevelAndMask(graph, zPlatform, cfg);
        double[] bathymetry = Bathymetry(zPlatform, h);
        sec["ocean"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        FieldSet fields = plateFields.Copy();
        fields.Update(crustFields);
        fields["is_ocean"] = isOcean;
        fields.Update(Uplift.GenerateUplift(graph, fields, cfg));
        sec["uplift"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        fields.Update(Climate.GenerateClimate(graph, cfg, z: PositivePart(bathymetry)));
        sec["climate"] = Seconds(t);
        sec["total"] = Seconds(t0);
        Fields.CheckFields(fields.Names);

        var info = new OrderedDictionary<string, object?>
        {
            ["sea_level_m"] = h,
            ["ocean_fraction"] = AreaFraction(graph, isOcean),
            ["continental_fraction"] = AreaFraction(graph, continental),
            ["bathymetry_m"] = bathymetry,
            ["z_platform_base_m"] = crustInfo["z_platform_base_m"],
            ["trench_m"] = crustInfo["trench_m"],
            ["coast_signed_m"] = crustInfo["coast_signed_m"],
            ["plates"] = plateInfo,
            ["seconds"] = sec,
        };
        return (fields, info);
    }

    /// <summary>inf 를 big 으로 잠시 바꿔 선형 보간하고, 가장 가까운 거친 칸이 inf 인 칸은 inf 로 되돌립니다 (_finite_resample).</summary>
    internal static double[] FiniteResample(double[] values, int nSrc, CellGraph fine, double big, long[] nearestIdx)
    {
        bool[] finite = Array.ConvertAll(values, double.IsFinite);
        double[] v = new double[values.Length];
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = finite[c] ? values[c] : big;
        }
        double[] output = Resample.ResampleSphere(v, nSrc, fine);
        for (int c = 0; c < output.Length; c++)
        {
            if (!finite[nearestIdx[c]])
            {
                output[c] = double.PositiveInfinity;
            }
        }
        return output;
    }

    /// <summary>
    /// 발산 경계까지 거리 dist_divergent_m 을 고운 격자(L0)로 옮깁니다. 경계선 위에서 거리가 0 이 되도록, 경계 양쪽 칸에 반대 부호를
    /// 붙여 보간합니다. nearestIdx 는 L0 칸마다 가장 가까운 거친 칸 번호입니다.
    /// </summary>
    /// <remarks>
    /// 거친 격자의 발산 경계 칸은 경계선에서 반 칸 떨어져 있어서, 거리 값이 반 칸입니다(laptop 약 28~39 km). 경계 양쪽 칸이 모두
    /// 이 값이라 그대로 보간하면 두 칸 사이의 경계선 위에서도 반 칸이 나옵니다. 그러면 열곡 축(docs/pipeline.md 4.4)이 설정만큼
    /// 가라앉지 않습니다. 한쪽을 음수로 두면 보간한 값이 두 칸 사이에서 0 을 지나고, 그 절댓값이 경계선까지 거리가 됩니다.
    /// 예: 78 km 떨어진 두 경계 칸의 값이 +39 km 와 −39 km 이면, 한쪽에서 19.5 km 떨어진 L0 칸은 |39 − 78 × 0.25| = 19.5 km 입니다.
    /// 부호는 경계 칸마다 건너편 판(<see cref="Plates.AcrossPlate"/>)의 번호가 제 판보다 크면 +, 작으면 − 입니다. 그래서 판 P 와 Q 사이
    /// 경계의 양쪽 칸은 늘 반대 부호입니다. 경계가 아닌 칸은 같은 판에서 가장 가까운 발산 경계 칸의 부호를 받습니다
    /// (dist_divergent_m 을 잰 방법과 같음). 같은 판에 발산 경계가 없는 칸은 + 입니다.
    /// 부호를 붙여 보간한 값은 가장 가까운 거친 칸이 발산 경계 칸인 L0 칸에만 씁니다. 다른 곳에서는 경계가 없어도 부호가 바뀌는
    /// 자리가 있기 때문입니다. 판 하나가 번호가 더 큰 판, 더 작은 판과 모두 발산 경계를 이루면, 그 판 안에서 가장 가까운 경계가
    /// 바뀌는 선을 사이에 두고 부호가 뒤집힙니다. 같은 두 판 사이 경계라도 발산이 아닌 구간(변환·수렴)에서는 양쪽 부호가 반대입니다.
    /// 이 자리의 거친 칸은 발산 경계 칸이 아니라 걸러집니다. 걸러진 L0 칸은 그대로 보간한 거리를 씁니다. 발산 경계 칸에서 한 칸 넘게
    /// 떨어진 곳은 거리가 칸을 건널 때마다 고르게 늘어서, 그대로 보간해도 맞습니다.
    /// </remarks>
    internal static double[] DivergentDistanceToFine(
        CellGraph coarseGraph, FieldSet coarseFields, CellGraph fineGraph, double big, long[] nearestIdx)
    {
        int nSrc = (int)coarseGraph.Shape[1];
        int n = coarseGraph.NCells;
        double[] dist = coarseFields.GetFloat64("dist_divergent_m");
        double[] output = FiniteResample(dist, nSrc, fineGraph, big, nearestIdx);
        int[] plate = coarseFields.Get<int>("plate_id");
        bool[] isDiv = Array.ConvertAll(coarseFields.Get<byte>("boundary_type"), b => b == Plates.Divergent);
        if (Array.IndexOf(isDiv, true) < 0)
        {
            return output;
        }
        int[] across = Plates.AcrossPlate(coarseGraph.Nbr, plate);
        double[] sideAtBoundary = new double[n];
        for (int c = 0; c < n; c++)
        {
            sideAtBoundary[c] = across[c] > plate[c] ? 1.0 : -1.0;
        }
        (_, _, double[] side) = Distance.NearestSourceValues(Plates.SamePlateGraph(coarseGraph, plate), isDiv, sideAtBoundary);
        double[] signedC = new double[n];
        for (int c = 0; c < n; c++)
        {
            double d = double.IsFinite(dist[c]) ? dist[c] : big;
            signedC[c] = side[c] < 0.0 ? -d : d; // 같은 판에 발산 경계가 없으면 side 가 NaN 이라 + 입니다.
        }
        double[] signedF = Resample.ResampleSphere(signedC, nSrc, fineGraph);
        for (int f = 0; f < output.Length; f++)
        {
            if (isDiv[nearestIdx[f]] && double.IsFinite(output[f]))
            {
                output[f] = Math.Abs(signedF[f]);
            }
        }
        return output;
    }

    /// <summary>
    /// 거친 격자 재료를 고운 구면 그래프(L0)로 옮기고, 좁은 것과 해수면·기후는 다시 계산합니다 (transfer_materials).
    /// coarseInfo 가 null 이거나 z_platform_base_m 이 없으면 z_platform_m − 해구로 다시 만듭니다.
    /// info: sea_level_m, ocean_fraction, continental_fraction, bathymetry_m, z_platform_base_m, trench_m, seconds.
    /// </summary>
    public static (FieldSet Fields, OrderedDictionary<string, object?> Info) TransferMaterials(
        CellGraph coarseGraph, FieldSet coarseFields, IReadOnlyDictionary<string, object?>? coarseInfo,
        CellGraph fineGraph, Config cfg)
    {
        Plates.CheckSphereGraph(coarseGraph);
        Plates.CheckSphereGraph(fineGraph);
        int nSrc = (int)coarseGraph.Shape[1];
        if (coarseGraph.NCells != 6 * nSrc * nSrc)
        {
            throw new ArgumentException("coarse_graph 는 큐브스피어 구면 그래프여야 합니다");
        }
        var need = new List<string>(CategoricalFields);
        need.AddRange(LinearFields);
        need.Add("dist_convergent_m");
        need.Add("dist_divergent_m");
        need.Add("z_platform_m");
        foreach (string name in need)
        {
            if (!coarseFields.Contains(name) || coarseFields[name].Length != coarseGraph.NCells)
            {
                throw new ArgumentException($"coarse_fields 에 거친 격자 모양의 '{name}' 이 있어야 합니다");
            }
        }
        long t0 = Stopwatch.GetTimestamp();
        double big = Math.PI * coarseGraph.R!.Value;
        double hCoarse = coarseGraph.Spacing;
        int nf = fineGraph.NCells;
        var output = new FieldSet();
        // 범주 값은 가장 가까운 거친 칸 (resample_sphere(..., "nearest") 와 같고, 칸 찾기를 한 번만 함).
        long[] idx = Cubesphere.CellOf(fineGraph.Unit(), nSrc);

        foreach (string name in CategoricalFields)
        {
            output[name] = FieldSet.Take(coarseFields[name], idx);
        }
        foreach (string name in new[] { "convergence_m_per_yr", "spreading_m_per_yr", "crust_thickness_m" })
        {
            output[name] = Resample.ResampleSphere(coarseFields.GetFloat64(name), nSrc, fineGraph);
        }
        output["dist_divergent_m"] = DivergentDistanceToFine(coarseGraph, coarseFields, fineGraph, big, idx);

        // 해양저 나이: 대륙 NaN 을 가장 가까운 해양 값으로 채워 보간 → 고운 격자 대륙 칸은 NaN.
        double[] ageC = coarseFields.GetFloat64("ocean_age_myr");
        byte[] crustC = (byte[])coarseFields["crust_type"];
        bool[] isOc = Array.ConvertAll(crustC, v => v == 0);
        double[] ageF;
        if (Array.IndexOf(isOc, true) >= 0)
        {
            (_, _, double[] filled) = Distance.NearestSourceValues(coarseGraph, isOc, Array.ConvertAll(ageC, NpMath.NanToNum));
            for (int c = 0; c < filled.Length; c++)
            {
                if (!double.IsFinite(filled[c]))
                {
                    filled[c] = 0.0;
                }
            }
            ageF = Resample.ResampleSphere(filled, nSrc, fineGraph);
            double cap = cfg.F("ocean.age_cap_myr");
            for (int c = 0; c < nf; c++)
            {
                ageF[c] = NpMath.Clip(ageF[c], 0.0, cap);
            }
        }
        else
        {
            ageF = new double[nf];
            Array.Fill(ageF, NpMath.NaN);
        }
        byte[] crustF = (byte[])output["crust_type"];
        for (int c = 0; c < nf; c++)
        {
            if (crustF[c] == 1)
            {
                ageF[c] = NpMath.NaN;
            }
        }
        output["ocean_age_myr"] = ageF;

        // 수렴 경계 거리: 섭입판 쪽을 음수로 둔 부호 거리를 보간하면 경계선(0)이 거친 칸보다 곱게 잡힙니다.
        sbyte[] sideC = (sbyte[])coarseFields["subduction_side"];
        double[] dConvC = coarseFields.GetFloat64("dist_convergent_m");
        int ncc = dConvC.Length;
        bool[] finiteC = Array.ConvertAll(dConvC, double.IsFinite);
        double[] dAbsC = new double[ncc];
        double[] sC = new double[ncc];
        for (int c = 0; c < ncc; c++)
        {
            dAbsC[c] = finiteC[c] ? dConvC[c] : big;
            sC[c] = sideC[c] == -1 ? -dAbsC[c] : dAbsC[c];
        }
        double[] sF = Resample.ResampleSphere(sC, nSrc, fineGraph);
        double[] absF = Resample.ResampleSphere(dAbsC, nSrc, fineGraph);
        byte[] kindF = (byte[])output["convergence_kind"];
        sbyte[] sideF = (sbyte[])((sbyte[])output["subduction_side"]).Clone();
        double[] dConvF = new double[nf];
        double nearLimit = SignedDistRangeCells * hCoarse;
        for (int c = 0; c < nf; c++)
        {
            bool near = absF[c] < nearLimit;
            dConvF[c] = near ? Math.Abs(sF[c]) : absF[c];
            if (!finiteC[idx[c]])
            {
                dConvF[c] = double.PositiveInfinity;
            }
            bool subduct = kindF[c] == Plates.KindOceanContinent || kindF[c] == Plates.KindOceanOcean;
            if (near && subduct && sF[c] != 0.0)
            {
                sideF[c] = sF[c] < 0.0 ? (sbyte)-1 : (sbyte)1;
            }
        }
        output["subduction_side"] = sideF;
        output["dist_convergent_m"] = dConvF;

        // 기준 고도 = 보간한 해구 뺀 값 + 고운 격자에서 다시 계산한 해구.
        double[] baseC;
        if (coarseInfo is not null && coarseInfo.TryGetValue("z_platform_base_m", out object? b) && b is not null)
        {
            baseC = FieldSet.ToFloat64((Array)b);
        }
        else
        {
            double[] zc = coarseFields.GetFloat64("z_platform_m");
            double[] tc = Crust.TrenchOffsetM(dConvC, sideC, (byte[])coarseFields["convergence_kind"], cfg);
            baseC = new double[ncc];
            for (int c = 0; c < ncc; c++)
            {
                baseC[c] = zc[c] - tc[c];
            }
        }
        double[] baseF = Resample.ResampleSphere(baseC, nSrc, fineGraph);
        double[] trenchF = Crust.TrenchOffsetM(dConvF, sideF, kindF, cfg);
        double[] zPlatformF = new double[nf];
        for (int c = 0; c < nf; c++)
        {
            zPlatformF[c] = baseF[c] + trenchF[c];
        }
        output["z_platform_m"] = zPlatformF;
        double tResample = Seconds(t0);

        long t = Stopwatch.GetTimestamp();
        (double h, bool[] isOcean) = SeaLevelAndMask(fineGraph, zPlatformF, cfg);
        output["is_ocean"] = isOcean;
        double[] bathymetry = Bathymetry(zPlatformF, h);
        double tOcean = Seconds(t);

        t = Stopwatch.GetTimestamp();
        output.Update(Uplift.GenerateUplift(fineGraph, output, cfg));
        output.Update(Climate.GenerateClimate(fineGraph, cfg, z: PositivePart(bathymetry)));
        double tRest = Seconds(t);
        Fields.CheckFields(output.Names);

        var info = new OrderedDictionary<string, object?>
        {
            ["sea_level_m"] = h,
            ["ocean_fraction"] = AreaFraction(fineGraph, isOcean),
            ["continental_fraction"] = AreaFraction(fineGraph, Array.ConvertAll(crustF, v => v == 1)),
            ["bathymetry_m"] = bathymetry,
            ["z_platform_base_m"] = baseF,
            ["trench_m"] = trenchF,
            ["seconds"] = new OrderedDictionary<string, object?>
            {
                ["resample"] = tResample,
                ["ocean"] = tOcean,
                ["uplift_climate"] = tRest,
                ["total"] = Seconds(t0),
            },
        };
        return (output, info);
    }
}
