using System;
using Bpcg.Core;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>
/// 산사태 둑을 놓는 문턱 (docs/pipeline.md 7.4, 설정 [dams]). 문턱을 바꾸면 둑을 놓을 수 있는 물길 칸(후보)과 둑의 기대 수가
/// 설정 주석이 말하는 쪽으로 움직이는지 봅니다. 땅은 Valley 입니다: 서쪽으로 흐르는 V 자 골짜기 하나, 물길 칸 41개.
/// 서쪽 끝 칸은 출구이고 그 옆 칸은 물이 바로 출구로 가므로, 둑을 놓을 수 있는 물길 칸은 2~40열의 39칸입니다.
/// </summary>
public sealed partial class DamsTests
{
    private static long Candidates(DamResult d) => (long)d.Diag["n_candidates"]!;

    private static double Expected(DamResult d) => (double)d.Diag["expected_dams"]!;

    [Fact]
    public void ADamNeedsASteepSideSlopeAndEnoughUpstreamArea()
    {
        (CellGraph g, SolverResult steep, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(steep);
        // 옆 비탈 경사 ÷ 무너지는 경사(0.8)가 min_slope_ratio 이상인 물길에만 산사태가 납니다. 옆 비탈 0.8 은 비율 1.0 입니다.
        Assert.Equal(39L, Candidates(Dams.Apply(g, steep.Z, steep, u, run, outlet, Cfg(("dams.min_slope_ratio", 0.95)))));
        DamResult none = Dams.Apply(g, steep.Z, steep, u, run, outlet, Cfg(("dams.min_slope_ratio", 1.05)));
        Assert.Equal(0L, Candidates(none));
        Assert.Empty(none.Sites);
        // 옆 비탈 0.3 은 비율 0.375 입니다. 문턱을 0.35 로 낮추면 이 완만한 골짜기에도 둑이 생기고, 0.4 면 생기지 않습니다.
        (_, SolverResult gentle, _, _) = Valley(sideSlope: 0.3, sedimentM3PerYr: 0.0);
        DamResult low = Dams.Apply(g, gentle.Z, gentle, u, run, Outlet(gentle), Cfg(("dams.min_slope_ratio", 0.35)));
        Assert.Equal(39L, Candidates(low));
        Assert.NotEmpty(low.Sites);
        Assert.Equal(0L, Candidates(Dams.Apply(g, gentle.Z, gentle, u, run, Outlet(gentle), Cfg(("dams.min_slope_ratio", 0.4)))));

        // 상류 넓이가 min_drainage_area_m2 이상인 칸만 물길로 봅니다. 강의 상류 넓이는 동쪽 끝 40열에서 5.025 km² 이고, 서쪽으로
        // 한 칸 갈 때마다 0.025 km² 씩 늘어 2열에서 5.975 km² 입니다. 문턱 5.5 km² 면 2~21열의 20칸만 후보이고, 6 km² 면 후보가 없습니다.
        DamResult down = Dams.Apply(g, steep.Z, steep, u, run, outlet, Cfg(("dams.min_drainage_area_m2", 5.5e6)));
        Assert.Equal(20L, Candidates(down));
        Assert.All(down.Sites, o => Assert.True((long)Site(o)["cell"]! % N <= 21));
        Assert.Equal(0L, Candidates(Dams.Apply(g, steep.Z, steep, u, run, outlet, Cfg(("dams.min_drainage_area_m2", 6.0e6)))));
    }

    [Fact]
    public void DamHeightsStayInTheSetRangeAndLowDamsAreTheCommonOnes()
    {
        (CellGraph g, SolverResult r, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(r);
        foreach ((double lo, double hi) in new[] { (10.0, 150.0), (20.0, 60.0), (30.0, 40.0) })
        {
            DamResult d = Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.min_height_m", lo), ("dams.max_height_m", hi)));
            Assert.NotEmpty(d.Sites);
            Assert.All(d.Sites, o => Assert.InRange((double)Site(o)["height_m"]!, lo, hi));
        }
        // 작은 산사태가 흔하므로 낮은 둑이 흔합니다. 다루는 가장 낮은 높이를 올리면 둑의 기대 수가 줄고, 가장 높은 높이를 낮춰도 줄어듭니다.
        double Exp(double lo, double hi) =>
            Expected(Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.min_height_m", lo), ("dams.max_height_m", hi))));
        double all = Exp(10.0, 150.0);
        Assert.True(Exp(20.0, 150.0) < all && Exp(40.0, 150.0) < Exp(20.0, 150.0));
        Assert.True(Exp(10.0, 40.0) < all);
    }

    [Fact]
    public void ExpectedDamsScaleWithBlockingFractionAndPersistenceAndGrowWithSteeperDamFaces()
    {
        (CellGraph g, SolverResult r, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(r);
        DamResult Run((string Key, object Value)[] set) => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(set));
        // 지금 남아 있을 둑의 기대 수 = 한 해에 생기는 수 × 남을 확률 × 남는 햇수 × 막는 몫. 막는 몫과 남는 햇수에 정확히 비례합니다.
        double e = Expected(Run([("dams.blocking_fraction", 1.0), ("dams.persistence_yr", 5000.0)]));
        Assert.True(e > 0.0);
        Assert.Equal(0.5, Expected(Run([("dams.blocking_fraction", 0.5), ("dams.persistence_yr", 5000.0)])) / e, 12);
        Assert.Equal(0.5, Expected(Run([("dams.blocking_fraction", 1.0), ("dams.persistence_yr", 2500.0)])) / e, 12);
        Assert.Equal(4.0, Expected(Run([("dams.blocking_fraction", 1.0), ("dams.persistence_yr", 20000.0)])) / e, 12);
        // 둑의 나이는 0 ~ 남는 햇수 사이입니다.
        DamResult young = Run([("dams.persistence_yr", 2500.0)]);
        Assert.NotEmpty(young.Sites);
        Assert.All(young.Sites, o => Assert.InRange((double)Site(o)["age_yr"]!, 0.0, 2500.0));
        // 둑 비탈이 가파를수록 같은 높이에 돌이 덜 들어 둑이 흔해집니다 (설정 주석: 줄이면 둑이 드물어짐).
        double gentleFace = Expected(Run([("dams.slope", 0.08)]));
        double steepFace = Expected(Run([("dams.slope", 0.3)]));
        Assert.True(gentleFace < e && e < steepFace, $"둑 비탈 0.08, 0.15, 0.3 의 기대 수가 {gentleFace}, {e}, {steepFace} 입니다");
    }

    [Fact]
    public void ThresholdsOutsideTheirRangeAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, SolverResult r, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(r);
        ArgumentException e = Assert.Throws<ArgumentException>(
            () => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.min_slope_ratio", -0.1))));
        Assert.Contains("min_slope_ratio -0.1", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.min_drainage_area_m2", 0.0))));
        Assert.Contains("min_drainage_area_m2 0", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.fill_slope_ratio", 1.0))));
        Assert.Contains("fill_slope_ratio 1", e.Message, StringComparison.Ordinal);
    }
}
