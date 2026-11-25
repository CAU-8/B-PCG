using System.Collections.Generic;
using System.Text.Json;
using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 행성 1단계(판 → 지각 → 해수면 → 융기 → 기후) 대조. 정답은 포팅 때 Python 생성기가 낸 값입니다
/// (만든 방법: git 기록의 csharp/golden/export_golden.py 의 export_planet).
/// 이 단계의 계산을 일부러 바꿨다면 정답 자료를 새로 만들 수 없으므로, 바뀐 필드를 확인한 뒤 그 사례의 시험을 고칩니다.
/// </summary>
public sealed class PlanetMaterialsTests
{
    // 거친 격자(면당 8칸, 16칸)에서 재료 전체를 만듭니다. 흔들기 없는 구면 격자입니다.
    [Theory]
    [MemberData(nameof(PlanetGolden.MaterialsCases), MemberType = typeof(PlanetGolden))]
    public void BuildMaterialsMatchesPython(string name)
    {
        GoldenCase g = Golden.Load("planet", name);
        Config cfg = PlanetGolden.Cfg(g);
        int n = g.Meta.GetProperty("n_per_face").GetInt32();
        CellGraph gr = Graph.SphereGraph(n, cfg.F("planet.radius_m"));

        Compare.Bits(g.Out("continent_score").AsDouble(), Crust.ContinentScore(gr, cfg), $"{name}.continent_score");
        Compare.Equal(g.Out("continental").AsBool(), Crust.ContinentMask(gr, cfg), $"{name}.continental");

        (FieldSet fields, OrderedDictionary<string, object?> info) = Materials.BuildMaterials(gr, cfg);
        PlanetGolden.AssertFields(g, "out.f.", fields, name);
        PlanetGolden.AssertInfoArrays(g, "out.info.", info, $"{name}.info");
        PlanetGolden.AssertInfoArrays(g, "out.plates_info.", (IReadOnlyDictionary<string, object?>)info["plates"]!, $"{name}.plates");
        foreach (string key in new[] { "sea_level_m", "ocean_fraction", "continental_fraction" })
        {
            Compare.Bits(g.Out(key).ScalarDouble(), System.Convert.ToDouble(info[key], System.Globalization.CultureInfo.InvariantCulture), $"{name}.{key}");
        }
    }

    // 거친 격자의 재료를 흔든 고운 격자(L0)로 옮깁니다.
    [Theory]
    [MemberData(nameof(PlanetGolden.TransferCases), MemberType = typeof(PlanetGolden))]
    public void TransferMaterialsMatchesPython(string name)
    {
        GoldenCase g = Golden.Load("planet", name);
        Config cfg = PlanetGolden.Cfg(g);
        JsonElement m = g.Meta;
        double r = cfg.F("planet.radius_m");
        CellGraph coarse = Graph.SphereGraph(m.GetProperty("n_coarse").GetInt32(), r);
        CellGraph fine = Graph.SphereGraph(m.GetProperty("n_fine").GetInt32(), r, m.GetProperty("jitter").GetDouble(), m.GetProperty("seed").GetInt64());
        FieldSet coarseFields = PlanetGolden.Fields(g, "in.cf.");
        Dictionary<string, object?>? coarseInfo = m.GetProperty("with_info").GetBoolean()
            ? new Dictionary<string, object?> { ["z_platform_base_m"] = g.In("coarse_z_platform_base_m").AsDouble() }
            : null;

        (FieldSet fields, OrderedDictionary<string, object?> info) = Materials.TransferMaterials(coarse, coarseFields, coarseInfo, fine, cfg);
        // dist_divergent_m 은 정답 자료를 만든 뒤 일부러 바꿨습니다. 경계 양쪽에 반대 부호를 붙여 보간해, 경계선 위에서 거리가
        // 0 에 닿게 했습니다 (docs/pipeline.md 4.1, Materials.DivergentDistanceToFine). 그래서 이 필드는 따로 봅니다.
        PlanetGolden.AssertFields(g, "out.f.", fields, name, skip: ["dist_divergent_m"]);
        AssertSignedDivergentDistance(g.Out("f.dist_divergent_m").AsDouble(), fields.GetFloat64("dist_divergent_m"),
            coarseFields.Get<byte>("boundary_type"), Cubesphere.CellOf(fine.Unit(), m.GetProperty("n_coarse").GetInt32()), name);
        PlanetGolden.AssertInfoArrays(g, "out.info.", info, $"{name}.info");
        foreach (string key in new[] { "sea_level_m", "ocean_fraction", "continental_fraction" })
        {
            Compare.Bits(g.Out(key).ScalarDouble(), System.Convert.ToDouble(info[key], System.Globalization.CultureInfo.InvariantCulture), $"{name}.{key}");
        }
    }

    /// <summary>
    /// 옛 방법(그대로 보간)의 정답과 지금 dist_divergent_m 을 비교합니다. 가장 가까운 거친 칸이 발산 경계 칸이 아닌 L0 칸은 비트까지
    /// 같아야 합니다. 발산 경계 칸 둘레의 L0 칸은 부호를 붙여 보간하므로 옛 값보다 크지 않고, 적어도 한 칸은 작아야 합니다.
    /// </summary>
    private static void AssertSignedDivergentDistance(double[] old, double[] now, byte[] coarseBoundary, long[] nearest, string name)
    {
        Assert.Equal(old.Length, now.Length);
        int nSmaller = 0;
        for (int f = 0; f < old.Length; f++)
        {
            if (coarseBoundary[nearest[f]] != Plates.Divergent)
            {
                Compare.Bits([old[f]], [now[f]], $"{name}.dist_divergent_m[{f}] (발산 경계 칸 둘레 밖)");
                continue;
            }
            Assert.True(now[f] >= 0.0 && now[f] <= old[f] * (1.0 + 1e-12),
                $"{name}.dist_divergent_m[{f}]: 부호 보간 값 {now[f]} 은 0 이상이고 옛 값 {old[f]} 이하여야 합니다");
            nSmaller += now[f] < old[f] ? 1 : 0;
        }
        Assert.True(nSmaller > 0, $"{name}.dist_divergent_m: 발산 경계 둘레에서 옛 값보다 작아진 칸이 없습니다");
    }

    // 해수면(물 부피를 낮은 곳부터 채움), 바다 칸, 기울인 자전축의 위도와 기후. 흔든 구면 격자(면당 8칸)입니다.
    [Fact]
    public void SeaLevelAndClimateOnASphereMatchPython()
    {
        GoldenCase g = Golden.Load("planet", "ocean_sphere8");
        JsonElement gm = g.Meta.GetProperty("graph");
        CellGraph s8 = Graph.SphereGraph(gm.GetProperty("n").GetInt32(), gm.GetProperty("R").GetDouble(), gm.GetProperty("jitter").GetDouble(), gm.GetProperty("seed").GetInt64());
        var axis = new List<object?>();
        foreach (JsonElement v in g.Meta.GetProperty("axis").EnumerateArray())
        {
            axis.Add(v.GetDouble());
        }
        Config cfg = Config.LoadConfig("earth", "tiny", [new("planet.axis", axis)]);
        double[] z = g.In("z").AsDouble();
        double volume = g.In("water_volume").ScalarDouble();
        double probe = g.In("h_probe").ScalarDouble();

        double h = Ocean.SeaLevel(z, s8.Area, volume);
        Compare.Bits(g.Out("sea_level").ScalarDouble(), h, "sea_level");
        Compare.Bits(g.Out("volume_at_h").ScalarDouble(), Ocean.OceanVolume(z, s8.Area, h), "volume_at_h");
        Compare.Bits(g.Out("volume_probe").ScalarDouble(), Ocean.OceanVolume(z, s8.Area, probe), "volume_probe");
        Compare.Equal(g.Out("ocean_mask").AsBool(), Ocean.OceanMask(s8, z, h), "ocean_mask");
        Compare.Equal(g.Out("ocean_mask_probe").AsBool(), Ocean.OceanMask(s8, z, probe), "ocean_mask_probe");
        Compare.Bits(g.Out("latitude").AsDouble(), Climate.LatitudeRad(s8, cfg), "latitude");

        double[] zLand = System.Array.ConvertAll(z, v => System.Math.Max(v, 0.0));
        PlanetGolden.AssertFields(g, "out.climate.", Climate.GenerateClimate(s8, cfg, z: zLand), "climate");
        PlanetGolden.AssertFields(g, "out.climate_seed.", Climate.GenerateClimate(s8, cfg, seed: g.Meta.GetProperty("climate_seed").GetInt64()), "climate_seed");
    }

    // 평면 격자(히어로 유역과 같은 꼴)에서 융기와 기후. 위도는 한 값(lat_deg)으로 줍니다.
    [Fact]
    public void UpliftAndClimateOnAFlatGridMatchPython()
    {
        GoldenCase g = Golden.Load("planet", "flat24");
        JsonElement gm = g.Meta.GetProperty("graph");
        JsonElement origin = g.Meta.GetProperty("origin");
        CellGraph fl = Graph.FlatGraph(
            gm.GetProperty("ny").GetInt32(), gm.GetProperty("nx").GetInt32(), gm.GetProperty("dx").GetDouble(),
            gm.GetProperty("jitter").GetDouble(), gm.GetProperty("seed").GetInt64(), (origin[0].GetDouble(), origin[1].GetDouble()));
        Config cfg = Config.LoadConfig("earth", "tiny");
        double lat = g.Meta.GetProperty("lat_deg").GetDouble();
        FieldSet ff = PlanetGolden.Fields(g, "in.ff.", PlanetGolden.Strings(g.Meta.GetProperty("field_order")));

        PlanetGolden.AssertFields(g, "out.uplift.", Uplift.GenerateUplift(fl, ff, cfg), "uplift");
        PlanetGolden.AssertFields(g, "out.climate.", Climate.GenerateClimate(fl, cfg, z: g.In("z").AsDouble(), latDeg: lat), "climate");
        Compare.Bits(g.Out("latitude").AsDouble(), Climate.LatitudeRad(fl, cfg, lat), "latitude");
    }
}
