using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 행성 단계의 식 하나짜리 함수 대조 (golden 사례 planet/functions): 바다 바닥 깊이, 지각평형 높이, 매끈한 계단,
/// 해구 깊이, 화산호까지 거리, 유출, 기온. 정답은 포팅 때 Python 생성기가 지구 설정(earth, tiny)으로 낸 값입니다.
/// 설정 파일의 지각 밀도·섭입 각도·기온 값을 바꾸면 이 시험이 달라졌다고 알려 줍니다.
/// </summary>
public sealed class PlanetFunctionTests
{
    private static readonly GoldenCase G = Golden.Load("planet", "functions");

    private static Config Cfg() => PlanetGolden.Cfg(G);

    [Fact]
    public void OceanDepth()
    {
        double ridge = G.Meta.GetProperty("ridge_depth_m").GetDouble();
        double[] ages = G.In("ages").AsDouble();
        double[] got = new double[ages.Length];
        for (int i = 0; i < ages.Length; i++)
        {
            got[i] = Crust.OceanDepthM(ages[i], ridge);
        }
        Compare.Bits(G.Out("ocean_depth").AsDouble(), got, "ocean_depth");
    }

    [Fact]
    public void AiryElevation() =>
        Compare.Bits(G.Out("airy").AsDouble(), Crust.AiryElevationM(G.In("thick").AsDouble(), Cfg()), "airy");

    [Fact]
    public void Smoothstep()
    {
        double[] x = G.In("sx").AsDouble();
        double[] got = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            got[i] = Crust.Smoothstep(x[i]);
        }
        Compare.Bits(G.Out("smoothstep").AsDouble(), got, "smoothstep");
    }

    [Fact]
    public void TrenchOffset() =>
        Compare.Bits(
            G.Out("trench").AsDouble(),
            Crust.TrenchOffsetM(G.In("d_conv").AsDouble(), G.In("side").AsSByte(), G.In("kind").AsByte(), Cfg()),
            "trench");

    [Fact]
    public void ArcDistance() =>
        Compare.Bits(G.Out("arc_distance_m").ScalarDouble(), Uplift.ArcDistanceM(Cfg()), "arc_distance_m");

    [Fact]
    public void BudykoRunoff() =>
        Compare.Bits(G.Out("runoff").AsDouble(), Climate.BudykoRunoff(G.In("p").AsDouble(), G.In("pet").AsDouble()), "runoff");

    [Fact]
    public void SurfaceTemperature()
    {
        double[] lat = G.In("lat").AsDouble();
        Compare.Bits(G.Out("temperature").AsDouble(), Climate.SurfaceTemperature(lat, G.In("z").AsDouble(), Cfg()), "temperature");
        Compare.Bits(G.Out("temperature_noz").AsDouble(), Climate.SurfaceTemperature(lat, null, Cfg()), "temperature_noz");
    }
}
