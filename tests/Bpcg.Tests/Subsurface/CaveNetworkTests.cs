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

    private static readonly CaveNetworkParams Params = new(
        RadiusMaxM: RadiusMax, SinkSpacingM: 0.0, SinkMinAreaM2: 1.0e4, PathWavelengthM: 200.0, Seed: 3);

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
                Assert.True(net.Flow[next] >= net.Flow[c], "하류로 갈수록 물이 줄면 안 됩니다");
                c = next;
                Assert.True(++steps <= N * N, "길이 샘에 닿지 않고 돕니다");
            }
            Assert.Equal(spring, c);
        }
        // 유량을 주지 않으면 넓이 × 1 m/yr 로 봅니다. 샘으로 나오는 물은 세 싱크홀의 합이고, 샘은 하나입니다.
        Assert.Equal(3 * 2.0e5, net.Flow[spring], 6);
        Assert.Equal(1, net.NSprings);
        Assert.Equal([2.0e5, 2.0e5, 2.0e5], net.SinkDischarge);
    }

    [Fact]
    public void LaterSinksJoinTheConduitThatBrokeThroughFirst()
    {
        // 큰 싱크홀(물 5 × 10⁵)이 먼저 샘까지 통로를 뚫습니다. 작은 싱크홀은 샘까지 따로 가지 않고 그 통로에 붙습니다.
        int big = Cell(12, 22);
        int small = Cell(9, 15);
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World([big, small], 1.0e5, jitter: 0.0);
        area[big] = 5.0e5;
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        // 작은 싱크홀의 길을 따라가다 처음으로 두 물이 합쳐지는 칸.
        int c = small;
        int steps = 0;
        while (net.Flow[c] < 6.0e5 - 1.0)
        {
            c = net.Parent[c];
            steps++;
            Assert.True(c >= 0, "작은 싱크홀의 길이 큰 통로를 만나지 못하고 끝났습니다");
        }
        // 합쳐지는 자리는 샘(0열)이 아니라 작은 싱크홀 가까이입니다: 샘까지 15칸인데 그보다 훨씬 적게 가서 붙습니다.
        Assert.True(c % N >= 6, $"합쳐지는 칸 {c} 이 샘 바로 앞입니다 (열 {c % N})");
        Assert.True(steps <= 10, $"합쳐질 때까지 {steps}칸을 갔습니다");
        // 통로가 한 번 합쳐지는 동굴 하나: 끝 셋(싱크홀 둘, 샘 하나)과 합쳐지는 자리 하나, 가지 셋. 이어진 가지 수의 평균은 6 ÷ 4.
        Assert.Equal(1, net.Shape.NCaves);
        Assert.Equal(0, net.Shape.NSinglePassage);
        Assert.Equal(3, net.Shape.NBranches);
        Assert.Equal(1.5, net.Shape.MeanDegree, 12);
        Assert.True(net.Shape.Tortuosity >= 1.0);
    }

    [Fact]
    public void ASinkBelowAnotherOnTheSameValleyDoesNotCountTheSameWaterTwice()
    {
        // 지표 물길이 서쪽으로 흐르는 골짜기(12행)에 싱크홀 둘. 위쪽(22열)을 지나는 물은 1 × 10⁵, 아래쪽(16열)은 3 × 10⁵ 입니다.
        // 아래쪽 싱크홀로 들어가는 물은 위쪽이 삼킨 1 × 10⁵ 를 뺀 2 × 10⁵ 입니다.
        int upper = Cell(12, 22);
        int lower = Cell(12, 16);
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World([upper, lower], 1.0e5);
        area[lower] = 3.0e5;
        long[] rcv = Enumerable.Range(0, N * N).Select(c => c % N == 0 ? (long)c : c - 1).ToArray();
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(Receiver: rcv));
        Assert.Equal([lower, upper], net.Sinks);
        Assert.Equal([2.0e5, 1.0e5], net.SinkDischarge);
        Assert.Equal(3.0e5, net.Flow[Cell(N / 2, 0)], 6);
        // 지표 물길을 주지 않으면 그대로 더합니다.
        CaveNetwork plain = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params);
        Assert.Equal(4.0e5, plain.Flow[Cell(N / 2, 0)], 6);
    }

    [Fact]
    public void AStreamThatSinksIsAlwaysASinkAndCandidatesNearItAreDropped()
    {
        // 개울이 사라지는 칸(둘 수 없는 칸이어도, 빗물이 모이는 넓이가 0 이어도)은 싱크홀입니다. 들어가는 물은 적어 준 양입니다.
        int stream = Cell(6, 14);
        int near = Cell(6, 17);   // 75 m 떨어진 후보: 간격 100 m 안이라 빠집니다
        int far = Cell(18, 20);
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World([near, far], 2.0e5, jitter: 0.0);
        allowed[stream] = false;
        double[] loss = new double[N * N];
        loss[stream] = 7.0e6;
        CaveNetwork net = CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params with { SinkSpacingM = 100.0 }, new CaveNetworkFields(StreamLossM3PerYr: loss));
        Assert.Equal([stream, far], net.Sinks);
        Assert.Equal([true, false], net.SinkIsStream);
        Assert.Equal([7.0e6, 2.0e5], net.SinkDischarge);
        Assert.Equal(7.2e6, net.Flow[Cell(N / 2, 0)], 3);
    }

    [Fact]
    public void RadiusIsTheSmallerOfTheFloodPipeAndTheTimeTheWallHadToRetreat()
    {
        // 가득 찬 관: f = 0.1, 기울기 0.002, g = 9.81 에서 10 m³/s 면 약 1.67 m, 60 m³/s 면 약 3.42 m. 거꾸로 풀면 같은 유량이 나옵니다.
        Assert.Equal(1.668, CaveNetwork.FullPipeRadius(10.0, 0.002, 9.81), 3);
        Assert.Equal(3.415, CaveNetwork.FullPipeRadius(60.0, 0.002, 9.81), 3);
        Assert.Equal(60.0, CaveNetwork.FullPipeDischarge(CaveNetwork.FullPipeRadius(60.0, 0.002, 9.81), 0.002, 9.81), 9);
        Assert.Equal(0.0, CaveNetwork.FullPipeRadius(0.0, 0.002, 9.81));
        // 잠겨 있는 동안 벽이 물러난 거리: 한 해 0.1 mm 씩, 범위 10 m. 강이 한 해 1 mm 파 내려가면 1 m, 0.2 mm 면 5 m.
        Assert.Equal(1.0, CaveNetwork.TimeLimitedRadius(1.0e-3, 1.0e-4, 10.0), 12);
        Assert.Equal(5.0, CaveNetwork.TimeLimitedRadius(2.0e-4, 1.0e-4, 10.0), 12);
        Assert.Equal(double.PositiveInfinity, CaveNetwork.TimeLimitedRadius(0.0, 1.0e-4, 10.0));
        Assert.Equal(double.PositiveInfinity, CaveNetwork.TimeLimitedRadius(1.0e-3, 0.0, 10.0));
        // 한 해 평균 2 m³/s 인 통로: 큰비 때 60 m³/s 라 3.42 m 가 필요하지만 그리는 위 한계 3 m 로 자릅니다.
        double q = 2.0 * Constants.SecondsPerYear;
        Assert.Equal(RadiusMax, CaveNetwork.Radius(q, 0.0, Params));
        // 평균 0.2 m³/s: 큰비 때 6 m³/s → (0.1 × 36 ÷ 0.77457)^0.2 ≈ 1.36 m 라 아래 한계 2 m.
        Assert.Equal(CaveNetwork.RadiusMinM, CaveNetwork.Radius(0.2 * Constants.SecondsPerYear, 0.0, Params));
        // 평균 1 m³/s: 큰비 때 30 m³/s → 약 2.59 m. 강이 한 해 1 mm 파 내려가는 곳이면 벽이 1 m 만 물러나 아래 한계 2 m.
        var timed = Params with { WallRetreatMPerYr = 1.0e-4, ActiveZoneM = 10.0 };
        Assert.Equal(2.588, CaveNetwork.Radius(Constants.SecondsPerYear, 0.0, timed), 3);
        Assert.Equal(2.588, CaveNetwork.Radius(Constants.SecondsPerYear, 2.0e-4, timed), 3);
        Assert.Equal(CaveNetwork.RadiusMinM, CaveNetwork.Radius(Constants.SecondsPerYear, 1.0e-3, timed));
    }

    [Fact]
    public void PassageRadiiFollowTheFlowAndShaftsReachFromTheSurfaceToThePassage()
    {
        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        // 싱크홀마다 한 해 평균 1 m³/s 가 들어갑니다. 둘이 합쳐진 통로는 2 m³/s 입니다.
        double[] q = area.Select(a => a > 0.0 ? Constants.SecondsPerYear : 0.0).ToArray();
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(DischargeM3PerYr: q));
        Assert.Equal(sinks.Length, net.SegIsShaft.Count(b => b));
        for (int e = 0; e < net.NSegments; e++)
        {
            if (!net.SegIsShaft[e])
            {
                continue;
            }
            // 수직굴: 지표(물 높이 + 60 m) 위에서 시작해 통로까지 곧게 내려갑니다.
            Assert.Equal(net.SegA[e * 3], net.SegB[e * 3]);
            Assert.Equal(net.SegA[(e * 3) + 1], net.SegB[(e * 3) + 1]);
            Assert.True(net.SegA[(e * 3) + 2] - net.SegB[(e * 3) + 2] > 55.0);
        }
        // 층 통로 조각은 칸 번호 순서로 놓이고, 반지름은 그 칸을 지나는 물로 정해집니다.
        int[] cells = Enumerable.Range(0, N * N).Where(c => net.Flow[c] > 0.0 && net.Parent[c] >= 0).ToArray();
        double[] actual = Enumerable.Range(0, net.NSegments).Where(e => !net.SegIsShaft[e]).Select(e => net.SegRadius[e]).ToArray();
        Assert.Equal(cells.Select(c => CaveNetwork.Radius(net.Flow[c], 0.0, Params)), actual);
        Assert.Equal(cells.Select(c => net.CellRadius[c]), actual);
        Assert.Equal(sinks.Length + cells.Length, net.NSegments);
        Assert.Contains(actual, r => r > 2.5 && r < 2.7);  // 1 m³/s: 약 2.59 m
        Assert.Contains(actual, r => r == RadiusMax);       // 2 m³/s 이상: 위 한계
        // 층리면을 주지 않으면 통로가 물 높이를 따라갑니다: 가운데 선은 물 높이 + 0.4 × 반지름, 다 잠긴 길이 0, 호수 없음.
        foreach (int c in cells)
        {
            Assert.Equal(levels[c] + (CaveNetwork.StreamLift * net.CellRadius[c]), net.AxisZ[c], 12);
        }
        Assert.Equal(0.0, net.FloodedM);
        Assert.Empty(net.Lakes);
    }

    [Fact]
    public void DippingBedsMakeThePassageLoopBelowTheWaterAndLeaveLakes()
    {
        // 폭: 매머드 동굴(길이 2,200 m, 기운 각 0.6°)이면 평균 깊이 약 2.3 m (폭은 그 두 배), 길이 800 m·10° 면 약 10 m.
        Assert.Equal(2.0 * 2.28, CaveNetwork.LoopDepth(2200.0, Math.Sin(0.6 * Math.PI / 180.0)), 1);
        Assert.Equal(2.0 * 9.8, CaveNetwork.LoopDepth(800.0, Math.Sin(10.0 * Math.PI / 180.0)), 0);
        Assert.Equal(0.0, CaveNetwork.LoopDepth(800.0, 0.0));
        Assert.Equal(0.0, CaveNetwork.LoopDepth(10.0, 0.01));  // 폭이 1 m 에 못 미치면 물 높이를 그대로 따라감
        Assert.Equal(CaveNetwork.LoopMaxM, CaveNetwork.LoopDepth(1.0e6, 1.0));

        int[] sinks = [Cell(3, 20), Cell(12, 22), Cell(21, 18)];
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World(sinks, 2.0e5);
        // 층리면이 동쪽으로 100 m 에 20 m 씩 내려갑니다 (기운 각의 sin ≈ 0.196).
        int n = N * N;
        double[] bed = Enumerable.Range(0, n).Select(c => 50.0 - (0.2 * (((c % N) + 0.5) * Dx))).ToArray();
        double[] dip = Enumerable.Repeat(0.2 / Math.Sqrt(1.04), n).ToArray();
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(BedZ: bed, DipSin: dip));
        int spring = Cell(N / 2, 0);
        double deepest = 0.0;
        for (int c = 0; c < n; c++)
        {
            if (!(net.Flow[c] > 0.0))
            {
                continue;
            }
            // 통로는 물 높이를 따라가는 자리보다 높지 않고, 내려간 깊이는 폭의 위 한계(LoopMaxM)를 넘지 않습니다.
            double drop = levels[c] + (CaveNetwork.StreamLift * net.CellRadius[c]) - net.AxisZ[c];
            Assert.InRange(drop, -1e-9, CaveNetwork.LoopMaxM);
            deepest = Math.Max(deepest, drop);
        }
        // 샘에서는 물이 지표로 나오므로 내려가지 않습니다.
        Assert.Equal(levels[spring] + (CaveNetwork.StreamLift * net.CellRadius[spring]), net.AxisZ[spring], 12);
        Assert.True(deepest > 5.0, $"가장 깊이 내려간 곳이 {deepest} m 입니다");
        Assert.True(net.MeanLoopDepthM > 0.5 && net.MeanLoopDepthM < deepest);
        Assert.True(net.FloodedM > 0.0 && net.FloodedM < net.LowerLevelM);
        // 호수: 물이 2.5 m 넘게 깊고 천장 아래에 0.5 m 넘게 빈 곳이 남은 통로 칸.
        Assert.NotEmpty(net.Lakes);
        for (int i = 0; i < net.Lakes.Length; i++)
        {
            int c = net.Lakes[i];
            Assert.True(levels[c] - (net.AxisZ[c] - net.CellRadius[c]) >= CaveNetwork.LakeMinWaterM);
            Assert.True(net.AxisZ[c] + net.CellRadius[c] - levels[c] >= CaveNetwork.LakeMinAirM);
            Assert.Equal(levels[c], net.LakeXyz[(i * 3) + 2]);
        }
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
        // 싱크홀 하나에서 샘까지 외길인 동굴 하나.
        Assert.Equal(1, net.Shape.NCaves);
        Assert.Equal(1, net.Shape.NSinglePassage);
        Assert.Equal(0.0, net.Shape.MeanDegree);
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
    public void WrongLengthsAndSettingsAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = World([Cell(12, 22)], 2.0e5);
        ArgumentException e = Assert.Throws<ArgumentException>(
            () => CaveNetwork.Build(g, z, levels, new bool[7], allowed, area, Params));
        Assert.Contains("7", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CaveNetwork.Build(g, new double[3], levels, outlet, allowed, area, Params));
        Assert.Throws<ArgumentException>(
            () => CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params with { RadiusMaxM = 0.0 }));
        e = Assert.Throws<ArgumentException>(
            () => CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(UpliftMPerYr: new double[5])));
        Assert.Contains("UpliftMPerYr", e.Message, StringComparison.Ordinal);
        Assert.Contains("5", e.Message, StringComparison.Ordinal);
    }
}
