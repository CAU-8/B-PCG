using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>
/// 산사태 둑 (docs/pipeline.md 7.4): 둑이 있을 기대 수를 솟는 속도와 골짜기 모양에서 계산하고, 둑 뒤를 강이 실어 온 흙으로 메웁니다.
/// </summary>
public sealed class DamsTests
{
    private const int N = 41;
    private const double Dx = 25.0;
    private const double ChannelSlope = 0.02;
    private const int ChannelRow = N / 2;
    private const double HillLength = 500.0;

    private static readonly DamSettings St = new(
        BlockingFraction: 0.02, PersistenceYr: 5000.0, MinHeightM: 10.0, MaxHeightM: 150.0, FaceSlope: 0.15, FillSlopeRatio: 0.5,
        MinSlopeRatio: 0.9, MinDrainageAreaM2: 1.0e5, Seed: 7);

    private static Config Cfg(params (string Key, object Value)[] overrides)
    {
        var all = new List<KeyValuePair<string, object?>>
        {
            new("dams.enabled", true),
            new("dams.blocking_fraction", 1.0),
            new("dams.persistence_yr", 5000.0),
            new("dams.min_height_m", 10.0),
            new("dams.max_height_m", 150.0),
            new("dams.slope", 0.15),
            new("dams.fill_slope_ratio", 0.5),
            new("dams.min_slope_ratio", 0.9),
            new("dams.min_drainage_area_m2", 1.0e5),
        };
        foreach ((string key, object value) in overrides)
        {
            all.RemoveAll(kv => kv.Key == key);
            all.Add(new(key, value));
        }
        return Config.LoadConfig("earth", "tiny").WithOverrides(all);
    }

    /// <summary>
    /// 서쪽으로 흐르는 V 자 골짜기 하나 (한 변 약 1 km). 가운데 행이 강이고 (경사 0.02), 양옆 비탈은 경사 sideSlope 로 강을 향해
    /// 내려옵니다. 강의 상류 넓이는 동쪽 끝에서 5 km² 이고, 서쪽으로 한 칸 갈 때마다 양옆 비탈(길이 500 m)의 넓이만큼 늘어납니다.
    /// 무너지는 경사 S_crit 은 0.8, 강이 나르는 흙은 sedimentM3PerYr 입니다.
    /// </summary>
    private static (CellGraph Graph, SolverResult Result, double[] Uplift, double[] Runoff) Valley(double sideSlope, double sedimentM3PerYr)
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        int n = N * N;
        double[] z = new double[n];
        long[] rcv = new long[n];
        double[] q = new double[n];
        double[] slope = new double[n];
        double[] sCrit = new double[n];
        double[] area = new double[n];
        double[] qs = new double[n];
        double[] uplift = new double[n];
        double[] runoff = new double[n];
        double y0 = g.Pos[(ChannelRow * N * 3) + 1];
        for (int c = 0; c < n; c++)
        {
            int row = c / N;
            int col = c % N;
            double x = g.Pos[c * 3];
            double y = g.Pos[(c * 3) + 1];
            z[c] = 100.0 + (ChannelSlope * x) + (sideSlope * Math.Abs(y - y0));
            sCrit[c] = 0.8;
            uplift[c] = 1.0e-3;
            runoff[c] = 1.0;
            if (row == ChannelRow)
            {
                rcv[c] = col == 0 ? c : c - 1;
                area[c] = 5.0e6 + ((N - col) * 2.0 * HillLength * Dx);
                slope[c] = ChannelSlope;
                qs[c] = sedimentM3PerYr;
            }
            else
            {
                rcv[c] = row < ChannelRow ? c + N : c - N;
                area[c] = Dx * Dx;
                slope[c] = sideSlope;
            }
            q[c] = area[c] * runoff[c];
        }
        var result = new SolverResult
        {
            Z = z,
            Receiver = rcv,
            Order = Routing.TopoOrder(rcv),
            Discharge = q,
            DrainageArea = area,
            SedimentFlux = qs,
            Slope = slope,
            KS = new double[n],
            SCritSurface = sCrit,
            Iterations = 1,
        };
        return (g, result, uplift, runoff);
    }

    private static bool[] Outlet(SolverResult r) => r.Receiver.Select((v, c) => v == c).ToArray();

    private static OrderedDictionary<string, object?> Site(object? o) => (OrderedDictionary<string, object?>)o!;

    [Fact]
    public void LandslideCountsTimesVolumesAddUpToTheRockThatLeft()
    {
        // 넓이가 a 이상인 산사태 수(나간 암석 1 m³ 에 대한 수)를 넓이 구간마다 빼서 그 구간의 부피를 곱해 모두 더하면 1 입니다.
        const double aMax = 2.5e5;
        const int bins = 4000;
        double total = 0.0;
        double ratio = Math.Pow(aMax / Dams.LandslideMinAreaM2, 1.0 / bins);
        double a = Dams.LandslideMinAreaM2;
        for (int k = 0; k < bins; k++)
        {
            double next = a * ratio;
            double count = Dams.NumberPerVolume(a, aMax) - Dams.NumberPerVolume(next, aMax);
            double mid = Math.Sqrt(a * next);
            total += count * Dams.VolumeAreaCoefficient * Math.Pow(mid, Dams.VolumeAreaExponent);
            a = next;
        }
        Assert.Equal(1.0, total, 3);
        // 큰 산사태일수록 드뭅니다. 그 비탈에서 날 수 있는 가장 큰 넓이 이상은 없습니다.
        Assert.True(Dams.NumberPerVolume(1.0e4, aMax) > Dams.NumberPerVolume(1.0e5, aMax));
        Assert.Equal(0.0, Dams.NumberPerVolume(aMax, aMax));
        Assert.Equal(0.0, Dams.NumberPerVolume(1.0e6, aMax));
        // 넓이가 10 배이면 수는 약 10^1.4 ≈ 25 분의 1 (가장 큰 넓이가 아주 클 때).
        double far = Dams.NumberPerVolume(1.0e4, 1.0e12) / Dams.NumberPerVolume(1.0e5, 1.0e12);
        Assert.Equal(Math.Pow(10.0, 1.4), far, 2);
        // 부피 ↔ 넓이: 0.146 × a^1.33 을 거꾸로 풀면 a.
        Assert.Equal(3.0e4, Dams.LandslideArea(Dams.VolumeAreaCoefficient * Math.Pow(3.0e4, Dams.VolumeAreaExponent)), 6);
    }

    [Fact]
    public void BigRiversBreakSmallDams()
    {
        // 상류 넓이 10 km², 높이 30 m, 부피 100만 m³: DBI = log10(10 × 30 ÷ 1) ≈ 2.477, 남을 확률 = 1 − (2.477 − 1.2) ÷ 3.8 ≈ 0.664.
        Assert.Equal(Math.Log10(300.0), Dams.Dbi(10.0e6, 30.0, 1.0e6), 12);
        Assert.Equal(1.0 - ((Math.Log10(300.0) - 1.2) / 3.8), Dams.Survival(10.0e6, 30.0, 1.0e6), 12);
        // 같은 둑이라도 강이 클수록 덜 남고, 둑이 클수록 더 남습니다. DBI 가 1.2 이하이면 1, 5.0 이상이면 0.
        Assert.True(Dams.Survival(100.0e6, 30.0, 1.0e6) < Dams.Survival(10.0e6, 30.0, 1.0e6));
        Assert.True(Dams.Survival(10.0e6, 30.0, 10.0e6) > Dams.Survival(10.0e6, 30.0, 1.0e6));
        Assert.Equal(1.0, Dams.Survival(0.1e6, 30.0, 1.0e6));
        Assert.Equal(0.0, Dams.Survival(1.0e10, 30.0, 1.0e5));
    }

    [Fact]
    public void ExpectedDamsScaleWithRockSupplyAndVanishOnSteepChannels()
    {
        double[] edges = Dams.HeightEdges(St);
        Assert.Equal(Dams.HeightBins + 1, edges.Length);
        Assert.Equal(10.0, edges[0], 12);
        Assert.Equal(150.0, edges[^1], 9);
        double[] w = new double[Dams.HeightBins];
        // 곧은 V 자 골짜기 (옆 비탈 0.8, 둑 비탈 0.15): 높이 10 m 에 2 × 10³ ÷ (3 × 0.15 × 0.8) ≈ 5,556 m³ 이 들고, 부피는 높이의 세제곱에 비례합니다.
        double v0 = Dams.DamVolume(10.0, 0.15, 0.8);
        Assert.Equal(2000.0 / 0.36, v0, 9);
        double m1 = Dams.ExpectedDams(25.0, 2.5e5, v0, 10.0, 3.0, 0.02, 5.0e6, Dx, St, edges, w);
        Assert.True(m1 > 0.0);
        Assert.Equal(m1, w.Sum(), 12);
        // 낮은 둑이 높은 둑보다 흔합니다 (작은 산사태가 흔하기 때문).
        Assert.True(w[0] > w[4] && w[4] > 0.0);
        // 산비탈에서 나오는 암석이 2 배이면 기대 수도 2 배, 막는 몫이 절반이면 절반입니다.
        double m2 = Dams.ExpectedDams(50.0, 2.5e5, v0, 10.0, 3.0, 0.02, 5.0e6, Dx, St, edges, w);
        Assert.Equal(2.0 * m1, m2, 12);
        double mHalf = Dams.ExpectedDams(25.0, 2.5e5, v0, 10.0, 3.0, 0.02, 5.0e6, Dx, St with { BlockingFraction = 0.01 }, edges, w);
        Assert.Equal(0.5 * m1, mHalf, 12);
        // 강이 클수록 둑이 덜 남습니다.
        double mBig = Dams.ExpectedDams(25.0, 2.5e5, v0, 10.0, 3.0, 0.02, 500.0e6, Dx, St, edges, w);
        Assert.True(mBig < m1);
        // 강 경사 1.0 에서는 물이 고이는 길이(0.75 × 높이 ÷ 경사)가 칸 4개(100 m)가 되려면 둑이 133 m 를 넘어야 합니다.
        // 이 비탈의 가장 큰 산사태(25만 m², 약 220만 m³)는 이 골짜기를 약 73 m 까지만 막으므로 기대 수가 0 입니다.
        double mSteep = Dams.ExpectedDams(25.0, 2.5e5, v0, 10.0, 3.0, 1.0, 5.0e6, Dx, St, edges, w);
        Assert.Equal(0.0, mSteep);
    }

    [Fact]
    public void MoundVolumeOnTheGridIsCloseToTheStraightValleyFormula()
    {
        (CellGraph g, SolverResult r, _, _) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        int a = (ChannelRow * N) + 20;
        var cells = new List<(int Cell, double Top)>();
        double v = Dams.MoundVolume(g, r.Z, r.Receiver, a, 30.0, 0.15, cells);
        // 식의 값은 2 × 30³ ÷ (3 × 0.15 × 0.8) = 150,000 m³. 격자는 25 m 칸이고 강이 기울어 있어 20% 안에서 맞습니다.
        Assert.InRange(v, 0.8 * 150_000.0, 1.2 * 150_000.0);
        Assert.Contains(cells, t => t.Cell == a && Math.Abs(t.Top - (r.Z[a] + 30.0)) < 1e-9);
        Assert.All(cells, t => Assert.True(t.Top > r.Z[t.Cell]));
        Assert.Equal(v, cells.Sum(t => (t.Top - r.Z[t.Cell]) * g.Area[t.Cell]), 6);
    }

    [Fact]
    public void YoungDamKeepsALakeAndAnOldDamLeavesAFlat()
    {
        // 강이 흙을 전혀 나르지 않으면 둑 뒤는 메워지지 않아 호수가 남습니다.
        (CellGraph g, SolverResult dry, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(dry);
        DamResult lakeCase = Dams.Apply(g, dry.Z, dry, u, run, outlet, Cfg());
        Assert.NotEmpty(lakeCase.Sites);
        Assert.True((long)lakeCase.Diag["n_lakes"]! >= 1);
        double[] filled = Depressions.FillDepressions(lakeCase.Z, g.Nbr, outlet);
        Assert.Contains(Enumerable.Range(0, filled.Length), c => filled[c] - lakeCase.Z[c] > 1.0);
        foreach (object? o in lakeCase.Sites)
        {
            OrderedDictionary<string, object?> s = Site(o);
            int a = (int)(long)s["cell"]!;
            // 둑은 강 위에 놓이고, 높이는 설정한 범위 안이며, 둑 자리의 강바닥이 그 높이만큼 솟습니다.
            Assert.Equal(ChannelRow, a / N);
            double h = (double)s["height_m"]!;
            Assert.InRange(h, 10.0, 150.0);
            Assert.Equal(dry.Z[a] + h, lakeCase.Z[a], 9);
            Assert.InRange((double)s["age_yr"]!, 0.0, 5000.0);
            Assert.True(lakeCase.Dam[a]);
        }
        // 둑과 메운 흙은 땅을 올리기만 합니다.
        Assert.All(Enumerable.Range(0, dry.Z.Length), c => Assert.True(lakeCase.Z[c] >= dry.Z[c]));

        // 강이 흙을 아주 많이 나르면 같은 둑 뒤가 다 메워져 평지가 되고, 물이 고이는 칸이 없습니다.
        (_, SolverResult wet, _, _) = Valley(sideSlope: 0.8, sedimentM3PerYr: 1.0e12);
        DamResult flatCase = Dams.Apply(g, wet.Z, wet, u, run, outlet, Cfg());
        Assert.Equal(lakeCase.Sites.Count, flatCase.Sites.Count);
        Assert.Equal(0L, (long)flatCase.Diag["n_lakes"]!);
        Assert.All(flatCase.Sites, o => Assert.Equal(1.0, (double)Site(o)["filled_fraction"]!));
        double[] after = Depressions.FillDepressions(flatCase.Z, g.Nbr, outlet);
        Assert.All(Enumerable.Range(0, after.Length), c => Assert.True(after[c] - flatCase.Z[c] <= 1e-9));
        Assert.Contains(true, flatCase.Fill);
        // 메운 바닥은 강을 따라 상류로 갈수록 높아집니다. 경사는 강바닥 경사 0.02 의 절반이라, 이웃한 두 강 칸 사이에서
        // 0.01 × 25 m = 0.25 m 입니다.
        int[] fillOnRiver = Enumerable.Range(0, N * N).Where(c => flatCase.Fill[c] && c / N == ChannelRow).ToArray();
        Assert.True(fillOnRiver.Length >= 2);
        for (int k = 1; k < fillOnRiver.Length; k++)
        {
            if (fillOnRiver[k] == fillOnRiver[k - 1] + 1)
            {
                Assert.Equal(0.5 * ChannelSlope * Dx, flatCase.Z[fillOnRiver[k]] - flatCase.Z[fillOnRiver[k - 1]], 6);
            }
        }
        // 같은 입력이면 결과가 같습니다.
        DamResult again = Dams.Apply(g, wet.Z, wet, u, run, outlet, Cfg());
        Assert.Equal(flatCase.Z, again.Z);
    }

    [Fact]
    public void FilledFloorLiesAtHalfTheChannelSlopeAndRisesTwiceAsFastBesideTheChannel()
    {
        (_, SolverResult r, _, _) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        double[] s = Dams.FillSlopes(r.Receiver, r.DrainageArea, r.Slope, St);
        int river = (ChannelRow * N) + 10;
        Assert.Equal(0.5 * ChannelSlope, s[river], 12);
        // 강 옆 칸과 그 위 비탈 칸은 물이 흘러드는 강 칸의 값의 두 배입니다 (자기 비탈의 경사 0.8 과 상관없습니다).
        Assert.Equal(2.0 * 0.5 * ChannelSlope, s[river + N], 12);
        Assert.Equal(2.0 * 0.5 * ChannelSlope, s[river - (5 * N)], 12);
        // 강바닥이 평평한 칸에도 가장 작은 경사가 남습니다.
        double[] f = Dams.FillSlopes(r.Receiver, r.DrainageArea, new double[r.Slope.Length], St);
        Assert.Equal(Dams.MinFillSlope, f[river], 12);
        Assert.Equal(2.0 * Dams.MinFillSlope, f[river + N], 12);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Dams.FillSlopes(r.Receiver, new double[3], r.Slope, St));
        Assert.Contains("areaM2 3", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDamOnGentleSlopesOrWhenTurnedOffOrOnThePlanet()
    {
        // 옆 비탈 경사 0.3 은 무너지는 경사 0.8 의 0.9 배(0.72)에 못 미쳐 산사태가 나지 않습니다.
        (CellGraph g, SolverResult gentle, double[] u, double[] run) = Valley(sideSlope: 0.3, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(gentle);
        DamResult none = Dams.Apply(g, gentle.Z, gentle, u, run, outlet, Cfg());
        Assert.Empty(none.Sites);
        Assert.Equal(0L, (long)none.Diag["n_candidates"]!);
        Assert.Equal(gentle.Z, none.Z);
        // 막는 몫이 0 이면 기대 수가 0 이라 둑이 없고, 설정에서 끄면 계산하지 않습니다.
        (_, SolverResult r, _, _) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        DamResult zero = Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.blocking_fraction", 0.0)));
        Assert.Empty(zero.Sites);
        Assert.Equal(0.0, (double)zero.Diag["expected_dams"]!);
        DamResult off = Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.enabled", false)));
        Assert.False((bool)off.Diag["applied"]!);
        Assert.Equal(r.Z, off.Z);
        // 땅이 솟지 않으면 산사태로 나올 암석이 없어 둑도 없습니다.
        DamResult still = Dams.Apply(g, r.Z, r, new double[u.Length], run, outlet, Cfg());
        Assert.Empty(still.Sites);
        // 행성 지도(구면 그래프)에는 둑을 놓지 않습니다.
        CellGraph sphere = Graph.SphereGraph(4, 6.371e6);
        int n = sphere.NCells;
        long[] self = Enumerable.Range(0, n).Select(c => (long)c).ToArray();
        var flatResult = new SolverResult
        {
            Z = new double[n],
            Receiver = self,
            Order = Routing.TopoOrder(self),
            Discharge = new double[n],
            DrainageArea = new double[n],
            SedimentFlux = new double[n],
            Slope = new double[n],
            KS = new double[n],
            SCritSurface = new double[n],
            Iterations = 1,
        };
        DamResult planet = Dams.Apply(sphere, flatResult.Z, flatResult, new double[n], new double[n], Enumerable.Repeat(true, n).ToArray(), Cfg());
        Assert.False((bool)planet.Diag["applied"]!);
    }

    [Fact]
    public void WrongSettingsAndLengthsAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, SolverResult r, double[] u, double[] run) = Valley(sideSlope: 0.8, sedimentM3PerYr: 0.0);
        bool[] outlet = Outlet(r);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Dams.Apply(g, new double[5], r, u, run, outlet, Cfg()));
        Assert.Contains("z 5", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.slope", 0.0))));
        Assert.Contains("slope 0", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(
            () => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.min_height_m", 50.0), ("dams.max_height_m", 40.0))));
        Assert.Throws<ArgumentException>(() => Dams.Apply(g, r.Z, r, u, run, outlet, Cfg(("dams.blocking_fraction", 1.5))));
    }
}
