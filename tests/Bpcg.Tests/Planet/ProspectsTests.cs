using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>광상 가능성 값(원래 광맥, 사금)의 시험.</summary>
public sealed class ProspectsTests
{
    /// <summary>
    /// 여섯 칸이 한 줄로 이어진 강: 5 → 4 → 3 → 2 → 1 → 0(출구). 칸 넓이는 모두 100 m² 이고, 위쪽 세 칸은 경사 0.1, 그 아래로 0.02, 0.01, 0.01 입니다.
    /// </summary>
    private static (long[] Rcv, double[] Area, double[] Drainage, double[] Slope) Chain()
    {
        long[] rcv = [0, 0, 1, 2, 3, 4];
        double[] area = Enumerable.Repeat(100.0, 6).ToArray();
        double[] drainage = [600.0, 500.0, 400.0, 300.0, 200.0, 100.0];
        double[] slope = [0.01, 0.01, 0.02, 0.1, 0.1, 0.1];
        return (rcv, area, drainage, slope);
    }

    [Fact]
    public void 사금은_상류에_광맥이_있고_경사가_줄어드는_강_칸에_생긴다()
    {
        (long[] rcv, double[] area, double[] drainage, double[] slope) = Chain();
        bool[] river = [false, true, true, true, true, true];
        bool[] fan = new bool[6];
        double[] alluvium = new double[6];
        double[] lode = [0.0, 0.0, 0.0, 0.0, 0.0, 1.0];

        double[] placer = Prospects.Placer(rcv, area, drainage, slope, river, fan, alluvium, lode, gain: 5.0);
        // 공급은 맨 위 칸의 광맥(값 1 × 100 m²)을 상류 면적으로 나눈 값에 5 를 곱해 1 에서 자릅니다: 칸 2 는 100 ÷ 400 × 5 = 1.25 → 1, 칸 1 은 100 ÷ 500 × 5 = 1.
        // 덫은 위쪽 칸보다 경사가 줄어든 비율입니다: 칸 2 는 1 − 0.02 ÷ 0.1 = 0.8, 칸 1 은 1 − 0.01 ÷ 0.02 = 0.5.
        Assert.Equal(0.8, placer[2], 9);
        Assert.Equal(0.5, placer[1], 9);
        // 경사가 줄지 않는 칸(3, 4), 위쪽 칸이 없는 칸(5), 강이 아닌 칸(0)은 0 입니다.
        Assert.Equal(0.0, placer[3]);
        Assert.Equal(0.0, placer[4]);
        Assert.Equal(0.0, placer[5]);
        Assert.Equal(0.0, placer[0]);

        // 충적층이 쌓인 강 칸은 덫이 적어도 0.7, 선상지 칸은 강이 아니어도 덫이 1 입니다.
        alluvium[3] = 2.0;
        fan[0] = true;
        placer = Prospects.Placer(rcv, area, drainage, slope, river, fan, alluvium, lode, gain: 5.0);
        Assert.Equal(0.7, placer[3], 9);
        Assert.Equal(100.0 / 600.0 * 5.0, placer[0], 9);
    }

    [Fact]
    public void 상류에_광맥이_없으면_사금_값은_정확히_0이다()
    {
        (long[] rcv, double[] area, double[] drainage, double[] slope) = Chain();
        bool[] river = Enumerable.Repeat(true, 6).ToArray();
        bool[] fan = Enumerable.Repeat(true, 6).ToArray();
        double[] alluvium = Enumerable.Repeat(3.0, 6).ToArray();
        Assert.All(Prospects.Placer(rcv, area, drainage, slope, river, fan, alluvium, new double[6], gain: 5.0), v => Assert.Equal(0.0, v));

        // 광맥이 아래쪽 칸(1)에만 있으면, 그보다 위쪽 칸(2~5)은 덫이 좋아도 사금이 없습니다.
        double[] lode = [0.0, 1.0, 0.0, 0.0, 0.0, 0.0];
        double[] placer = Prospects.Placer(rcv, area, drainage, slope, river, fan, alluvium, lode, gain: 5.0);
        Assert.All(new[] { 2, 3, 4, 5 }, c => Assert.Equal(0.0, placer[c]));
        Assert.True(placer[1] > 0.0 && placer[0] > 0.0);
    }

    [Fact]
    public void 원래_광맥은_위쪽_판의_육지에서_화산호_띠_한가운데가_가장_높다()
    {
        CellGraph graph = Graph.SphereGraph(16, 1_000.0);
        int n = graph.NCells;
        sbyte[] side = Enumerable.Repeat((sbyte)1, n).ToArray();
        bool[] ocean = new bool[n];
        // 수렴 경계까지의 거리를 칸의 z 좌표로 흉내 냅니다(0~2,000 m). 화산호 띠의 한가운데는 경계에서 600 m, 띠 너비의 절반은 200 m 입니다.
        double[] dist = Enumerable.Range(0, n).Select(c => graph.Pos[(3 * c) + 2] + 1_000.0).ToArray();
        double[] arc = Enumerable.Repeat(600.0, n).ToArray();

        double[] all = Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 1.0, seed: 3);
        for (int c = 0; c < n; c++)
        {
            double x = (dist[c] - 600.0) / 200.0;
            Assert.Equal(Math.Exp(-(x * x)), all[c], 12);
        }
        Assert.True(all.Max() > 0.95);

        // 바다, 파고드는 쪽 판, 화산호 거리를 모르는 칸은 0 입니다.
        ocean[0] = true;
        side[1] = -1;
        arc[2] = double.NaN;
        double[] masked = Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 1.0, seed: 3);
        Assert.Equal(0.0, masked[0]);
        Assert.Equal(0.0, masked[1]);
        Assert.Equal(0.0, masked[2]);

        // 상자의 비율이 0 이면 광맥이 없고, 0.5 면 상자의 절반쯤에만 있습니다. 같은 시드면 같은 상자가 남습니다.
        Assert.All(Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 0.0, seed: 3), v => Assert.Equal(0.0, v));
        double[] half = Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 0.5, seed: 3);
        double kept = half.Count(v => v > 0.0) / (double)masked.Count(v => v > 0.0);
        Assert.InRange(kept, 0.2, 0.8);
        Assert.Equal(half, Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 0.5, seed: 3));
        Assert.NotEqual(half, Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, clusterFraction: 0.5, seed: 4));

        ArgumentException e = Assert.Throws<ArgumentException>(() => Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 0.0, 0.5, 3));
        Assert.Contains("minerals.cluster_m(0 m)", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Prospects.Lode(graph, side, dist, arc, ocean, 200.0, 500.0, 1.5, 3));
    }

    [Fact]
    public void 작은_행성의_사금_칸은_모두_강이나_선상지이고_상류에_광맥이_있다()
    {
        Config cfg = Config.LoadConfig("small10", "tiny").WithOverrides(
        [
            new KeyValuePair<string, object?>("profile.grid.coarse_n_per_face", 32L),
            new KeyValuePair<string, object?>("profile.grid.l0_n_per_face", 128L),
        ]);
        PlanetState planet = Pipeline.GeneratePlanet(cfg, null);
        double[] lode = planet.Fields.GetFloat64("prospect_lode");
        double[] placer = planet.Fields.GetFloat64("prospect_placer");
        bool[] ocean = planet.Fields.Get<bool>("is_ocean");
        bool[] river = planet.Fields.Get<bool>("is_river");
        bool[] fan = planet.Fields.Get<bool>("fan");
        long[] rcv = planet.Fields.Get<long>("receiver");
        Assert.All(lode, v => Assert.InRange(v, 0.0, 1.0));
        Assert.All(placer, v => Assert.InRange(v, 0.0, 1.0));
        Assert.True(lode.Count(v => v > 0.0) > 100, "작은 행성에 원래 광맥 칸이 100개도 없습니다.");
        Assert.DoesNotContain(Enumerable.Range(0, lode.Length), c => ocean[c] && lode[c] > 0.0);

        // 칸마다 '상류에 광맥 칸이 몇 개 있는지'를 물길을 따라 세어, 사금 칸의 상류에 광맥이 있는지 따로 확인합니다.
        double[] lodeCells = lode.Select(v => v > 0.0 ? 1.0 : 0.0).ToArray();
        double[] upstream = AccumulateModule.Accumulate(rcv, Routing.TopoOrder(rcv), lodeCells);
        int placers = 0;
        for (int c = 0; c < placer.Length; c++)
        {
            if (placer[c] > 0.0)
            {
                placers++;
                Assert.True(river[c] || fan[c], $"칸 {c} 는 강도 선상지도 아닌데 사금 값이 {placer[c]} 입니다.");
                Assert.True(upstream[c] > 0.0, $"칸 {c} 는 상류에 광맥 칸이 없는데 사금 값이 {placer[c]} 입니다.");
            }
        }
        Assert.True(placers > 0, "작은 행성에 사금 칸이 하나도 없습니다.");

        // 광맥이 있는 상자의 비율을 0 으로 두면 광맥도 사금도 없습니다.
        PlanetState barren = Pipeline.GeneratePlanet(cfg.WithOverrides([new KeyValuePair<string, object?>("minerals.cluster_fraction", 0.0)]), null);
        Assert.All(barren.Fields.GetFloat64("prospect_lode"), v => Assert.Equal(0.0, v));
        Assert.All(barren.Fields.GetFloat64("prospect_placer"), v => Assert.Equal(0.0, v));
    }
}
