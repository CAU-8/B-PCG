using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>산사태 둑 (docs/pipeline.md 7.4): 강 위에 얹은 둔덕이 골짜기를 막아 위쪽에 물이 고입니다.</summary>
public sealed class DamsTests
{
    private const int N = 41;
    private const double Dx = 25.0;
    private const double ChannelSlope = 0.02;
    private const int ChannelRow = N / 2;

    private static Config Cfg(params (string Key, object Value)[] overrides)
    {
        var all = new List<KeyValuePair<string, object?>>
        {
            new("dams.enabled", true),
            new("dams.spacing_m", 10_000.0),
            new("dams.min_height_m", 30.0),
            new("dams.max_height_m", 30.0),
            new("dams.slope", 0.3),
            new("dams.min_discharge_m3_per_s", 1.0),
            new("dams.max_discharge_m3_per_s", 60.0),
            new("dams.min_slope_ratio", 0.9),
        };
        foreach ((string key, object value) in overrides)
        {
            all.RemoveAll(kv => kv.Key == key);
            all.Add(new(key, value));
        }
        return Config.LoadConfig("earth", "tiny").WithOverrides(all);
    }

    /// <summary>
    /// 서쪽으로 흐르는 V 자 골짜기 하나 (한 변 약 1 km). 가운데 행이 강이고 (경사 0.02, 유량 riverM3PerS), 양옆 비탈은
    /// 경사 sideSlope 로 강을 향해 내려옵니다. 출구는 강의 서쪽 끝 칸입니다. 무너지는 경사 S_crit 은 0.8 입니다.
    /// </summary>
    private static (CellGraph Graph, SolverResult Result) Valley(double sideSlope, double riverM3PerS)
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        int n = N * N;
        double[] z = new double[n];
        long[] rcv = new long[n];
        double[] q = new double[n];
        double[] slope = new double[n];
        double[] sCrit = new double[n];
        double y0 = g.Pos[(ChannelRow * N * 3) + 1];
        for (int c = 0; c < n; c++)
        {
            int row = c / N;
            int col = c % N;
            double x = g.Pos[c * 3];
            double y = g.Pos[(c * 3) + 1];
            z[c] = 100.0 + (ChannelSlope * x) + (sideSlope * Math.Abs(y - y0));
            sCrit[c] = 0.8;
            if (row == ChannelRow)
            {
                rcv[c] = col == 0 ? c : c - 1;
                q[c] = riverM3PerS * Constants.SecondsPerYear;
                slope[c] = ChannelSlope;
            }
            else
            {
                rcv[c] = row < ChannelRow ? c + N : c - N;
                q[c] = 0.001 * Constants.SecondsPerYear;
                slope[c] = sideSlope;
            }
        }
        var result = new SolverResult
        {
            Z = z,
            Receiver = rcv,
            Order = Routing.TopoOrder(rcv),
            Discharge = q,
            DrainageArea = new double[n],
            SedimentFlux = new double[n],
            Slope = slope,
            KS = new double[n],
            SCritSurface = sCrit,
            Iterations = 1,
        };
        return (g, result);
    }

    private static bool[] Outlet(SolverResult r) => r.Receiver.Select((v, c) => v == c).ToArray();

    [Fact]
    public void DamRaisesAMoundOnTheRiverAndWaterPondsUpstream()
    {
        (CellGraph g, SolverResult r) = Valley(sideSlope: 0.8, riverM3PerS: 10.0);
        // 둑 자리를 골짜기의 서쪽 절반으로 묶어, 둑 위쪽(동쪽)에 물이 고일 골짜기가 500 m 넘게 남게 합니다.
        bool[] eastHalf = Enumerable.Range(0, N * N).Select(c => c % N >= N / 2).ToArray();
        (double[] zNew, bool[] dam, List<object?> dams) = Dams.AddDams(g, r.Z, r, Cfg(), eastHalf);
        var site = (OrderedDictionary<string, object?>)Assert.Single(dams)!;
        int a = (int)(long)site["cell"]!;
        // 둑은 강 위에 놓이고, 둑 자리의 강바닥은 설정한 높이(30 m)만큼 솟습니다.
        Assert.Equal(ChannelRow, a / N);
        Assert.Equal(30.0, (double)site["height_m"]!);
        Assert.Equal(r.Z[a] + 30.0, zNew[a], 9);
        // 올린 칸은 모두 둔덕 반지름(30 m ÷ 0.3 = 100 m) 안에 있고(경계 포함), 올린 칸만 높이가 바뀝니다.
        for (int c = 0; c < dam.Length; c++)
        {
            double d = double.Hypot(g.Pos[c * 3] - g.Pos[a * 3], g.Pos[(c * 3) + 1] - g.Pos[(a * 3) + 1]);
            Assert.True(!dam[c] || d <= 100.0 + 1e-9, $"칸 {c} 는 둑에서 {d:F0} m 떨어졌는데 올렸습니다");
            Assert.True(dam[c] ? zNew[c] > r.Z[c] : zNew[c] == r.Z[c]);
        }
        Assert.Equal((long)dam.Count(b => b), (long)site["n_raised"]!);
        // 둑을 얹기 전에는 물이 고이는 칸이 없고, 얹은 뒤에는 둑 위쪽(동쪽)에만 고입니다.
        bool[] outlet = Outlet(r);
        double[] before = Depressions.FillDepressions(r.Z, g.Nbr, outlet);
        Assert.All(Enumerable.Range(0, before.Length), c => Assert.Equal(r.Z[c], before[c]));
        double[] after = Depressions.FillDepressions(zNew, g.Nbr, outlet);
        int[] ponded = Enumerable.Range(0, after.Length).Where(c => after[c] - zNew[c] > 1e-3).ToArray();
        Assert.NotEmpty(ponded);
        Assert.All(ponded, c => Assert.True(g.Pos[c * 3] > g.Pos[a * 3], $"둑 아래쪽 칸 {c} 에 물이 고였습니다"));
        // 물은 둔덕과 골짜기 벽이 만나는 낮은 목에서 넘치므로 호수는 둑 높이보다 얕고, 그래도 10 m 는 넘습니다.
        double deepest = ponded.Max(c => after[c] - zNew[c]);
        Assert.InRange(deepest, 10.0, 30.0);
    }

    [Fact]
    public void NoDamWhereTheSideSlopeIsGentleOrTheRiverIsOutOfRange()
    {
        // 옆 비탈 경사 0.3 은 무너지는 경사 0.8 의 0.9 배(0.72)에 못 미칩니다.
        (CellGraph gentle, SolverResult rGentle) = Valley(sideSlope: 0.3, riverM3PerS: 10.0);
        Assert.Empty(Dams.AddDams(gentle, rGentle.Z, rGentle, Cfg()).Dams);
        // 유량 100 m³/s 는 상한 60 m³/s 를 넘고, 0.5 m³/s 는 하한 1 m³/s 에 못 미칩니다.
        (CellGraph big, SolverResult rBig) = Valley(sideSlope: 0.8, riverM3PerS: 100.0);
        Assert.Empty(Dams.AddDams(big, rBig.Z, rBig, Cfg()).Dams);
        (CellGraph small, SolverResult rSmall) = Valley(sideSlope: 0.8, riverM3PerS: 0.5);
        Assert.Empty(Dams.AddDams(small, rSmall.Z, rSmall, Cfg()).Dams);
        // 설정에서 끄면 놓지 않고, 높이도 바뀌지 않습니다.
        (CellGraph g, SolverResult r) = Valley(sideSlope: 0.8, riverM3PerS: 10.0);
        (double[] zOff, bool[] damOff, List<object?> off) = Dams.AddDams(g, r.Z, r, Cfg(("dams.enabled", false)));
        Assert.Empty(off);
        Assert.Equal(r.Z, zOff);
        Assert.DoesNotContain(true, damOff);
        // exclude 로 강 칸을 모두 빼면 놓을 자리가 없습니다.
        bool[] exclude = Enumerable.Range(0, N * N).Select(c => c / N == ChannelRow).ToArray();
        Assert.Empty(Dams.AddDams(g, r.Z, r, Cfg(), exclude).Dams);
    }

    [Fact]
    public void DamsKeepTheirSpacingAndTheSameInputsGiveTheSameDams()
    {
        (CellGraph g, SolverResult r) = Valley(sideSlope: 0.8, riverM3PerS: 10.0);
        Config cfg = Cfg(("dams.spacing_m", 300.0), ("dams.min_height_m", 20.0), ("dams.max_height_m", 40.0));
        (double[] zA, _, List<object?> a) = Dams.AddDams(g, r.Z, r, cfg);
        (double[] zB, _, List<object?> b) = Dams.AddDams(g, r.Z, r, cfg);
        Assert.Equal(zA, zB);
        int[] cells = a.Select(d => (int)(long)((OrderedDictionary<string, object?>)d!)["cell"]!).ToArray();
        Assert.Equal(cells, b.Select(d => (int)(long)((OrderedDictionary<string, object?>)d!)["cell"]!).ToArray());
        // 길이 1 km 골짜기에 간격 300 m 면 둑이 둘 이상 들어가고, 서로 300 m 넘게 떨어집니다.
        Assert.True(cells.Length >= 2, $"둑이 {cells.Length}개뿐입니다");
        for (int i = 0; i < cells.Length; i++)
        {
            double h = (double)((OrderedDictionary<string, object?>)a[i]!)["height_m"]!;
            Assert.InRange(h, 20.0, 40.0);
            for (int j = i + 1; j < cells.Length; j++)
            {
                double d = Math.Abs(g.Pos[cells[i] * 3] - g.Pos[cells[j] * 3]);
                Assert.True(d >= 300.0, $"둑 {cells[i]} 과 {cells[j]} 사이가 {d:F0} m 입니다");
            }
        }
    }

    [Fact]
    public void PlanetGraphGetsNoDams()
    {
        CellGraph g = Graph.SphereGraph(4, 6.371e6);
        int n = g.NCells;
        long[] rcv = Enumerable.Range(0, n).Select(c => (long)c).ToArray();
        var r = new SolverResult
        {
            Z = new double[n],
            Receiver = rcv,
            Order = Routing.TopoOrder(rcv),
            Discharge = new double[n],
            DrainageArea = new double[n],
            SedimentFlux = new double[n],
            Slope = new double[n],
            KS = new double[n],
            SCritSurface = new double[n],
            Iterations = 1,
        };
        Assert.Empty(Dams.AddDams(g, r.Z, r, Cfg()).Dams);
    }

    [Fact]
    public void WrongSettingsAndLengthsAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, SolverResult r) = Valley(sideSlope: 0.8, riverM3PerS: 10.0);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Dams.AddDams(g, new double[5], r, Cfg()));
        Assert.Contains("5", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Dams.AddDams(g, r.Z, r, Cfg(("dams.slope", 0.0))));
        Assert.Contains("slope 0", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(
            () => Dams.AddDams(g, r.Z, r, Cfg(("dams.min_height_m", 50.0), ("dams.max_height_m", 40.0))));
    }
}
