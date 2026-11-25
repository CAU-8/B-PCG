using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>
/// 아래층 동굴 통로가 층리면(지층이 쌓인 면)을 따라 물 높이 아래로 오르내리는 규칙 (docs/pipeline.md 10장 4번 '통로의 높이').
/// 기대값은 손으로 만든 층리면과 설정 상수에서 바로 계산합니다. 그물이 낸 값끼리 견주지 않습니다.
/// </summary>
public sealed class CaveBeddingTests
{
    private const int N = 24;
    private const double Dx = 25.0;

    // 한 줄 통로가 놓이는 행과, 그 행에서 싱크홀이 있는 열. 샘은 0열입니다.
    private const int Row = 12;
    private const int SinkCol = 22;

    // 통로를 지나는 물이 적어(넓이 2 × 10⁵ m² × 1 m/yr) 반지름은 그리는 아래 한계 2 m 입니다.
    private const double R = CaveNetwork.RadiusMinM;

    // 층리면이 기운 정도 (sin). 기울기 (−0.15, 0.05) 인 평면은 sin = sqrt(0.025 ÷ 1.025) ≈ 0.156 (약 9.0°) 입니다.
    private static readonly double PlaneDipSin = Math.Sqrt(0.025 / 1.025);

    private static readonly CaveNetworkParams Params = new(
        RadiusMaxM: 3.0, SinkSpacingM: 0.0, SinkMinAreaM2: 1.0e4, PathWavelengthM: 200.0, Seed: 3);

    private static int Cell(int row, int col) => (row * N) + col;

    private static double X(int col) => (col + 0.5) * Dx;

    // 물 높이(아래 지하수면)는 동쪽으로 1 km 에 2 m 씩 오릅니다. 샘(서쪽)으로 갈수록 낮아집니다.
    private static double Level(int col) => 100.0 + (0.002 * X(col));

    /// <summary>
    /// 나란한 지층 네 겹: 층 i 의 바닥 높이 = 250 − 0.15·x + 0.05·y − 60·i [m]. 칸마다 대표점(흔든 자리)에서 잽니다.
    /// 층리면은 동쪽으로 100 m 에 15 m 씩 내려가고 북쪽으로 100 m 에 5 m 씩 오릅니다.
    /// </summary>
    private static (double[] Bottom, int NLayers) TiltedLayers(CellGraph g)
    {
        const int nL = 4;
        double[] bottom = new double[g.NCells * nL];
        for (int c = 0; c < g.NCells; c++)
        {
            for (int i = 0; i < nL; i++)
            {
                bottom[(c * nL) + i] = 250.0 - (0.15 * g.Pos[c * 3]) + (0.05 * g.Pos[(c * 3) + 1]) - (60.0 * i);
            }
        }
        return (bottom, nL);
    }

    /// <summary>
    /// 한 줄 통로: 흔들지 않은 격자의 Row 행에만 동굴 층이 있어, 통로가 그 행을 따라 곧게 갑니다. 0열이 샘, SinkCol 열이
    /// 싱크홀입니다. 칸에서 샘까지 거리는 25 m × 열 번호이고, 싱크홀에서 샘까지의 물길은 550 m 입니다.
    /// </summary>
    private static (CellGraph G, double[] Z, double[] Levels, bool[] Outlet, bool[] Allowed, double[] Area) RowWorld()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        int n = N * N;
        double[] levels = Enumerable.Repeat(double.NaN, n).ToArray();
        double[] z = Enumerable.Repeat(160.0, n).ToArray();
        for (int col = 0; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            levels[c] = Level(col);
            z[c] = levels[c] + 60.0;
        }
        bool[] outlet = new bool[n];
        bool[] allowed = new bool[n];
        double[] area = new double[n];
        outlet[Cell(Row, 0)] = true;
        allowed[Cell(Row, SinkCol)] = true;
        area[Cell(Row, SinkCol)] = 2.0e5;
        return (g, z, levels, outlet, allowed, area);
    }

    [Fact]
    public void BedDipFromJitteredNodesMatchesTheHandMadePlane()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 11);
        int n = N * N;
        (double[] bottom, int nL) = TiltedLayers(g);
        double[] level = Enumerable.Repeat(100.0, n).ToArray();
        int none = Cell(3, 4);
        int basement = Cell(20, 9);
        level[none] = double.NaN;      // 동굴 층이 없는 칸
        level[basement] = -1.0e4;      // 모든 층 바닥보다 아래 (기반암 안)
        (double[] bedZ, double[] dipSin) = Bedding.AtLevel(g, bottom, nL, level);
        var layersUsed = new HashSet<int>();
        for (int c = 0; c < n; c++)
        {
            if (c == none)
            {
                Assert.True(double.IsNaN(bedZ[c]));
                Assert.Equal(0.0, dipSin[c]);
                continue;
            }
            // 층리면은 물 높이가 든 지층의 바닥면입니다: 바닥이 처음으로 물 높이보다 낮은 층. 기반암 안이면 마지막 층의 바닥.
            int li = nL - 1;
            for (int i = 0; i < nL; i++)
            {
                if (bottom[(c * nL) + i] < level[c])
                {
                    li = i;
                    break;
                }
            }
            layersUsed.Add(li);
            Assert.Equal(bottom[(c * nL) + li], bedZ[c]);
            // 가장자리와 모서리 칸도 이웃 대표점이 한 줄로 서 있지 않으면 같은 기울기가 나옵니다.
            Assert.Equal(PlaneDipSin, dipSin[c], tolerance: 1e-9);
        }
        Assert.Equal([1, 2, 3], layersUsed.Order());

        // 대표점이 충분히 흔들려 있는지: 높이 차를 칸 간격 두 배로 나누는 방법은 이 평면에서도 기운 정도를 10% 넘게 틀립니다.
        double worst = 0.0;
        for (int row = 1; row < N - 1; row++)
        {
            for (int col = 1; col < N - 1; col++)
            {
                double gx = (bottom[Cell(row, col + 1) * nL] - bottom[Cell(row, col - 1) * nL]) / (2.0 * Dx);
                double gy = (bottom[Cell(row - 1, col) * nL] - bottom[Cell(row + 1, col) * nL]) / (2.0 * Dx);
                double g2 = (gx * gx) + (gy * gy);
                worst = Math.Max(worst, Math.Abs(Math.Sqrt(g2 / (1.0 + g2)) - PlaneDipSin) / PlaneDipSin);
            }
        }
        Assert.True(worst > 0.1, $"칸 간격으로 나눈 기운 정도의 가장 큰 오차가 {worst:P1} 입니다");
    }

    [Fact]
    public void PassageFollowsAHandMadeTiltedBedBetweenJitteredNodes()
    {
        // 서쪽 가장자리 가운데 칸이 샘, 동쪽의 싱크홀 하나. 대표점은 흔들려 있고, 통로는 그 사이를 굽이쳐 갑니다.
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 11);
        int n = N * N;
        double[] levels = new double[n];
        double[] z = new double[n];
        bool[] allowed = new bool[n];
        for (int c = 0; c < n; c++)
        {
            levels[c] = Level(c % N);
            z[c] = levels[c] + 60.0;
            allowed[c] = true;
        }
        int spring = Cell(Row, 0);
        int sink = Cell(Row, SinkCol);
        bool[] outlet = new bool[n];
        outlet[spring] = true;
        allowed[spring] = false;
        double[] area = new double[n];
        area[sink] = 2.0e5;
        (double[] bottom, int nL) = TiltedLayers(g);
        (double[] bedZ, double[] dipSin) = Bedding.AtLevel(g, bottom, nL, levels);
        CaveNetwork net = CaveNetwork.Build(
            g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(BedZ: bedZ, DipSin: dipSin));

        // 싱크홀에서 샘까지의 길. 통로 조각의 양 끝(다듬은 자리)으로 칸마다 샘까지의 거리를 잽니다.
        var down = new Dictionary<int, (double X, double Y, double Step)>();
        int e = 0;
        for (int c = 0; c < n; c++)
        {
            if (net.Flow[c] > 0.0 && net.Parent[c] >= 0)
            {
                double ex = net.SegB[e * 3] - net.SegA[e * 3];
                double ey = net.SegB[(e * 3) + 1] - net.SegA[(e * 3) + 1];
                down[c] = (net.SegA[e * 3], net.SegA[(e * 3) + 1], Math.Sqrt((ex * ex) + (ey * ey)));
                e++;
            }
        }
        var path = new List<int>();
        for (int c = sink; c >= 0; c = net.Parent[c])
        {
            path.Add(c);
        }
        Assert.Equal(spring, path[^1]);
        var toSpring = new Dictionary<int, double> { [spring] = 0.0 };
        for (int i = path.Count - 2; i >= 0; i--)
        {
            toSpring[path[i]] = toSpring[path[i + 1]] + down[path[i]].Step;
        }
        // 싱크홀이 하나라 모든 칸의 물길 길이는 싱크홀에서 샘까지의 길이이고, 기운 정도도 어디서나 같아 폭이 하나입니다.
        double span = CaveNetwork.LoopDepth(toSpring[sink], PlaneDipSin);
        Assert.InRange(span, 5.0, 40.0);

        // 층리면은 폭 간격으로 겹겹이 있습니다. 통로 깊이 = (물 높이 − 층리면) 을 폭으로 나눈 나머지이고, 샘까지 거리의 0.5배를 넘지 않습니다.
        foreach (int c in path)
        {
            double above = levels[c] - bedZ[c];
            double saw = above - (span * Math.Floor(above / span));
            double drop = Math.Min(saw, CaveNetwork.SpringRiseSlope * toSpring[c]);
            Assert.Equal(levels[c] + (CaveNetwork.StreamLift * R) - drop, net.AxisZ[c], tolerance: 1e-6);
        }
        // 그래서 칸 사이에서 통로의 높이 차는 층리면의 높이 차와 같거나(면을 따라감), 폭만큼 다릅니다(세로 틈을 타고 다른 면으로 옮겨 탐).
        // 옮겨 타는 자리는 (물 높이 − 층리면) 이 폭의 배수를 지나는 자리뿐입니다. 샘 쪽 자르기가 걸리지 않는 칸만 봅니다.
        int moves = 0;
        int crossings = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            int c = path[i];
            int q = path[i + 1];
            if (toSpring[q] < 2.0 * span)
            {
                continue;
            }
            double k = ((net.AxisZ[q] - net.AxisZ[c]) - (bedZ[q] - bedZ[c])) / span;
            Assert.Equal(Math.Round(k), k, tolerance: 1e-6);
            moves += Math.Round(k) != 0.0 ? 1 : 0;
            crossings += Math.Floor((levels[c] - bedZ[c]) / span) != Math.Floor((levels[q] - bedZ[q]) / span) ? 1 : 0;
        }
        Assert.Equal(crossings, moves);
        Assert.True(moves >= 2, $"통로가 층리면을 바꿔 탄 자리가 {moves}곳뿐이라 톱니를 시험하지 못합니다");
    }

    [Fact]
    public void SawtoothDepthCarriesTheDownstreamDepthAlongTheBed()
    {
        // 폭 10 m. 샘 칸에서 물 높이가 층리면보다 123 m 위면 물 높이 바로 아래의 면은 3 m 아래입니다.
        Assert.Equal(3.0, CaveNetwork.SawtoothDepth(10.0, 123.0, double.NaN, double.NaN), tolerance: 1e-12);
        // 상류 칸에서 그 차가 127 m 면 같은 면이 4 m 더 깊어 7 m, 135 m 면 15 m 가 폭을 넘어 위의 면(5 m)으로 옮겨 탑니다.
        Assert.Equal(7.0, CaveNetwork.SawtoothDepth(10.0, 127.0, 123.0, 3.0), tolerance: 1e-12);
        Assert.Equal(5.0, CaveNetwork.SawtoothDepth(10.0, 135.0, 127.0, 7.0), tolerance: 1e-12);
        // 면이 물 높이 위로 올라가면 아래의 면으로 옮겨 탑니다: 1 m 에서 3 m 얕아지면 −2 m 라 8 m.
        Assert.Equal(8.0, CaveNetwork.SawtoothDepth(10.0, 120.0, 123.0, 1.0), tolerance: 1e-12);
        // 하류 칸의 깊이를 이어 가므로, 폭이 달라져도 같은 면 위에서는 깊이가 면의 높이 차만큼만 바뀝니다.
        // 칸마다 따로 나머지를 구하면 123 m 는 폭 10 m 에서 3 m, 폭 11 m 에서 2 m 로 갈라집니다.
        Assert.Equal(3.5, CaveNetwork.SawtoothDepth(11.0, 123.5, 123.0, 3.0), tolerance: 1e-12);
        // 폭이 없거나 층리면을 모르면 통로는 물 높이를 따라갑니다.
        Assert.Equal(0.0, CaveNetwork.SawtoothDepth(0.0, 123.0, 120.0, 3.0));
        Assert.Equal(0.0, CaveNetwork.SawtoothDepth(10.0, double.NaN, 120.0, 3.0));
        // 하류 칸의 층리면을 모르면 물 높이 바로 아래의 면에서 다시 시작합니다.
        Assert.Equal(3.0, CaveNetwork.SawtoothDepth(10.0, 123.0, double.NaN, 7.0), tolerance: 1e-12);
    }

    [Fact]
    public void SawtoothStaysOnTheBedWhenTheSpanChangesFromCellToCell()
    {
        // 톱니 위상: 물 높이가 층리면보다 500 m 넘게 위라 (물 높이 − 층리면) 이 폭의 20~100배입니다. 이런 땅에서 폭이 칸마다 조금씩
        // 달라도 통로는 면을 따라가다가, 폭만큼 깊어질 때만 위의 면으로 옮겨 탑니다.
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = RowWorld();
        int n = N * N;
        double[] bed = Enumerable.Repeat(double.NaN, n).ToArray();
        double[] dip = new double[n];
        for (int col = 0; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            bed[c] = -400.0 - (3.0 * col);                     // 층리면은 상류(동쪽)로 칸마다 3 m 씩 깊어집니다
            dip[c] = 0.05 + (0.25 * col / SinkCol);            // 폭이 샘 쪽 약 5 m 에서 싱크홀 쪽 약 23 m 까지 늘어납니다
        }
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(BedZ: bed, DipSin: dip));
        double Span(int col) => CaveNetwork.LoopDepth(SinkCol * Dx, dip[Cell(Row, col)]);
        double Fold(int col)
        {
            double above = levels[Cell(Row, col)] - bed[Cell(Row, col)];
            return above - (Span(col) * Math.Floor(above / Span(col)));
        }
        int moves = 0;
        int foldedMoves = 0;
        // 1열과 그 위는 샘까지 25 m 넘게 떨어져 샘 쪽 자르기(거리 × 0.5)가 폭보다 깊어 걸리지 않습니다.
        for (int col = 2; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            int q = Cell(Row, col - 1);
            Assert.Equal(q, net.Parent[c]);
            double drop = levels[c] + (CaveNetwork.StreamLift * R) - net.AxisZ[c];
            Assert.InRange(drop, 0.0, Span(col));
            // 통로의 높이 차는 층리면의 높이 차와 같거나, 이 칸의 폭의 배수만큼 다릅니다.
            double k = ((net.AxisZ[q] - net.AxisZ[c]) - (bed[q] - bed[c])) / Span(col);
            Assert.Equal(Math.Round(k), k, tolerance: 1e-9);
            moves += Math.Round(k) != 0.0 ? 1 : 0;
            // 칸마다 따로 구한 나머지로 놓으면, 두 칸의 통로 높이 차가 층리면의 높이 차와 어긋나는 자리.
            double foldedDev = Fold(col) - Fold(col - 1) + ((levels[q] - levels[c]) - (bed[q] - bed[c]));
            foldedMoves += Math.Abs(foldedDev) > 1e-6 ? 1 : 0;
        }
        // 물길 550 m 에서 층리면이 64 m 깊어지는 동안 폭 5~23 m 의 톱니를 몇 번 넘습니다. 칸마다 따로 나머지를 구하면 거의 모든 칸에서 꺾입니다.
        Assert.InRange(moves, 1, 8);
        Assert.True(foldedMoves >= 15, $"칸마다 따로 구한 나머지가 면에서 벗어난 자리가 {foldedMoves}곳뿐이라 이 시험이 약합니다");
    }

    [Fact]
    public void PassageRisesToTheSpringNoSteeperThanTheLimit()
    {
        // 샘 높이 자르기: 샘에서는 물이 지표로 나와야 하므로, 샘에서 거리 D 떨어진 통로는 물 높이 아래 0.5·D 보다 깊지 않습니다.
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = RowWorld();
        int n = N * N;
        const double sin = 0.4;
        const double deep = 20.0;
        double span = CaveNetwork.LoopDepth(SinkCol * Dx, sin);   // 물길 550 m, sin 0.4 → 약 28.5 m
        Assert.True(span > deep);
        double[] bed = Enumerable.Repeat(double.NaN, n).ToArray();
        double[] dip = new double[n];
        for (int col = 0; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            // 층리면이 물 높이와 나란히 놓여, 면을 따라가면 통로는 어디서나 물 높이 아래 20 m 입니다.
            bed[c] = levels[c] - ((10.0 * span) + deep);
            dip[c] = sin;
        }
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(BedZ: bed, DipSin: dip));
        for (int col = 0; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            // 0열(샘) 0 m, 1열(25 m) 12.5 m, 2열(50 m)부터 20 m.
            double drop = Math.Min(deep, CaveNetwork.SpringRiseSlope * Dx * col);
            Assert.Equal(levels[c] + (CaveNetwork.StreamLift * R) - drop, net.AxisZ[c], tolerance: 1e-9);
        }
        // 샘으로 올라오는 마지막 조각의 기울기는 0.5 에서 물 높이가 샘 쪽으로 낮아지는 몫(0.002)을 뺀 값입니다.
        double rise = (net.AxisZ[Cell(Row, 0)] - net.AxisZ[Cell(Row, 1)]) / Dx;
        Assert.Equal(CaveNetwork.SpringRiseSlope - 0.002, rise, tolerance: 1e-9);
    }

    [Fact]
    public void CaveLakesAreRunsOfDeepWaterUnderAnAirGap()
    {
        // 동굴 호수 규칙: 물 깊이가 2.5 m 이상이고 천장 아래에 0.5 m 이상 빈 통로 칸을 물길을 따라 이어 묶고, 묶음마다 물이 가장
        // 깊은 칸 하나를 호수의 자리로 셉니다. 반지름 2 m 통로의 가운데 선은 물 높이 + 0.8 m − (내려간 깊이 d) 라,
        // 물 깊이는 d + 1.2 m, 천장 아래 빈 높이는 2.8 m − d 입니다. 그래서 d 가 1.3~2.3 m 인 칸이 호수 칸이고,
        // d 가 2.8 m 이상인 칸은 천장까지 잠깁니다.
        (CellGraph g, double[] z, double[] levels, bool[] outlet, bool[] allowed, double[] area) = RowWorld();
        int n = N * N;
        const double sin = 0.4;
        double span = CaveNetwork.LoopDepth(SinkCol * Dx, sin);
        // 열마다 내려간 깊이 d [m] (0열은 샘이라 0 으로 자름).
        double[] d =
        [
            0.3, 0.5, 0.8, 1.5, 2.0, 2.2, 0.4, 0.3, 1.8, 0.2, 5.0, 6.0,
            1.4, 1.9, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1,
        ];
        Assert.Equal(SinkCol + 1, d.Length);
        double[] bed = Enumerable.Repeat(double.NaN, n).ToArray();
        double[] dip = new double[n];
        for (int col = 0; col <= SinkCol; col++)
        {
            int c = Cell(Row, col);
            bed[c] = levels[c] - ((10.0 * span) + d[col]);
            dip[c] = sin;
        }
        CaveNetwork net = CaveNetwork.Build(g, z, levels, outlet, allowed, area, Params, new CaveNetworkFields(BedZ: bed, DipSin: dip));

        // 호수 칸은 3~5열, 8열, 12~13열입니다. 묶음마다 물이 가장 깊은 5열, 8열, 13열이 호수의 자리입니다.
        int[] lakeCols = [5, 8, 13];
        Assert.Equal(lakeCols.Select(col => Cell(Row, col)), net.Lakes);
        for (int i = 0; i < lakeCols.Length; i++)
        {
            Assert.Equal(X(lakeCols[i]), net.LakeXyz[i * 3], tolerance: 1e-9);
            Assert.Equal(-(Row + 0.5) * Dx, net.LakeXyz[(i * 3) + 1], tolerance: 1e-9);
            Assert.Equal(levels[Cell(Row, lakeCols[i])], net.LakeXyz[(i * 3) + 2]);
        }

        // 천장까지 잠긴 조각은 양 끝이 모두 d ≥ 2.8 m 인 11열 → 10열 하나입니다.
        double Axis(int col) => levels[Cell(Row, col)] + (CaveNetwork.StreamLift * R) - (col == 0 ? 0.0 : d[col]);
        double Segment(int col) => Math.Sqrt((Dx * Dx) + Math.Pow(Axis(col - 1) - Axis(col), 2));
        Assert.Equal(Segment(11), net.FloodedM, tolerance: 1e-9);
        Assert.Equal(Enumerable.Range(1, SinkCol).Sum(Segment), net.LowerLevelM, tolerance: 1e-9);
        // 내려간 깊이의 평균은 통로 칸 23개(샘 포함)로 나눕니다.
        Assert.Equal(d.Skip(1).Sum() / (SinkCol + 1), net.MeanLoopDepthM, tolerance: 1e-9);
    }
}
