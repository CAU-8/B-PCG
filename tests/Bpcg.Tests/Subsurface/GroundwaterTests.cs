using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>지하수면 어림식 (docs/pipeline.md 8.3): 함양 경사 계수, 지하수 언덕 풀이, 물길 배수, 석회암의 평평한 지하수면.</summary>
public sealed class GroundwaterTests
{
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.019, 1.0)]
    [InlineData(0.02, 0.95)]
    [InlineData(0.1, 0.75)]
    [InlineData(0.44, 0.30)]
    [InlineData(0.45, 0.15)]
    [InlineData(0.8, 0.15)]
    public void RechargeSlopeFactorFollowsWaterGapClasses(double slope, double expected)
    {
        Assert.Equal(expected, Groundwater.RechargeSlopeFactor(slope));
    }

    [Fact]
    public void MoundHeightSolvesTheBalanceAndSeepsWhenTooLittleFlows()
    {
        // 화강암(약 2.46 m/yr, 1/e 길이 100 m), 흙 가까운 층 310 m/yr × 5 m, 물길 위 200 m 비탈.
        const double kRock = 2.46;
        const double rockM = 100.0;
        const double kSurf = 310.0;
        const double f = 5.0;
        const double top = 200.0;
        double rx = 0.1 * 200.0 * 200.0;
        double h = Groundwater.MoundHeight(rx, top, kRock, rockM, kSurf, f);
        double depth = top - h;
        double t = (kRock * rockM * Math.Exp(-depth / rockM)) + (kSurf * f * Math.Exp(-depth / f));
        Assert.InRange(h, 0.0, top);
        Assert.True(Math.Abs((2.0 * h * t) - rx) < 1e-6 * rx, $"2·H·T = {2.0 * h * t}, R·X = {rx}");
        // 빗물이 많을수록 언덕이 높습니다.
        Assert.True(Groundwater.MoundHeight(4.0 * rx, top, kRock, rockM, kSurf, f) > h);
        // 물이 거의 안 흐르는 땅에서는 지표까지 차서 스며 나옵니다.
        Assert.Equal(top, Groundwater.MoundHeight(rx, top, 1e-9, 0.0, 1e-9, f));
        Assert.Equal(0.0, Groundwater.MoundHeight(0.0, top, kRock, rockM, kSurf, f));
    }

    // 1 줄 × nx 칸 띠: 서쪽 끝 칸이 강(수면 0 m), 동쪽으로 갈수록 경사 slope 로 높아집니다.
    private static (CellGraph Graph, double[] Z, double[] Level, bool[] Water, double[] Slope, double[] Area) Strip(
        int nx, double dx, double slope)
    {
        CellGraph g = Graph.FlatGraph(1, nx, dx);
        double[] z = new double[nx];
        double[] level = new double[nx];
        bool[] water = new bool[nx];
        double[] s = new double[nx];
        double[] area = new double[nx];
        for (int i = 0; i < nx; i++)
        {
            z[i] = slope * dx * i;
            level[i] = double.NaN;
            s[i] = slope;
            area[i] = dx * dx;
        }
        water[0] = true;
        level[0] = 0.0;
        return (g, z, level, water, s, area);
    }

    private static LayerColumns Uniform(int n, byte rockId)
    {
        double[] bottom = new double[n];
        byte[] rock = new byte[n * 2];
        for (int c = 0; c < n; c++)
        {
            bottom[c] = -1.0e4;
            rock[c * 2] = rockId;
            rock[(c * 2) + 1] = rockId;
        }
        return LayerColumns.FromColumns(bottom, rock, 1);
    }

    private static byte[] Fill(int n, byte v)
    {
        byte[] a = new byte[n];
        Array.Fill(a, v);
        return a;
    }

    // 지표에서 깊이 top 까지 첫 층, 그 아래 둘째 층 (둘째 층 바닥은 아주 깊음).
    private static LayerColumns TwoLayers(int n, byte upper, double upperBottom, byte lower)
    {
        double[] bottom = new double[n * 2];
        byte[] rock = new byte[n * 3];
        for (int c = 0; c < n; c++)
        {
            bottom[c * 2] = upperBottom;
            bottom[(c * 2) + 1] = -1.0e4;
            rock[c * 3] = upper;
            rock[(c * 3) + 1] = lower;
            rock[(c * 3) + 2] = lower;
        }
        return LayerColumns.FromColumns(bottom, rock, 2);
    }

    [Fact]
    public void KarstWaterTableIsFlatAtRiverLevelWithoutDepthCap()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        (CellGraph g, double[] z, double[] level, bool[] water, double[] s, double[] area) = Strip(80, 25.0, 0.3);
        int n = z.Length;
        (double[] zGw, double[] lower, double[] baseLevel, _) = Groundwater.WaterTable(
            g, z, level, water, Fill(n, Rocks.Limestone), s, [1.0], area, Uniform(n, Rocks.Limestone), cfg);
        double grad = cfg.F("groundwater.karst_gradient");
        for (int c = 1; c < n; c++)
        {
            Assert.Equal(0.0, baseLevel[c]);
            double expected = grad * 25.0 * c;
            Assert.True(Math.Abs(zGw[c] - expected) < 1e-9, $"칸 {c}: z_gw {zGw[c]}, 기대 {expected}");
            Assert.Equal(zGw[c], lower[c]);
        }
        // 최대 깊이(300 m)로 자르지 않습니다: 끝 칸은 지표보다 약 590 m 낮습니다.
        Assert.True(z[n - 1] - zGw[n - 1] > cfg.F("groundwater.max_depth_m"));
    }

    [Fact]
    public void ShaleCapHoldsPerchedWaterAboveDrainedLimestone()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        (CellGraph g, double[] z, double[] level, bool[] water, double[] s, double[] area) = Strip(40, 25.0, 0.3);
        int n = z.Length;
        // 지표에서 5 m 고도까지 셰일(막는 층), 그 아래 석회암: 석회암은 강 높이까지 물을 빼지만 셰일 위 물은 남습니다.
        LayerColumns cols = TwoLayers(n, Rocks.Shale, 5.0, Rocks.Limestone);
        (double[] upper, double[] lower, _, OrderedDictionary<string, object?> diag) = Groundwater.WaterTable(
            g, z, level, water, Fill(n, Rocks.Shale), s, [1.0], area, cols, cfg);
        double grad = cfg.F("groundwater.karst_gradient");
        for (int c = 2; c < n; c++)
        {
            Assert.True(Math.Abs(lower[c] - (grad * 25.0 * c)) < 1e-9, $"칸 {c}: 아래 {lower[c]}");
            Assert.True(upper[c] > lower[c] + 1.0 && upper[c] <= z[c] + 1e-9, $"칸 {c}: 위 {upper[c]}, 아래 {lower[c]}, 지표 {z[c]}");
        }
        Assert.True((double)diag["perched_fraction"]! > 0.5);
    }

    [Fact]
    public void ChannelDrainsOnlyWhereWaterTableReachesItsBed()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        double drainArea = cfg.F("groundwater.drain_area_m2");
        (CellGraph g, double[] z, double[] level, bool[] water, double[] s, double[] area) = Strip(40, 25.0, 0.3);
        int n = z.Length;
        area[20] = 2.0 * drainArea; // 가운데 칸은 물길
        // 물이 거의 안 빠지는 셰일에 비가 아주 많으면(유출 5 m/yr) 지하수면이 물길 바닥에 닿아, 물길이 물 빠지는 곳이 됩니다.
        (double[] wet, _, _, OrderedDictionary<string, object?> wetDiag) = Groundwater.WaterTable(
            g, z, level, water, Fill(n, Rocks.Shale), s, [5.0], area, Uniform(n, Rocks.Shale), cfg);
        Assert.Equal(z[20], wet[20]);
        Assert.True((long)wetDiag["drain_passes_used"]! >= 1);
        // 비가 적은 화강암에서는 지하수면이 물길 바닥까지 오지 않아 물길이 물을 받지 않습니다.
        (double[] dry, _, _, _) = Groundwater.WaterTable(
            g, z, level, water, Fill(n, Rocks.Granite), s, [1e-3], area, Uniform(n, Rocks.Granite), cfg);
        Assert.True(dry[20] < z[20] - 1.0, $"z {z[20]}, z_gw {dry[20]}");
        for (int c = 1; c < n; c++)
        {
            Assert.True(dry[c] <= z[c] + 1e-9 && dry[c] >= 0.0, $"칸 {c}: z_gw {dry[c]}");
        }
    }

    [Fact]
    public void ErrorNamesReceivedValues()
    {
        Config cfg = Config.LoadConfig("earth", "tiny")
            .WithOverrides(new List<KeyValuePair<string, object?>> { new("groundwater.karst_gradient", -1.0) });
        (CellGraph g, double[] z, double[] level, bool[] water, double[] s, double[] area) = Strip(4, 25.0, 0.3);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Groundwater.WaterTable(
            g, z, level, water, Fill(4, Rocks.Granite), s, [1.0], area, Uniform(4, Rocks.Granite), cfg));
        Assert.Contains("karst_gradient -1", e.Message);
    }
}
