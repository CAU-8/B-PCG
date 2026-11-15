using System;
using System.Linq;
using Bpcg.Core;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>동굴 통로 그물 (docs/pipeline.md 10장): 싱크홀에서 샘까지 물이 흐르는 길.</summary>
public sealed class CaveNetworkTests
{
    private const int N = 24;
    private const double Dx = 25.0;
    private const double RadiusMax = 3.0;
    private const double FullArea = 1.0e6;

    private static readonly CaveNetworkParams Params = new(
        RadiusMaxM: RadiusMax, FullSizeAreaM2: FullArea, SinkSpacingM: 0.0, SinkMinAreaM2: 1.0e4, PathWavelengthM: 200.0, Seed: 3);

    /// <summary>
    /// 동쪽으로 갈수록 물 높이가 1 km 에 2 m 씩 오르는 석회암 땅 하나. 서쪽 가장자리 가운데 칸이 샘이고,
    /// 지표는 물 높이보다 60 m 위입니다. sinks 의 칸에만 빗물이 모입니다 (넓이 areaM2).
    /// </summary>
    private static (CellGraph Graph, double[] Z, double[] Levels, bool[] Outlet, bool[] Allowed, double[] Area) World(
        int[] sinks, double areaM2, double jitter = 1.0)
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: jitter, seed: 11);
        int n = N * N;
        double[] z = new double[n];
        double[] levels = new double[n];
        bool[] outlet = new bool[n];
        bool[] allowed = new bool[n];
        double[] area = new double[n];
        for (int c = 0; c < n; c++)
        {
            levels[c] = 100.0 + (0.002 * (((c % N) + 0.5) * Dx));
            z[c] = levels[c] + 60.0;
            allowed[c] = true;
        }
        int spring = ((N / 2) * N) + 0;
        outlet[spring] = true;
        allowed[spring] = false;
        foreach (int s in sinks)
        {
            area[s] = areaM2;
        }
        return (g, z, levels, outlet, allowed, area);
    }

    private static int Cell(int row, int col) => (row * N) + col;

    [Fact]
    public void EverySinkDrainsToTheSpringWithoutLoopsAndNeverUphill()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        Assert.Equal(sinks.OrderBy(v => v), net.Sinks);
        int spring = Cell(N / 2, 0);
        foreach (int s in sinks)
        {
            int c = s;
            int steps = 0;
            while (net.Parent[c] >= 0)
            {
                int next = net.Parent[c];
                // 아래층은 물 높이가 낮아지는 쪽으로만 흐릅니다.
                Assert.True(levels[next] <= levels[c] + 1e-6, $"칸 {c} → {next} 에서 물 높이가 오릅니다");
                Assert.True(net.Flow[next] >= net.Flow[c], "하류로 갈수록 모인 넓이가 줄면 안 됩니다");
                c = next;
                Assert.True(++steps <= N * N, "길이 샘에 닿지 않고 돕니다");
            }
            Assert.Equal(spring, c);
        }
        // 샘으로 나오는 물은 세 싱크홀에 모인 넓이의 합입니다.
        Assert.Equal(3 * 2.0e5, net.Flow[spring], 6);
    }

    [Fact]
    public void PassagesGetThickerDownstreamAndShaftsReachFromTheSurfaceToTheLevel()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        int nShaft = net.SegIsShaft.Count(b => b);
        Assert.Equal(sinks.Length, nShaft);
        for (int e = 0; e < net.NSegments; e++)
        {
            if (!net.SegIsShaft[e])
            {
                continue;
            }
            // 수직굴: 지표(물 높이 + 60 m) 위에서 시작해 물 높이까지 곧게 내려갑니다.
            Assert.Equal(net.SegA[e * 3], net.SegB[e * 3]);
            Assert.Equal(net.SegA[(e * 3) + 1], net.SegB[(e * 3) + 1]);
            Assert.True(net.SegA[(e * 3) + 2] - net.SegB[(e * 3) + 2] > 60.0);
        }
        // 층 통로 조각은 칸 번호 순서로 놓이고, 반지름은 그 칸을 지나는 물이 모인 넓이로 정해집니다.
        double[] expected = Enumerable.Range(0, N * N)
            .Where(c => net.Flow[c] > 0.0 && net.Parent[c] >= 0)
            .Select(c => CaveNetwork.Radius(net.Flow[c], RadiusMax, FullArea))
            .ToArray();
        double[] actual = Enumerable.Range(0, net.NSegments).Where(e => !net.SegIsShaft[e]).Select(e => net.SegRadius[e]).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(sinks.Length + expected.Length, net.NSegments);
        // 넓이 0.2 km² 는 3 × √0.2 ≈ 1.34 m 라 아래 한계 2 m, 0.6 km² 는 3 × √0.6 ≈ 2.32 m, 1 km² 이상은 3 m 입니다.
        Assert.Equal(CaveNetwork.RadiusMinM, CaveNetwork.Radius(2.0e5, RadiusMax, FullArea));
        Assert.Equal(RadiusMax * Math.Sqrt(0.6), CaveNetwork.Radius(6.0e5, RadiusMax, FullArea), 12);
        Assert.Equal(RadiusMax, CaveNetwork.Radius(5.0e6, RadiusMax, FullArea));
    }

    [Fact]
    public void SameInputsGiveTheSameNetwork()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18), Cell(8, 9)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 3.0e5);
        CaveNetwork a = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        CaveNetwork b = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        Assert.Equal(a.SegA, b.SegA);
        Assert.Equal(a.SegB, b.SegB);
        Assert.Equal(a.SegRadius, b.SegRadius);
        Assert.Equal(a.Parent, b.Parent);
    }

    [Fact]
    public void NoSinkWhereTheLevelIsShallowOrTheWaterCannotReachASpring()
    {
        int shallow = Cell(5, 15);
        int cutOff = Cell(18, 20);
        int good = Cell(12, 22);
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) =
            World([shallow, cutOff, good], 2.0e5, jitter: 0.0);
        // 얕은 칸: 지표에서 동굴 층까지 5 m 라 SinkMinDepthM(10 m) 에 못 미칩니다.
        z[shallow] = levels[shallow] + 5.0;
        // 끊긴 칸: 둘레 여덟 칸에 동굴 층이 없어 샘까지 길이 닿지 않습니다.
        foreach (int dr in new[] { -1, 0, 1 })
        {
            foreach (int dc in new[] { -1, 0, 1 })
            {
                if (dr != 0 || dc != 0)
                {
                    levels[cutOff + (dr * N) + dc] = double.NaN;
                }
            }
        }
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        Assert.Equal([good], net.Sinks);
    }

    [Fact]
    public void ThinnedSinksAreFartherApartThanTheSpacing()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 11);
        var all = Enumerable.Range(0, N * N).ToList();
        const double spacing = 120.0;
        int[] kept = CaveNetwork.ThinSinks(all, g.Pos, spacing, seed: 5);
        Assert.NotEmpty(kept);
        Assert.True(kept.Length < all.Count / 4);
        for (int i = 0; i < kept.Length; i++)
        {
            for (int j = i + 1; j < kept.Length; j++)
            {
                double ex = g.Pos[kept[i] * 3] - g.Pos[kept[j] * 3];
                double ey = g.Pos[(kept[i] * 3) + 1] - g.Pos[(kept[j] * 3) + 1];
                Assert.True((ex * ex) + (ey * ey) > spacing * spacing, $"싱크홀 {kept[i]} 과 {kept[j]} 이 {spacing} m 안에 있습니다");
            }
        }
    }

    [Fact]
    public void LakeChambersSitOnFlowingPassagesAndKeepTheirSpacing()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        const double spacing = 150.0;
        const double chamber = 6.0;
        CaveNetwork plain = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        CaveNetwork net = CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params with { LakeSpacingM = spacing, ChamberRadiusM = chamber });
        // 방을 두지 않으면 방 조각이 없고, 방을 두어도 원래 통로와 수직굴은 그대로입니다.
        Assert.Empty(plain.Lakes);
        Assert.DoesNotContain(true, plain.SegIsChamber);
        Assert.Equal(plain.SegA, net.SegA.Take(plain.SegA.Length));
        Assert.Equal(plain.SegRadius, net.SegRadius.Take(plain.NSegments));
        Assert.NotEmpty(net.Lakes);
        int spring = Cell(N / 2, 0);
        int nHalves = 0;
        for (int i = 0; i < net.Lakes.Length; i++)
        {
            int c = net.Lakes[i];
            // 방은 위에서 물이 들어오고 아래로 나가는 통로 칸에 놓입니다. 싱크홀 칸과 샘 칸에는 놓지 않습니다.
            Assert.True(net.Flow[c] > 0.0 && net.Parent[c] >= 0);
            Assert.DoesNotContain(c, sinks);
            Assert.NotEqual(spring, c);
            int nUp = Enumerable.Range(0, N * N).Count(u => net.Parent[u] == c && net.Flow[u] > 0.0);
            Assert.True(nUp >= 1, $"방 칸 {c} 로 들어오는 통로가 없습니다");
            nHalves += 1 + nUp;
            // 물 높이는 그 칸의 아래층 높이 그대로입니다.
            Assert.Equal(levels[c], net.LakeXyz[(i * 3) + 2]);
            for (int j = i + 1; j < net.Lakes.Length; j++)
            {
                int d = net.Lakes[j];
                double ex = g.Pos[c * 3] - g.Pos[d * 3];
                double ey = g.Pos[(c * 3) + 1] - g.Pos[(d * 3) + 1];
                Assert.True((ex * ex) + (ey * ey) > spacing * spacing, $"방 {c} 와 {d} 가 {spacing} m 안에 있습니다");
            }
        }
        // 방 조각은 방 칸에서 이웃 통로 칸까지의 절반씩입니다: 방마다 하류 쪽 1개 + 상류 쪽 통로 수.
        int[] rooms = Enumerable.Range(0, net.NSegments).Where(e => net.SegIsChamber[e]).ToArray();
        Assert.Equal(nHalves, rooms.Length);
        Assert.Equal(Enumerable.Range(plain.NSegments, rooms.Length), rooms);
        foreach (int e in rooms)
        {
            Assert.Equal(chamber, net.SegRadius[e]);
            Assert.False(net.SegIsShaft[e]);
            // 방의 가운데는 물 높이보다 반지름의 0.2 배(1.2 m) 아래입니다. 조각의 첫 끝이 방 칸입니다.
            int i = Enumerable.Range(0, net.Lakes.Length).Single(
                k => net.SegA[e * 3] == net.LakeXyz[k * 3] && net.SegA[(e * 3) + 1] == net.LakeXyz[(k * 3) + 1]);
            Assert.Equal(net.LakeXyz[(i * 3) + 2] + (CaveNetwork.ChamberLift * chamber), net.SegA[(e * 3) + 2], 9);
        }
    }

    [Fact]
    public void NoLakeChamberWhereTheCoverIsThinOrThePassageIsAsWide()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        // 지표가 물 높이보다 60 m 위입니다. 방 반지름 25 m 는 그 3 배인 75 m 가 필요해 놓을 자리가 없습니다.
        Assert.Empty(CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params with { LakeSpacingM = 150.0, ChamberRadiusM = 25.0 }).Lakes);
        // 방 반지름 2 m 는 통로(2 m 이상)보다 굵지 않아 놓지 않습니다.
        Assert.Empty(CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params with { LakeSpacingM = 150.0, ChamberRadiusM = 2.0 }).Lakes);
        ArgumentException e = Assert.Throws<ArgumentException>(() => CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params with { LakeSpacingM = -1.0, ChamberRadiusM = 6.0 }));
        Assert.Contains("-1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongLengthsAreRejectedWithTheReceivedLengths()
    {
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World([Cell(12, 22)], 2.0e5);
        ArgumentException e = Assert.Throws<ArgumentException>(
            () => CaveNetwork.Build(g, z, levels, new bool[7], allowed, area, Params));
        Assert.Contains("7", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CaveNetwork.Build(g, new double[3], levels, outlet, allowed, area, Params));
        Assert.Throws<ArgumentException>(
            () => CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params with { FullSizeAreaM2 = 0.0 }));
    }
}
