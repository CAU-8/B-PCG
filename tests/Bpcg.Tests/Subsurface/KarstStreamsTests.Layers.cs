using System;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>
/// 땅속으로 사라지는 개울 (docs/pipeline.md 8.5) 가운데 지층 조건. 개울은 바닥과 석회암 지하수면 사이에 물을 막는 층이 없고,
/// 지하수면 높이의 암석이 물에 녹는 암석일 때만 사라집니다. 막는 층은 투수율 log10 k 가 groundwater.aquitard_log10_perm_m2
/// (지구 설정 −15) 이하인 층입니다. 셰일(−16.5)이 여기에 들어갑니다.
/// </summary>
public sealed partial class KarstStreamsTests
{
    private const byte L = Rocks.Limestone;

    /// <summary>
    /// 칸마다 지표에서 잰 깊이로 쌓은 지층 기둥. 층 i 의 바닥은 지표 아래 bottomsM[i] m 이고, rocks 는 위층부터의 암석과 맨 아래
    /// 기반암입니다 (층 수 + 1개). layered 가 거짓인 칸은 모든 층이 석회암입니다.
    /// </summary>
    private static LayerColumns Layers(double[] z, double[] bottomsM, byte[] rocks, Func<int, bool>? layered = null)
    {
        int n = z.Length;
        int nl = bottomsM.Length;
        double[] bottom = new double[n * nl];
        byte[] rock = new byte[n * (nl + 1)];
        for (int c = 0; c < n; c++)
        {
            bool here = layered is null || layered(c);
            for (int i = 0; i < nl; i++)
            {
                bottom[(c * nl) + i] = z[c] - bottomsM[i];
            }
            for (int i = 0; i <= nl; i++)
            {
                rock[(c * (nl + 1)) + i] = here ? rocks[i] : L;
            }
        }
        return LayerColumns.FromColumns(bottom, rock, nl);
    }

    /// <summary>
    /// World 의 땅을 columns 지층으로 바꿔 돌립니다. 땅은 어디나 한 해 0.2 mm 솟고 강도 그만큼 파 내려가, 개울이 들어갈 통로는
    /// 한 해 평균 초당 약 1.45 m³ 까지 삼킵니다 (초당 0.5 m³ 인 개울을 다 삼킴).
    /// </summary>
    private static KarstStreamResult? RunOn(
        (CellGraph G, double[] Z, long[] Rcv, long[] Order, double[] Source, double[] Q, bool[] River, double[] Level, byte[] Rock,
            LayerColumns Columns) w, LayerColumns columns, Config cfg)
    {
        int n = w.Z.Length;
        return KarstStreams.Apply(
            w.G, w.Z, w.Rcv, w.Order, w.Source, w.Q, w.River, new bool[n], new bool[n], w.Level, w.Rock,
            Enumerable.Repeat(2.0e-4, n).ToArray(), columns, cfg);
    }

    [Fact]
    public void ALayerThatBlocksWaterAboveTheKarstWaterTableKeepsTheStreamOnTheSurface()
    {
        var w = World(0.5);
        int head = Cell(StreamRow, Cols - 1);
        // 지표 아래 5~15 m 에 셰일이 깔린 석회암 땅. 개울이 시작하는 칸의 석회암 지하수면은 지표 아래 약 47 m 라, 셰일이 개울 바닥과
        // 지하수면 사이를 막습니다. 개울은 어디서도 사라지지 않습니다.
        LayerColumns shallow = Layers(w.Z, [5.0, 15.0, 5000.0], [L, Rocks.Shale, L, L]);
        Assert.Null(RunOn(w, shallow, Cfg()));
        // 막는 층 문턱을 셰일보다 낮추면(−17) 셰일도 물이 통하는 층으로 봅니다. 셰일이 없을 때처럼 개울이 시작하는 칸에서 사라집니다.
        KarstStreamResult loose = RunOn(w, shallow, Cfg(("groundwater.aquitard_log10_perm_m2", -17.0)))!;
        Assert.Equal(1, loose.NSinks);
        Assert.True(loose.Exchange[head] < 0.0);
        // 셰일이 지하수면보다 깊이(지표 아래 150~160 m) 있으면 개울 바닥과 지하수면 사이에는 막는 층이 없어, 개울이 시작하는 칸에서 사라집니다.
        KarstStreamResult deep = RunOn(w, Layers(w.Z, [150.0, 160.0, 5000.0], [L, Rocks.Shale, L, L]), Cfg())!;
        Assert.Equal(1, deep.NSinks);
        Assert.True(deep.Exchange[head] < 0.0);
    }

    [Fact]
    public void TheStreamSinksWhereTheBlockingLayerEnds()
    {
        var w = World(0.5);
        // 지표 아래 5~15 m 의 셰일이 동쪽 20열부터 39열 아래에만 있습니다. 개울은 셰일 위를 흐르다가 셰일이 끝나는 19열에서 사라집니다.
        // 19열의 바닥(123.75 m)은 큰 강에서 이어지는 석회암 지하수면(약 100.9 m)보다 약 23 m 높습니다.
        LayerColumns east = Layers(w.Z, [5.0, 15.0, 5000.0], [L, Rocks.Shale, L, L], c => c % Cols >= 20);
        KarstStreamResult r = RunOn(w, east, Cfg())!;
        double lost = 0.5 * Constants.SecondsPerYear;
        Assert.Equal(1, r.NSinks);
        Assert.Equal(-lost, r.Exchange[Cell(StreamRow, 19)], 3);
        Assert.Equal(lost, r.Exchange[Cell(StreamRow, 0)], 3);
        Assert.Equal(0.0, r.Exchange.Sum(), 3);
        // 셰일 위에서는 개울이 그대로 흐르고, 사라진 칸부터 큰 강 앞까지는 마릅니다.
        for (int col = 20; col < Cols; col++)
        {
            Assert.Equal(lost, r.Discharge[Cell(StreamRow, col)], 3);
        }
        for (int col = 1; col <= 19; col++)
        {
            Assert.Equal(0.0, r.Discharge[Cell(StreamRow, col)]);
        }
    }

    [Fact]
    public void NothingSinksWhereTheRockAtTheKarstWaterTableDoesNotDissolve()
    {
        var w = World(0.5);
        int head = Cell(StreamRow, Cols - 1);
        // 지표의 석회암이 5 m 두께뿐이고 그 아래가 사암이면, 석회암 지하수면 높이의 암석이 물에 녹지 않는 사암이라 물을 큰 강까지 빼는
        // 통로가 생기지 않습니다. 개울은 흐릅니다.
        Assert.Null(RunOn(w, Layers(w.Z, [5.0, 5000.0], [L, Rocks.Sandstone, Rocks.Sandstone]), Cfg()));
        // 사암이 지표 아래 5~30 m 에만 있고 그 아래가 석회암이면, 개울이 시작하는 칸의 지하수면(지표 아래 약 47 m)이 석회암 안에 옵니다.
        // 사암은 물이 잘 통해(log10 k = −12.5) 막는 층이 아니므로 개울이 그 칸에서 사라집니다.
        LayerColumns sandy = Layers(w.Z, [5.0, 30.0, 5000.0], [L, Rocks.Sandstone, L, L]);
        KarstStreamResult r = RunOn(w, sandy, Cfg())!;
        Assert.Equal(1, r.NSinks);
        Assert.True(r.Exchange[head] < 0.0);
        // 막는 층 문턱을 사암보다 높이면(−12) 사암도 막는 층이 되어 개울이 흐릅니다.
        Assert.Null(RunOn(w, sandy, Cfg(("groundwater.aquitard_log10_perm_m2", -12.0))));
    }
}
