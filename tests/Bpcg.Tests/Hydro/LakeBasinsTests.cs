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
/// 잠기고, 위 분지의 물은 위 분지가 넘칠 때만 아래 분지로 갑니다. 그때도 위 분지의 수면이 증발시키고 남은 물만 갑니다.
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

    /// <summary>넘치는 호수 하나: 잠긴 칸, 들어오는 물 [m³/yr], 잠긴 칸이 더 증발시키고 남아 턱을 넘는 물 [m³/yr].</summary>
    private sealed record Pool(List<int> Cells, double InflowM3PerYr, double OverflowM3PerYr);

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
    /// 가파른 이웃)으로 다시 정하고, 유출을 모아 칸마다 흘러드는 물을 셉니다. 물길이 끝나는 칸을 출구로 보고 다시 채웠을 때 남는
    /// 웅덩이는 넘치는 호수입니다. 넘치는 호수는 잠긴 칸이 더 증발시키는 물을 쓰고 남은 물만 턱 너머로 보내므로, 물을 모을 때 그 몫을
    /// 뺍니다. 그다음 세 가지를 봅니다.
    /// 1. 물이 고인 묶음(넘치지 못하는 호수): 잠긴 칸이 수면에서 더 쓰는 물 ≤ 흘러드는 물. 남는 물은 물가 칸과 그 너머 다음으로 낮은
    ///    칸을 모두 잠글 만큼이 못 됩니다. 곧 들어오는 물로 감당할 수 있는 만큼만 잠급니다. 물가 칸은 호수에 닿은 칸 가운데 바닥이
    ///    수면 높이에 걸친 칸입니다. 물 깊이가 1 mm(LakeBalance.PondedM) 이하라 호수로 세지 않고, 물길도 거기서 끝나지 않습니다.
    ///    호수가 그 칸을 잠그는 데 물을 썼는지는 지도에 남지 않아, 물가 칸 몫은 남는 물의 위 한계에만 넣습니다.
    ///    수면은 잠긴 칸의 바닥보다 높고, 호수에 닿은 잠기지 않은 칸의 바닥보다 1 mm 넘게 높지 않습니다.
    /// 2. 한 칸도 잠그지 못한 마른 분지: 가장 낮은 칸 하나에서 물길이 끝나고, 흘러드는 물이 그 칸을 잠그는 데 드는 물보다 적습니다.
    /// 3. 넘치는 호수: 들어오는 물 ≥ 웅덩이를 다 잠그는 데 드는 물.
    /// 반환: 넘치지 못하는 호수와 마른 분지의 목록, 넘치는 호수의 목록.
    /// </summary>
    private static (List<Lake> Lakes, List<Pool> Pools) AssertBalanced(
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
        // 넘치는 호수의 칸은 땅일 때보다 cost 만큼 더 증발시킵니다. 그 몫을 빼고 모으면, 웅덩이를 나가는 물이 넘친 물입니다.
        double[] zHat = Depressions.FillDepressions(z, g.Nbr, end);
        (int[] pool, List<List<int>> poolCells) = Components(g, c => !end[c] && zHat[c] - z[c] > LakeBalance.PondedM);
        double[] net = new double[n];
        for (int c = 0; c < n; c++)
        {
            net[c] = source[c] - (pool[c] >= 0 ? cost[c] : 0.0);
        }
        double[] zt = Depressions.FillEpsilon(z, g.Nbr, end, RouteEps);
        long[] rcv = Routing.D8Receivers(zt, g.Nbr, g.Dist, end).Rcv;
        double[] q = AccumulateModule.Accumulate(rcv, Routing.TopoOrder(rcv), net);

        var lakes = new List<Lake>();
        foreach (List<int> cells in Components(g, c => r.Sink[c]).Groups)
        {
            double w = cells.Sum(c => q[c]);
            double spent = cells.Sum(c => cost[c]);
            bool closed = cells.Any(c => r.Closed[c]);
            lakes.Add(new Lake(cells, w, closed));
            if (!closed && w < spent)
            {
                Assert.True(cells.Count == 1, $"마른 분지인데 물길이 끝나는 칸이 {cells.Count}개입니다 (칸 {cells[0]} 의 묶음)");
                continue;
            }
            // 수면: 넘치지 못하는 호수의 수면. 물이 가장 낮은 칸을 잠글 만큼 왔어도 물 깊이가 1 mm 에 못 미쳐 마른 분지로 센 묶음은 그 바닥.
            double h = closed ? r.Level[cells.First(c => r.Closed[c])] : cells.Max(c => z[c]);
            // 물가 칸: 묶음에서 바닥이 수면 높이 이하인 칸만 거쳐 닿는, 바닥이 수면 높이 이하인 칸.
            bool[] inShore = new bool[n];
            var shore = new List<int>();
            var stack = new Stack<int>(cells);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && !end[v] && !inShore[v] && z[v] <= h)
                    {
                        inShore[v] = true;
                        shore.Add(v);
                        stack.Push(v);
                    }
                }
            }
            // 다음으로 잠길 칸: 묶음과 물가 칸에 붙은 칸 가운데 출구도, 물길이 끝나는 칸도, 물가 칸도 아닌 가장 낮은 칸.
            int next = -1;
            foreach (int c in cells.Concat(shore))
            {
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && !end[v] && !inShore[v] && (next < 0 || z[v] < z[next] || (z[v] == z[next] && v < next)))
                    {
                        next = v;
                    }
                }
            }
            double shoreCost = shore.Sum(c => cost[c]);
            Assert.True(
                spent <= w * (1.0 + 1e-9),
                $"잠긴 칸 {cells.Count}개가 수면에서 한 해 {spent:E4} m³ 를 더 증발시키는데 흘러드는 물은 {w:E4} m³ 뿐입니다 (칸 {cells[0]} 의 묶음)");
            Assert.True(next >= 0, $"칸 {cells[0]} 의 묶음 둘레에 다음으로 잠길 칸이 없습니다");
            Assert.True(
                w - spent < shoreCost + cost[next],
                $"칸 {cells[0]} 의 묶음: 물이 한 해 {w - spent:E4} m³ 남는데 물가 칸 {shore.Count}개(드는 물 {shoreCost:E4} m³)와 "
                + $"다음 칸 {next} (드는 물 {cost[next]:E4} m³)을 잠그지 않았습니다");
            foreach (int c in shore)
            {
                Assert.True(h - z[c] <= LakeBalance.PondedM, $"칸 {c} 의 바닥이 칸 {cells[0]} 의 묶음 수면보다 1 mm 넘게 낮은데 잠기지 않았습니다");
            }
            foreach (int c in cells.Where(c => r.Closed[c]))
            {
                Assert.True(r.Level[c] > z[c], $"넘치지 못하는 호수 칸 {c} 의 수면이 바닥보다 높지 않습니다");
                Assert.True(r.Level[c] == h, $"넘치지 못하는 호수 칸 {c} 의 수면 {r.Level[c]} m 가 같은 호수의 수면 {h} m 와 다릅니다");
            }
        }

        var pools = new List<Pool>();
        for (int j = 0; j < poolCells.Count; j++)
        {
            double wIn = poolCells[j].Sum(c => source[c]);
            for (int d = 0; d < n; d++)
            {
                if (pool[d] != j && rcv[d] != d && pool[(int)rcv[d]] == j)
                {
                    wIn += q[d];
                }
            }
            double full = poolCells[j].Sum(c => cost[c]);
            Assert.True(wIn >= full * (1.0 - 1e-9), $"넘친다고 남긴 웅덩이(칸 {poolCells[j][0]} 의 묶음)에 들어오는 물 {wIn:E4} m³ 가 다 잠그는 데 드는 {full:E4} m³ 보다 적습니다");
            pools.Add(new Pool(poolCells[j], wIn, wIn - full));
        }
        Assert.Equal(pools.Count, r.NOpen);
        Assert.Equal(lakes.Count(l => l.Closed), r.NClosed);
        Assert.Equal(lakes.Count(l => !l.Closed), r.NDry);
        return (lakes, pools);
    }

    [Fact]
    public void ALakeGrowsWithItsInflowAndTheUpperBasinFeedsTheLowerOnlyWhenItOverflows()
    {
        (CellGraph g, double[] z, bool[] outlet) = TwoFunnels();
        int n = g.NCells;
        // 강수 0.5 m/yr, 잠재 증발산 1.5 m/yr 인 마른 땅에서 유출을 늘려 갑니다. 잠긴 칸은 한 해 (1 + 유출) × 10⁶ m³ 를 더 씁니다.
        // 위 분지 U 는 유출이 0.25 와 0.3 m/yr 사이에서 넘치기 시작하고, 아래 분지 D 는 이 범위에서 늘 넘치지 못합니다.
        // U 가 넘치지 못하는 동안 D 로 물이 모이는 넓이는 403 km² 로 그대로입니다. U 가 넘치면 U 로 모이던 206 km² 의 물 가운데
        // U 의 수면이 더 증발시키고 남은 물(넘친 물)이 D 로 갑니다. 예: 유출 0.3 m/yr 에서 U 에 한 해 6.18 × 10⁷ m³ 가 들어오고,
        // 넘칠 높이(24 m)보다 낮은 45칸이 5.85 × 10⁷ m³ 를 더 증발시켜 3.3 × 10⁶ m³ 가 넘칩니다.
        int prevD = 0;
        int prevU = 0;
        double sharedD = 0.0;
        bool uOpened = false;
        foreach (double runoff in new[] { 0.02, 0.05, 0.1, 0.2, 0.3, 0.4, 0.5 })
        {
            LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(n, runoff), Fill(n, 0.5), Fill(n, 1.5), null, RouteEps);
            (List<Lake> lakes, List<Pool> pools) = AssertBalanced(g, z, outlet, runoff, 0.5, 1.5, null, r);
            Lake d = lakes.Single(l => l.Cells.Contains(CentreD));
            Lake? u = lakes.SingleOrDefault(l => l.Cells.Contains(CentreU));
            Assert.True(d.Closed);
            // LakeBalance.Solve 가 센 D 의 들어온 물도 지도에서 다시 센 물과 같습니다.
            Assert.Equal(d.InflowM3PerYr, r.Lakes.Single(b => b.Pit == CentreD).InflowM3PerYr, 1e-9 * d.InflowM3PerYr);
            // 물이 늘면 넘치지 못하는 호수는 줄지 않습니다.
            Assert.True(d.Cells.Count >= prevD, $"유출 {runoff} m/yr: D 의 잠긴 칸이 {prevD}개에서 {d.Cells.Count}개로 줄었습니다");
            prevD = d.Cells.Count;
            if (u is not null)
            {
                Assert.False(uOpened, $"유출 {runoff} m/yr: 넘치던 U 가 물이 늘었는데 다시 닫혔습니다");
                Assert.True(u.Closed && u.Cells.Count >= prevU);
                prevU = u.Cells.Count;
                // U 가 넘치지 못하는 동안 D 로 모이는 넓이(흘러드는 물 ÷ 유출, m²)는 그대로입니다 (U 의 물은 D 로 가지 않음).
                double catchD = d.InflowM3PerYr / runoff;
                Assert.True(sharedD == 0.0 || Math.Abs(catchD - sharedD) <= 1e-6 * sharedD, $"U 가 닫혀 있는데 D 로 모이는 넓이가 {sharedD} 에서 {catchD} m² 로 바뀌었습니다");
                sharedD = catchD;
            }
            else
            {
                // U 가 넘치면 D 로 들어오는 물 = D 로 모이는 넓이의 유출 + U 가 넘긴 물 입니다.
                Pool up = pools.Single(p => p.Cells.Contains(CentreU));
                Assert.True(up.OverflowM3PerYr > 0.0, $"유출 {runoff} m/yr: U 가 넘친다는데 턱을 넘는 물이 한 해 {up.OverflowM3PerYr:E4} m³ 입니다");
                double expect = (sharedD * runoff) + up.OverflowM3PerYr;
                Assert.True(
                    Math.Abs(d.InflowM3PerYr - expect) <= 1e-9 * expect,
                    $"유출 {runoff} m/yr: U 가 넘치는데 D 로 들어오는 물 {d.InflowM3PerYr:E4} m³ 가 D 로 모이는 넓이의 유출과 U 가 넘긴 물을 더한 {expect:E4} m³ 와 다릅니다");
                LakeBudget ub = r.Lakes.Single(b => b.Pit == CentreU);
                Assert.Equal(LakeState.Open, ub.State);
                Assert.Equal(up.OverflowM3PerYr, ub.OverflowM3PerYr, 1e-9 * up.InflowM3PerYr);
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
        Assert.Empty(AssertBalanced(g, z, outlet, 0.5, 1.0, 0.6, null, wet).Lakes);
        Assert.Equal(2, wet.NOpen);
        // 유출 0.001 m/yr: 어느 분지로 들어오는 물도 가장 낮은 칸 하나를 잠그는 데 드는 약 1.8 × 10⁶ m³ 에 못 미칩니다.
        // 물길은 두 분지의 가장 낮은 칸에서 끝납니다.
        LakeBalanceResult dry = LakeBalance.Solve(g, z, outlet, Fill(n, 0.001), Fill(n, 0.2), Fill(n, 2.0), null, RouteEps);
        List<Lake> lakes = AssertBalanced(g, z, outlet, 0.001, 0.2, 2.0, null, dry).Lakes;
        Assert.Equal(2, dry.NDry);
        Assert.Equal([CentreD, CentreU], Enumerable.Range(0, n).Where(c => dry.Sink[c]));
        Assert.All(lakes, l => Assert.False(l.Closed));
    }

    /// <summary>
    /// 넘치지 못하는 호수의 비탈 위(호수 수면보다 높은 곳)에 작은 웅덩이가 있으면, 그 웅덩이로 모이는 물은 호수까지 오지 못합니다.
    /// 호수 물수지는 웅덩이 바닥마다 호수를 두고 자기 물로만 잠그므로(채우고 넘치고 합치기), 호수가 그 물까지 세어 넓이를 정하지
    /// 않습니다. 예전의 LakeBalance.Solve 는 첫 계산에서 그 물까지 세어 호수 넓이를 정하고, 다음 계산에서 웅덩이를 따로 닫아,
    /// 호수가 들어오는 물보다 많이 증발시켰습니다. 이 시험은 그 결함이 돌아오지 않는지 봅니다.
    /// </summary>
    [Fact]
    public void APitAboveAClosedLakeDoesNotSpendTheSameWaterTwice()
    {
        // 가운데가 가장 낮은 깔때기 (21 × 21칸, 가운데 10 m, 한 칸 멀어질 때마다 1 m 높음, 출구는 왼쪽 위 모서리 칸 하나).
        // 마른 기후에서 호수의 수면은 약 13.6 m 입니다 (LakeBalanceTests 와 같은 땅).
        // 가운데에서 동쪽으로 8칸 떨어진 세로 세 칸(약 18 m)을 16.5 m 로 낮춰, 둘레(가장 낮은 곳 17 m)보다 0.5 m 낮은 웅덩이를 만듭니다.
        // 웅덩이는 호수 수면보다 높습니다. 웅덩이로 모이는 물(웅덩이 세 칸을 합한 열다섯 칸의 유출, 한 해 1.5 × 10⁶ m³)은
        // 세 칸을 잠그는 데 드는 3.3 × 10⁶ m³ 에 못 미쳐 웅덩이에서 끝나고, 호수에는 오지 못합니다. 호수에 오는 물은 나머지
        // 422칸의 유출 4.22 × 10⁷ m³ 뿐입니다. 호수가 웅덩이의 물까지 세면 39칸(4.29 × 10⁷ m³)을 잠가, 들어오는 물보다 많이
        // 증발시킵니다.
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
