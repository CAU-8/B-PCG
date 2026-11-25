using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>
/// 분지가 여럿인 땅의 호수 물수지 (docs/pipeline.md 8.1). LakeBalanceTests 는 분지 하나에서 잠기는 칸 수를 정확히 보고,
/// 이 시험은 분지 사이에서 물이 오가는 성질을 봅니다. 넘치지 못하는 호수는 들어오는 물로 증발을 감당할 수 있는 낮은 칸까지만
/// 잠기고, 위 분지의 물은 위 분지가 넘칠 때만 아래 분지로 갑니다.
/// </summary>
public sealed class LakeBasinsTests
{
    private const double Dx = 1000.0;   // 칸 넓이 10⁶ m²
    private const double RouteEps = 1e-3;
    private const int Rows = 15;
    private const int Cols = 41;
    private const int CentreD = (7 * Cols) + 12;
    private const int CentreU = (7 * Cols) + 30;

    /// <summary>넘치지 못하는 호수나 마른 분지 하나: 물길이 끝나는 칸의 묶음과, 그 묶음으로 실제로 흘러드는 물 [m³/yr].</summary>
    private sealed record Lake(List<int> Cells, double InflowM3PerYr, bool Closed);

    private static double[] Fill(int n, double v) => Enumerable.Repeat(v, n).ToArray();

    /// <summary>
    /// 깔때기 두 개가 동서로 이어진 땅 (15 × 41칸). 땅 높이는 min(10 + D 까지 거리, 20 + U 까지 거리) [m] 이고 거리는 칸 수입니다.
    /// 아래 깔때기 D 는 (7, 12) 칸이 가장 낮고(10 m), 위 깔때기 U 는 (7, 30) 칸이 가장 낮습니다(20 m).
    /// U 에 고인 물은 두 깔때기 사이의 고개 (7, 26)(24 m)를 넘어 D 로 가고, D 의 물은 서쪽 끝 출구 (7, 0)(0 m) 옆 칸(21 m)을 넘어 나갑니다.
    /// </summary>
    private static (CellGraph Graph, double[] Z, bool[] Outlet) TwoFunnels()
    {
        CellGraph g = Graph.FlatGraph(Rows, Cols, Dx);
        int n = Rows * Cols;
        double[] z = new double[n];
        bool[] outlet = new bool[n];
        for (int c = 0; c < n; c++)
        {
            double dr = (c / Cols) - 7.0;
            double toD = Math.Sqrt((dr * dr) + (((c % Cols) - 12.0) * ((c % Cols) - 12.0)));
            double toU = Math.Sqrt((dr * dr) + (((c % Cols) - 30.0) * ((c % Cols) - 30.0)));
            z[c] = Math.Min(10.0 + toD, 20.0 + toU);
        }
        z[7 * Cols] = 0.0;
        outlet[7 * Cols] = true;
        return (g, z, outlet);
    }

    /// <summary>pick 이 참인 칸을 서로 이웃한 묶음으로 나눕니다. 반환: 칸마다 묶음 번호(고르지 않은 칸은 −1)와 묶음 목록.</summary>
    private static (int[] Label, List<List<int>> Groups) Components(CellGraph g, Func<int, bool> pick)
    {
        int n = g.NCells;
        int[] label = Enumerable.Repeat(-1, n).ToArray();
        var groups = new List<List<int>>();
        var stack = new Stack<int>();
        for (int c0 = 0; c0 < n; c0++)
        {
            if (label[c0] >= 0 || !pick(c0))
            {
                continue;
            }
            var cells = new List<int>();
            label[c0] = groups.Count;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                cells.Add(c);
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && label[v] < 0 && pick(v))
                    {
                        label[v] = groups.Count;
                        stack.Push(v);
                    }
                }
            }
            groups.Add(cells);
        }
        return (label, groups);
    }

    /// <summary>
    /// 물수지가 맞는지 봅니다. 물길을 출구와 물길이 끝나는 칸(Sink)에서 끝나게 LakeBalance.Solve 와 같은 규칙(채운 지표에서 가장
    /// 가파른 이웃)으로 다시 정하고, 유출을 모아 칸마다 흘러드는 물을 셉니다. 그다음 세 가지를 봅니다.
    /// 1. 물이 고인 묶음(넘치지 못하는 호수): 잠긴 칸이 수면에서 더 쓰는 물 ≤ 흘러드는 물 &lt; 그 물 + 바로 옆에서 다음으로 낮은 칸이
    ///    더 쓰는 물. 곧 들어오는 물로 감당할 수 있는 만큼만 잠급니다. 수면은 잠긴 칸의 바닥보다 높고 다음 칸의 바닥보다 높지 않습니다.
    /// 2. 한 칸도 잠그지 못한 마른 분지: 가장 낮은 칸 하나에서 물길이 끝나고, 흘러드는 물이 그 칸을 잠그는 데 드는 물보다 적습니다.
    /// 3. 물길이 끝나는 칸을 출구로 보고 다시 채웠을 때 남는 웅덩이는 모두 넘치는 호수입니다: 들어오는 물 ≥ 웅덩이를 다 잠그는 데 드는 물.
    /// 반환: 넘치지 못하는 호수와 마른 분지의 목록.
    /// </summary>
    private static List<Lake> AssertBalanced(
        CellGraph g, double[] z, bool[] outlet, double runoff, double precip, double pet, double[]? inflow, LakeBalanceResult r)
    {
        int n = g.NCells;
        double[] cost = new double[n];
        double[] source = new double[n];
        bool[] end = new bool[n];
        for (int c = 0; c < n; c++)
        {
            cost[c] = Math.Max(pet - Math.Max(precip - runoff, 0.0), 0.0) * g.Area[c];
            source[c] = (runoff * g.Area[c]) + (inflow is null ? 0.0 : inflow[c]);
            end[c] = outlet[c] || r.Sink[c];
        }
        double[] zt = Depressions.FillEpsilon(z, g.Nbr, end, RouteEps);
        long[] rcv = Routing.D8Receivers(zt, g.Nbr, g.Dist, end).Rcv;
        double[] q = AccumulateModule.Accumulate(rcv, Routing.TopoOrder(rcv), source);

        var lakes = new List<Lake>();
        foreach (List<int> cells in Components(g, c => r.Sink[c]).Groups)
        {
            double w = cells.Sum(c => q[c]);
            double spent = cells.Sum(c => cost[c]);
            bool closed = cells.Any(c => r.Closed[c]);
            // 바로 옆에서 다음으로 잠길 칸: 묶음에 붙은 칸 가운데 출구도 물길이 끝나는 칸도 아닌 가장 낮은 칸.
            int next = -1;
            foreach (int c in cells)
            {
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && !end[v] && (next < 0 || z[v] < z[next] || (z[v] == z[next] && v < next)))
                    {
                        next = v;
                    }
                }
            }
            if (closed || w >= spent)
            {
                Assert.True(
                    spent <= w * (1.0 + 1e-9),
                    $"잠긴 칸 {cells.Count}개가 수면에서 한 해 {spent:E4} m³ 를 더 증발시키는데 흘러드는 물은 {w:E4} m³ 뿐입니다 (칸 {cells[0]} 의 묶음)");
                Assert.True(next >= 0, $"칸 {cells[0]} 의 묶음 둘레에 다음으로 잠길 칸이 없습니다");
                Assert.True(
                    w - spent < cost[next],
                    $"칸 {cells[0]} 의 묶음: 물이 한 해 {w - spent:E4} m³ 남는데 다음 칸 {next} (드는 물 {cost[next]:E4} m³)을 잠그지 않았습니다");
            }
            else
            {
                Assert.True(cells.Count == 1, $"마른 분지인데 물길이 끝나는 칸이 {cells.Count}개입니다 (칸 {cells[0]} 의 묶음)");
            }
            foreach (int c in cells.Where(c => r.Closed[c]))
            {
                Assert.True(r.Level[c] > z[c], $"넘치지 못하는 호수 칸 {c} 의 수면이 바닥보다 높지 않습니다");
                Assert.True(r.Level[c] <= z[next] + 1e-9, $"넘치지 못하는 호수 칸 {c} 의 수면이 잠기지 않은 옆 칸 {next} 의 바닥보다 높습니다");
            }
            lakes.Add(new Lake(cells, w, closed));
        }

        double[] zHat = Depressions.FillDepressions(z, g.Nbr, end);
        (int[] pool, List<List<int>> pools) = Components(g, c => !end[c] && zHat[c] - z[c] > LakeBalance.PondedM);
        for (int j = 0; j < pools.Count; j++)
        {
            double wIn = pools[j].Sum(c => source[c]);
            for (int d = 0; d < n; d++)
            {
                if (pool[d] != j && rcv[d] != d && pool[(int)rcv[d]] == j)
                {
                    wIn += q[d];
                }
            }
            double full = pools[j].Sum(c => cost[c]);
            Assert.True(wIn >= full * (1.0 - 1e-9), $"넘친다고 남긴 웅덩이(칸 {pools[j][0]} 의 묶음)에 들어오는 물 {wIn:E4} m³ 가 다 잠그는 데 드는 {full:E4} m³ 보다 적습니다");
        }
        Assert.Equal(pools.Count, r.NOpen);
        Assert.Equal(lakes.Count(l => l.Closed), r.NClosed);
        Assert.Equal(lakes.Count(l => !l.Closed), r.NDry);
        return lakes;
    }

    [Fact]
    public void ALakeGrowsWithItsInflowAndTheUpperBasinFeedsTheLowerOnlyWhenItOverflows()
    {
        (CellGraph g, double[] z, bool[] outlet) = TwoFunnels();
        int n = g.NCells;
        // 강수 0.5 m/yr, 잠재 증발산 1.5 m/yr 인 마른 땅에서 유출을 늘려 갑니다. 잠긴 칸은 한 해 (1 + 유출) × 10⁶ m³ 를 더 씁니다.
        // 위 분지 U 는 유출이 0.25 와 0.3 m/yr 사이에서 넘치기 시작하고, 아래 분지 D 는 이 범위에서 늘 넘치지 못합니다.
        // U 가 넘치지 못하는 동안 D 로 물이 모이는 넓이는 403 km² 로 그대로이고, U 가 넘치면 U 로 모이던 206 km² 의 물이 더해집니다.
        int prevD = 0;
        int prevU = 0;
        double sharedD = 0.0;
        bool uOpened = false;
        foreach (double runoff in new[] { 0.02, 0.05, 0.1, 0.2, 0.3, 0.4, 0.5 })
        {
            LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(n, runoff), Fill(n, 0.5), Fill(n, 1.5), null, RouteEps);
            List<Lake> lakes = AssertBalanced(g, z, outlet, runoff, 0.5, 1.5, null, r);
            Lake d = lakes.Single(l => l.Cells.Contains(CentreD));
            Lake? u = lakes.SingleOrDefault(l => l.Cells.Contains(CentreU));
            Assert.True(d.Closed);
            // 물이 늘면 넘치지 못하는 호수는 줄지 않습니다.
            Assert.True(d.Cells.Count >= prevD, $"유출 {runoff} m/yr: D 의 잠긴 칸이 {prevD}개에서 {d.Cells.Count}개로 줄었습니다");
            prevD = d.Cells.Count;
            // D 로 물이 모이는 넓이 (흘러드는 물 ÷ 유출) [m²].
            double catchD = d.InflowM3PerYr / runoff;
            if (u is not null)
            {
                Assert.False(uOpened, $"유출 {runoff} m/yr: 넘치던 U 가 물이 늘었는데 다시 닫혔습니다");
                Assert.True(u.Closed && u.Cells.Count >= prevU);
                prevU = u.Cells.Count;
                // U 가 넘치지 못하는 동안 D 로 모이는 넓이는 그대로입니다 (U 의 물은 D 로 가지 않음).
                Assert.True(sharedD == 0.0 || Math.Abs(catchD - sharedD) <= 1e-6 * sharedD, $"U 가 닫혀 있는데 D 로 모이는 넓이가 {sharedD} 에서 {catchD} m² 로 바뀌었습니다");
                sharedD = catchD;
            }
            else
            {
                // U 가 넘치면 U 에 모이던 물이 D 로 가서, D 로 물이 모이는 넓이가 커집니다.
                Assert.True(catchD > sharedD * 1.1, $"유출 {runoff} m/yr: U 가 넘치는데 D 로 모이는 넓이가 {sharedD} 에서 {catchD} m² 로만 바뀌었습니다");
                uOpened = true;
            }
        }
        Assert.True(uOpened, "유출 0.5 m/yr 까지 늘려도 위 분지가 넘치지 않았습니다");
    }

    [Fact]
    public void InAWetClimateBothBasinsOverflowAndInADesertBothStayDry()
    {
        (CellGraph g, double[] z, bool[] outlet) = TwoFunnels();
        int n = g.NCells;
        // 유출 0.5, 강수 1.0, 잠재 증발산 0.6 m/yr: 잠긴 칸은 한 해 10⁵ m³ 만 더 써서 두 분지 모두 넘칩니다.
        LakeBalanceResult wet = LakeBalance.Solve(g, z, outlet, Fill(n, 0.5), Fill(n, 1.0), Fill(n, 0.6), null, RouteEps);
        Assert.Empty(AssertBalanced(g, z, outlet, 0.5, 1.0, 0.6, null, wet));
        Assert.Equal(2, wet.NOpen);
        // 유출 0.001 m/yr: 어느 분지로 들어오는 물도 가장 낮은 칸 하나를 잠그는 데 드는 약 1.8 × 10⁶ m³ 에 못 미칩니다.
        // 물길은 두 분지의 가장 낮은 칸에서 끝납니다.
        LakeBalanceResult dry = LakeBalance.Solve(g, z, outlet, Fill(n, 0.001), Fill(n, 0.2), Fill(n, 2.0), null, RouteEps);
        List<Lake> lakes = AssertBalanced(g, z, outlet, 0.001, 0.2, 2.0, null, dry);
        Assert.Equal(2, dry.NDry);
        Assert.Equal([CentreD, CentreU], Enumerable.Range(0, n).Where(c => dry.Sink[c]));
        Assert.All(lakes, l => Assert.False(l.Closed));
    }

    /// <summary>
    /// 넘치지 못하는 호수의 비탈 위(호수 수면보다 높은 곳)에 작은 웅덩이가 있으면, 그 웅덩이로 모이는 물은 호수까지 오지 못합니다.
    /// 지금의 LakeBalance.Solve 는 첫 계산에서 그 물까지 세어 호수 넓이를 정하고, 다음 계산에서 웅덩이를 따로 닫습니다.
    /// 그래서 호수가 들어오는 물보다 많이 증발시킵니다 (docs/pipeline.md 14장 '한 분지에서 증발이 들어오는 물보다 많을 수 있습니다').
    /// 이 결함을 고치면 Skip 을 지웁니다.
    /// </summary>
    [Fact(Skip = "알려진 결함: 다음 번 계산이 닫는 안쪽 웅덩이의 물을 첫 번 계산이 이미 호수 넓이에 썼습니다 (docs/pipeline.md 14장, 호수 물수지)")]
    public void APitAboveAClosedLakeDoesNotSpendTheSameWaterTwice()
    {
        // 가운데가 가장 낮은 깔때기 (21 × 21칸, 가운데 10 m, 한 칸 멀어질 때마다 1 m 높음, 출구는 왼쪽 위 모서리 칸 하나).
        // 마른 기후에서 호수는 가운데 둘레 39칸을 잠그고 수면이 약 13.6 m 입니다 (LakeBalanceTests 와 같은 땅).
        // 가운데에서 동쪽으로 8칸 떨어진 세로 세 칸(약 18 m)을 16.5 m 로 낮춰, 둘레(가장 낮은 곳 17 m)보다 0.5 m 낮은 웅덩이를 만듭니다.
        // 웅덩이는 호수 수면보다 높아 처음 계산에서는 잠기지 않습니다. 웅덩이로 모이는 물(웅덩이 세 칸을 합한 열여섯 칸의 유출, 한 해 1.6 × 10⁶ m³)은
        // 세 칸을 잠그는 데 드는 3.3 × 10⁶ m³ 에 못 미쳐 웅덩이에서 끝나고, 호수에는 오지 못합니다. 호수가 감당하던 여유는
        // 4.38 × 10⁷ − 4.29 × 10⁷ = 9 × 10⁵ m³ 뿐이라, 웅덩이가 가둔 물을 빼면 호수 39칸의 증발(4.29 × 10⁷ m³)이 들어오는 물
        // (4.22 × 10⁷ m³)보다 많아집니다.
        const int n21 = 21;
        CellGraph g = Graph.FlatGraph(n21, n21, Dx);
        int n = n21 * n21;
        double[] z = new double[n];
        bool[] outlet = new bool[n];
        for (int c = 0; c < n; c++)
        {
            double dr = (c / n21) - 10;
            double dc = (c % n21) - 10;
            z[c] = 10.0 + Math.Sqrt((dr * dr) + (dc * dc));
        }
        z[0] = 0.0;
        outlet[0] = true;
        int[] pit = [(9 * n21) + 18, (10 * n21) + 18, (11 * n21) + 18];
        foreach (int c in pit)
        {
            z[c] = 16.5;
        }
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(n, 0.1), Fill(n, 0.5), Fill(n, 1.5), null, RouteEps);
        Assert.True(pit.Any(c => r.Sink[c]), "웅덩이에서 물길이 끝나지 않아 이 시험이 결함을 보지 못합니다");
        AssertBalanced(g, z, outlet, 0.1, 0.5, 1.5, null, r);
    }
}
