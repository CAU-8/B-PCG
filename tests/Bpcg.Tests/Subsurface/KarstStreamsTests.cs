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
            LayerColumns Columns) w, double uplift, Config cfg)
    {
        int n = w.Z.Length;
        return KarstStreams.Apply(
            w.G, w.Z, w.Rcv, w.Order, w.Source, w.Q, w.River, new bool[n], new bool[n], w.Level, w.Rock,
            Enumerable.Repeat(uplift, n).ToArray(), w.Columns, cfg);
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
}
