using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>석회암 땅에서 땅속으로 사라지는 개울 (docs/pipeline.md 8.5).</summary>
public sealed partial class KarstStreamsTests
{
    private const int Rows = 11;
    private const int Cols = 40;
    private const double Dx = 25.0;
    private const int StreamRow = 5;

    private static Config Cfg(params (string Key, object Value)[] overrides) =>
        Config.LoadConfig("earth", "tiny").WithOverrides(overrides.Select(o => new KeyValuePair<string, object?>(o.Key, o.Value)));

    private static int Cell(int row, int col) => (row * Cols) + col;

    /// <summary>
    /// 서쪽 끝 열(0열)에 큰 강이 남쪽으로 흐르고(초당 50 m³), 땅은 동쪽으로 100 m 에 5 m 씩 높아집니다. 모든 칸의 물은 서쪽 이웃으로
    /// 흐릅니다. 5행의 동쪽 끝 칸에서 개울(초당 streamM3PerS)이 시작합니다. 땅은 rock 한 가지 암석입니다.
    /// </summary>
    private static (CellGraph G, double[] Z, long[] Rcv, long[] Order, double[] Source, double[] Q, bool[] River, double[] Level,
        byte[] Rock, LayerColumns Columns) World(double streamM3PerS, byte rock = Rocks.Limestone)
    {
        CellGraph g = Graph.FlatGraph(Rows, Cols, Dx);
        int n = Rows * Cols;
        double[] z = new double[n];
        long[] rcv = new long[n];
        double[] source = new double[n];
        for (int c = 0; c < n; c++)
        {
            int row = c / Cols;
            int col = c % Cols;
            z[c] = 100.0 + (0.05 * col * Dx) - (col == 0 ? 0.001 * row : 0.0);
            // 0열은 남쪽(행 번호가 큰 쪽)으로, 나머지는 서쪽으로 흐릅니다. 큰 강의 맨 아래 칸이 출구입니다.
            rcv[c] = col == 0 ? (row == Rows - 1 ? c : c + Cols) : c - 1;
        }
        source[Cell(0, 0)] = 50.0 * Constants.SecondsPerYear;
        source[Cell(StreamRow, Cols - 1)] = streamM3PerS * Constants.SecondsPerYear;
        long[] order = Routing.TopoOrder(rcv);
        double[] q = AccumulateModule.Accumulate(rcv, order, source);
        double qMin = 0.3 * Constants.SecondsPerYear;
        bool[] river = q.Select(v => v >= qMin).ToArray();
        double[] level = Enumerable.Range(0, n).Select(c => river[c] ? z[c] : double.NaN).ToArray();
        byte[] surface = Enumerable.Repeat(rock, n).ToArray();
        // 지층 한 겹: 바닥이 아주 깊은 rock, 그 아래 기반암도 rock.
        double[] bottom = Enumerable.Repeat(-5000.0, n).ToArray();
        byte[] strata = Enumerable.Repeat(rock, n * 2).ToArray();
        return (g, z, rcv, order, source, q, river, level, surface, LayerColumns.FromColumns(bottom, strata, 1));
    }

    private static KarstStreamResult? Run(
        (CellGraph G, double[] Z, long[] Rcv, long[] Order, double[] Source, double[] Q, bool[] River, double[] Level, byte[] Rock,
            LayerColumns Columns) w, double uplift, Config cfg, bool[]? noSink = null)
    {
        int n = w.Z.Length;
        return KarstStreams.Apply(
            w.G, w.Z, w.Rcv, w.Order, w.Source, w.Q, w.River, new bool[n], new bool[n], w.Level, w.Rock,
            Enumerable.Repeat(uplift, n).ToArray(), w.Columns, cfg, noSink);
    }

    [Fact]
    public void CapacityShrinksWhereTheRiverCutsDownFast()
    {
        var p = new CaveNetworkParams(3.0, 0.0, 0.0, 1.0, 0, WallRetreatMPerYr: 1.0e-4, ActiveZoneM: 10.0);
        // 강이 한 해 0.2 mm 파 내려가면 벽이 5 m 물러날 수 있지만 그리는 한계 3 m 로 봅니다: 큰비 때 약 43.4 m³/s, 평균 약 1.45 m³/s.
        Assert.Equal(1.446, KarstStreams.Capacity(2.0e-4, p) / Constants.SecondsPerYear, 3);
        // 한 해 2 mm 파 내려가면 벽이 0.5 m 만 물러나, 평균 약 0.016 m³/s 밖에 흘리지 못합니다.
        Assert.Equal(0.0164, KarstStreams.Capacity(2.0e-3, p) / Constants.SecondsPerYear, 4);
    }

    [Fact]
    public void ASmallStreamOnLimestoneSinksAndComesBackAtTheBigRiver()
    {
        var w = World(0.5);
        KarstStreamResult r = Run(w, 2.0e-4, Cfg())!;
        Assert.NotNull(r);
        int head = Cell(StreamRow, Cols - 1);
        int spring = Cell(StreamRow, 0);
        double lost = 0.5 * Constants.SecondsPerYear;
        // 개울은 시작하는 칸에서 바로 사라집니다: 그 칸의 바닥(148.75 m)이 큰 강에서 이어지는 지하수면(약 102 m)보다 46 m 높습니다.
        Assert.Equal(1, r.NSinks);
        Assert.Equal(1, r.NSprings);
        Assert.Equal(-lost, r.Exchange[head], 3);
        Assert.Equal(lost, r.Exchange[spring], 3);
        Assert.Equal(lost, r.LostM3PerYr, 3);
        Assert.Equal(lost, r.RisenM3PerYr, 3);
        Assert.Equal(0, r.NLoopsCut);
        Assert.Equal(0.0, r.Exchange.Sum(), 3);
        // 사라진 아래쪽 골짜기에는 물이 없고, 큰 강은 그 물을 돌려받아 예전과 같은 양이 흐릅니다.
        for (int col = 1; col < Cols; col++)
        {
            Assert.Equal(0.0, r.Discharge[Cell(StreamRow, col)]);
        }
        Assert.Equal(w.Q[spring], r.Discharge[spring], 3);
        Assert.Equal(w.Q[Cell(Rows - 1, 0)], r.Discharge[Cell(Rows - 1, 0)], 3);
        // 큰 물은 큰 강 칸뿐입니다.
        Assert.Equal(Enumerable.Range(0, Rows).Select(row => Cell(row, 0)), Enumerable.Range(0, Rows * Cols).Where(c => r.Big[c]));
        // 유량 = 생긴 물 + 땅속과 주고받은 물을 상류부터 더한 값.
        double[] total = w.Source.Zip(r.Exchange, (a, b) => a + b).ToArray();
        double[] expect = AccumulateModule.Accumulate(w.Rcv, w.Order, total);
        for (int c = 0; c < expect.Length; c++)
        {
            Assert.Equal(expect[c], r.Discharge[c], 3);
        }
    }

    [Fact]
    public void NothingSinksOnOtherRockOrWhereTheConduitStaysThinOrTheKeysAreMissing()
    {
        // 화강암 땅: 물에 녹지 않아 개울이 그대로 흐릅니다.
        Assert.Null(Run(World(0.5, Rocks.Granite), 2.0e-4, Cfg()));
        // 강이 한 해 2 mm 파 내려가는 석회암: 통로가 가늘어 초당 0.5 m³ 를 다 삼키지 못하므로 개울도 큰 물입니다.
        Assert.Null(Run(World(0.5), 2.0e-3, Cfg()));
        // 벽이 녹는 속도를 0 으로 두면 이 과정을 끕니다.
        Assert.Null(Run(World(0.5), 2.0e-4, Cfg(("caves.wall_retreat_m_per_yr", 0.0))));
        // 지하수면이 지표 아래 max_depth_m 보다 깊은 칸(여기서는 30 m 로 줄임)에서는 이을 동굴이 없어 개울이 그대로 흐릅니다.
        // 개울은 내려오다가 그 깊이가 30 m 안으로 들어오는 첫 칸에서 사라집니다.
        var w = World(0.5);
        KarstStreamResult r = Run(w, 2.0e-4, Cfg(("groundwater.max_depth_m", 30.0)))!;
        int sink = Enumerable.Range(0, Rows * Cols).Single(c => r.Exchange[c] < 0.0);
        Assert.Equal(StreamRow, sink / Cols);
        double drop = w.Z[sink] - (w.Z[Cell(StreamRow, 0)] + (0.002 * (sink % Cols) * Dx));
        Assert.InRange(drop, KarstStreams.PerchMinM, 30.0);
        // 그 위쪽은 개울이 흐르고 아래쪽은 마릅니다.
        Assert.True(r.Discharge[sink + 1] > 0.0);
        Assert.Equal(0.0, r.Discharge[sink - 1]);
    }

    [Fact]
    public void ABlockedCellLetsTheStreamFlowOnToTheNextPerchedCell()
    {
        // 개울이 시작하는 칸(39열)을 막으면, 개울은 한 칸 내려간 38열에서 사라집니다. 그 칸도 지하수면보다 45 m 넘게 떠 있습니다.
        var w = World(0.5);
        int n = Rows * Cols;
        bool[] noSink = new bool[n];
        noSink[Cell(StreamRow, Cols - 1)] = true;
        KarstStreamResult r = Run(w, 2.0e-4, Cfg(), noSink)!;
        Assert.Equal([Cell(StreamRow, Cols - 2)], Enumerable.Range(0, n).Where(c => r.Exchange[c] < 0.0));
        Assert.Equal(0.5 * Constants.SecondsPerYear, r.Discharge[Cell(StreamRow, Cols - 1)], 3);
        // 11~39열을 모두 막으면 10열에서 사라집니다. 그 위쪽 골짜기에는 개울이 그대로 흐르고, 아래쪽은 마릅니다.
        for (int col = 11; col < Cols; col++)
        {
            noSink[Cell(StreamRow, col)] = true;
        }
        r = Run(w, 2.0e-4, Cfg(), noSink)!;
        Assert.Equal([Cell(StreamRow, 10)], Enumerable.Range(0, n).Where(c => r.Exchange[c] < 0.0));
        Assert.True(r.Discharge[Cell(StreamRow, 11)] > 0.0);
        Assert.Equal(0.0, r.Discharge[Cell(StreamRow, 9)]);
        Assert.Equal(r.LostM3PerYr, r.RisenM3PerYr, 3);
        // 모두 막으면 사라지는 칸이 없어 null 입니다.
        Array.Fill(noSink, true);
        Assert.Null(Run(w, 2.0e-4, Cfg(), noSink));
        // 길이가 틀린 막는 칸 배열은 받은 길이를 알려 줍니다.
        ArgumentException e = Assert.Throws<ArgumentException>(() => Run(w, 2.0e-4, Cfg(), new bool[3]));
        Assert.Contains("noSink 3", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 칸 7개로 된 물길: 개울 0 → 1 → 2 → 3 → 4 → 5 → 6(끝). 1과 4가 떠 있는 칸이고, 1의 샘은 3, 4의 샘은 6 입니다.
    /// 1에서 사라진 물은 3에서 솟아 4로 흘러갑니다. 물의 양은 qMin = 0.3, 1의 통로 1.0, 4의 통로 0.6 입니다.
    /// </summary>
    private static (long[] Rcv, long[] Order, double[] Source, bool[] Perched, long[] Spring, double[] Capacity) Chain()
    {
        long[] rcv = [1, 2, 3, 4, 5, 6, 6];
        long[] order = [6, 5, 4, 3, 2, 1, 0];
        double[] source = [0.5, 0.0, 0.0, 0.2, 0.0, 0.0, 0.0];
        bool[] perched = [false, true, false, false, true, false, false];
        long[] spring = [-1, 3, -1, -1, 6, -1, -1];
        double[] capacity = [0.0, 1.0, 0.0, 0.0, 0.6, 0.0, 0.0];
        return (rcv, order, source, perched, spring, capacity);
    }

    [Fact]
    public void RisenWaterReachesTheNextPerchedCellBeforeItIsJudged()
    {
        (long[] rcv, long[] order, double[] source, bool[] perched, long[] spring, double[] capacity) = Chain();
        (double[] q, double[] loss, double[] rise, List<int> stuck) =
            KarstStreams.Route(rcv, order, source, perched, spring, capacity, 0.3);
        Assert.Empty(stuck);
        // 1은 0.5 를 받아 모두 보냅니다. 그 물이 3에서 솟아 4에는 0.2 + 0.5 = 0.7 이 닿습니다. 4의 통로(0.6)보다 많아 4는 그대로 흐릅니다.
        Assert.Equal([0.0, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0], loss);
        Assert.Equal([0.0, 0.0, 0.0, 0.5, 0.0, 0.0, 0.0], rise);
        Assert.Equal(0.7, q[4], 12);
        Assert.Equal(0.7, q[6], 12);
        Assert.Equal(0.0, q[1]);
        // 4의 통로가 0.8 이면 솟은 물까지 0.7 을 모두 삼킵니다. 솟은 물이 오기 전의 0.2 로 판단했다면 qMin(0.3) 아래라 흘려보냈을
        // 자리입니다. 사라진 물(0.5 + 0.7)과 솟은 물(3에서 0.5, 6에서 0.7)의 합이 같습니다.
        capacity[4] = 0.8;
        (q, loss, rise, stuck) = KarstStreams.Route(rcv, order, source, perched, spring, capacity, 0.3);
        Assert.Empty(stuck);
        Assert.Equal(0.7, loss[4], 12);
        Assert.Equal(0.7, rise[6], 12);
        Assert.Equal(loss.Sum(), rise.Sum(), 12);
        Assert.Equal(0.7, q[6], 12);
    }

    [Fact]
    public void ALoopThroughTheGroundIsReportedInsteadOfCountedForever()
    {
        // 4의 샘을 맨 위 칸 0 으로 두면, 4에서 사라진 물이 0에서 솟아 다시 4로 흘러드는 고리가 생깁니다. 고리 위와 그 아래 칸은
        // 고르지 못한 칸으로 돌려줍니다 (Apply 는 그 가운데 떠 있는 칸을 막고 다시 모읍니다).
        (long[] rcv, long[] order, double[] source, bool[] perched, long[] spring, double[] capacity) = Chain();
        spring[4] = 0;
        (_, _, _, List<int> stuck) = KarstStreams.Route(rcv, order, source, perched, spring, capacity, 0.3);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], stuck);
    }

    [Fact]
    public void SinksWhoseWaterCannotDrainToASpringAreFound()
    {
        // 0열이 강(수면 100 m)이고, 아래층은 동쪽으로 한 칸에 0.05 m 씩 오릅니다. 모든 칸이 강가 샘까지 물 높이가 낮아지는 길로 닿습니다.
        const int rows = 9;
        const int cols = 12;
        int n = rows * cols;
        CellGraph g = Graph.FlatGraph(rows, cols, Dx);
        double[] level0 = new double[n];
        bool[] water = new bool[n];
        double[] waterLevel = new double[n];
        Array.Fill(waterLevel, double.NaN);
        for (int c = 0; c < n; c++)
        {
            int col = c % cols;
            level0[c] = 100.0 + (0.05 * col);
            water[c] = col == 0;
            waterLevel[c] = col == 0 ? 100.0 : double.NaN;
        }
        int east = (4 * cols) + 10;
        int north = (1 * cols) + 9;
        double[] exchange = new double[n];
        exchange[east] = -1.0;
        exchange[north] = -1.0;
        // 지표는 아래층보다 60 m 높습니다 (개울 바닥이 지하수면보다 떠 있음).
        double[] z = level0.Select(v => v + 60.0).ToArray();
        int[] Stray(double[] lv) => KarstStreams.WithoutSpring(exchange, KarstStreams.DrainsToSpring(g, lv, lv, z, water, waterLevel));
        Assert.Empty(Stray(level0));
        // 6열의 아래층을 380 m 로 올리면 동쪽 칸의 물은 380 m 로 오르지 못해 샘까지 갈 수 없습니다.
        double[] stepped = (double[])level0.Clone();
        for (int row = 0; row < rows; row++)
        {
            stepped[(row * cols) + 6] = 380.0;
        }
        Assert.Equal([north, east], Stray(stepped));
        // 6열에 아래층이 없어도(NaN) 마찬가지입니다. 사라지는 칸 자체에 아래층이 없으면 그 칸도 갈 수 없습니다.
        double[] cut = (double[])level0.Clone();
        for (int row = 0; row < rows; row++)
        {
            cut[(row * cols) + 6] = double.NaN;
        }
        Assert.Equal([north, east], Stray(cut));
        double[] holed = (double[])level0.Clone();
        holed[north] = double.NaN;
        Assert.Equal([north], Stray(holed));
        // 6열 가운데 한 칸만 이어져 있으면 그 칸을 지나 샘까지 갑니다.
        stepped[(7 * cols) + 6] = 100.3;
        Assert.Empty(Stray(stepped));
        // 6열이 옆 칸보다 0.75 m 만 높으면, 동쪽 칸의 물은 101 m 까지 차올라 넘어갑니다(넘침 높이). 개울 바닥이 그보다 59 m 넘게
        // 높으므로 개울은 여전히 떠 있어 물을 잃습니다.
        double[] ridge = (double[])level0.Clone();
        for (int row = 0; row < rows; row++)
        {
            ridge[(row * cols) + 6] = 101.0;
        }
        Assert.Empty(Stray(ridge));
        // 지표가 아래층보다 5 m 만 높으면, 101 m 까지 차오른 물이 바닥 가까이 닿아 동쪽 칸은 물을 잃지 않습니다.
        // 차오르지 않아도 빠지는 칸(넘침 높이 = 아래층)은 깊이를 다시 보지 않습니다.
        double[] low = level0.Select(v => v + 5.0).ToArray();
        bool[] shallow = KarstStreams.DrainsToSpring(g, ridge, ridge, low, water, waterLevel);
        Assert.False(shallow[east]);
        Assert.False(shallow[north]);
        Assert.True(shallow[(4 * cols) + 5]);
        // 강 칸과 그 옆 칸은 샘이라 늘 참이고, 아래층이 없는 칸은 늘 거짓입니다.
        bool[] drains = KarstStreams.DrainsToSpring(g, cut, cut, z, water, waterLevel);
        Assert.True(drains[4 * cols]);
        Assert.True(drains[(4 * cols) + 1]);
        Assert.False(drains[(4 * cols) + 6]);
        // 6열의 아래층이 지표에서 300 m 보다 깊어 그리지 않았을 뿐이면(길 높이 route0 에는 있음), 물은 그 밑을 지나 샘까지 갑니다.
        // 6열 자체는 그린 아래층이 없어 싱크홀을 둘 수 없으므로 거짓입니다.
        drains = KarstStreams.DrainsToSpring(g, cut, level0, z, water, waterLevel);
        Assert.True(drains[east]);
        Assert.True(drains[north]);
        Assert.False(drains[(4 * cols) + 6]);
        Assert.Empty(KarstStreams.WithoutSpring(exchange, drains));
        ArgumentException e = Assert.Throws<ArgumentException>(
            () => KarstStreams.DrainsToSpring(g, new double[5], new double[5], z, water, waterLevel));
        Assert.Contains("5", e.Message, StringComparison.Ordinal);
        // 길 높이는 그린 아래층이 있는 칸에서 아래층과 같아야 합니다.
        double[] wrong = (double[])level0.Clone();
        wrong[east] += 1.0;
        e = Assert.Throws<ArgumentException>(() => KarstStreams.DrainsToSpring(g, level0, wrong, z, water, waterLevel));
        Assert.Contains($"칸 {east}", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteLevelAddsLimestoneDeeperThanTheBakedDepth()
    {
        // 칸 넷. 0: 그린 아래층이 있음. 1: 아래층이 없고 아래 지하수면이 지표 아래 420 m 인 석회암.
        // 2: 1과 같은 깊이지만 그 높이가 화강암. 3: 아래층이 없지만 지하수면이 지표 아래 50 m 라 깊어서 빈 칸이 아님.
        double[] level0 = [100.25, double.NaN, double.NaN, double.NaN];
        double[] zLower = [100.25, 80.5, 80.5, 80.5];
        double[] z = [160.0, 500.5, 500.5, 130.5];
        double[] bottom = [-5000.0, -5000.0, -5000.0, -5000.0];
        byte[] rock = [Rocks.Limestone, Rocks.Limestone, Rocks.Limestone, Rocks.Limestone, Rocks.Granite, Rocks.Granite,
            Rocks.Limestone, Rocks.Limestone];
        LayerColumns cols = LayerColumns.FromColumns(bottom, rock, 1);
        double[] route = Caves.RouteLevel0(level0, zLower, z, cols, 300.0);
        Assert.Equal(100.25, route[0]);
        Assert.Equal(80.5, route[1]);
        Assert.True(double.IsNaN(route[2]));
        Assert.True(double.IsNaN(route[3]));
        // 묶음에 32비트로 저장한 값과 같아지도록 줄여서 돌려줍니다: 100.1 은 100.0999984741…
        route = Caves.RouteLevel0([100.1, double.NaN, double.NaN, double.NaN], zLower, z, cols, 300.0);
        Assert.Equal((double)(float)100.1, route[0]);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Caves.RouteLevel0(level0, new double[2], z, cols, 300.0));
        Assert.Contains("2", e.Message, StringComparison.Ordinal);
    }
}
