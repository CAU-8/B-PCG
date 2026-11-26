using System;
using System.Collections.Generic;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>구면 대표점(노드 흔들기로 옮긴 점)의 값을 다른 점에서 읽기 (NodeStencil, SphereDelaunay).</summary>
public sealed class NodeStencilTests
{
    private const double R = 6_371_000.0;

    // 칸마다 해시로 정한 [0, 1) 값. 시드가 같으면 늘 같습니다.
    private static double[] Field(int nCells, long stream)
    {
        long[] ids = new long[nCells];
        for (int i = 0; i < nCells; i++)
        {
            ids[i] = i;
        }
        return Hashing.HashUniformArray(ids, 11, stream);
    }

    private static byte[] Categories(int nCells)
    {
        double[] u = Field(nCells, 3);
        return Array.ConvertAll(u, v => (byte)(v * 7.0));
    }

    private static double[] Row(double[] unit, int c) => [unit[c * 3], unit[(c * 3) + 1], unit[(c * 3) + 2]];

    // 큐브 꼭짓점 칸, 면 가장자리 한가운데 칸, 면 안쪽 칸, 그리고 대표점이 제 칸 밖으로 나간 칸 몇 개.
    private static List<int> SampleCells(CellGraph g, double[] unit)
    {
        int n = (int)g.Shape[1];
        int nn = n * n;
        var cells = new List<int> { 0, (2 * nn) + (n - 1), (4 * nn) + ((n - 1) * n) + (n / 2), (5 * nn) + ((n / 2) * n) + (n / 3), 100 };
        long[] home = Cubesphere.CellOf(unit, n);
        int outsideOwn = 0;
        for (int c = 0; c < g.NCells && outsideOwn < 6; c++)
        {
            if (home[c] != c)
            {
                cells.Add(c);
                outsideOwn++;
            }
        }
        // 노드 흔들기 1.0 이면 면 가장자리 한가운데 근처의 대표점은 제 칸 밖으로 나갑니다. 그런 칸도 시험합니다.
        Assert.True(outsideOwn > 0);
        return cells;
    }

    [Fact]
    public void ReturnsTheNodeValueOnEachNode()
    {
        CellGraph g = Graph.SphereGraph(16, R, 1.0, 5);
        double[] unit = g.Unit();
        double[] field = Field(g.NCells, 1);
        float[] field32 = Array.ConvertAll(field, v => (float)v);
        byte[] cat = Categories(g.NCells);
        int oldMissed = 0;
        foreach (int c in SampleCells(g, unit))
        {
            NodeStencil st = NodeStencil.Build(g, Row(unit, c));
            // 면당 16칸이면 큐브 꼭짓점 둘레에서도 둘레 다섯 겹이 다 들어갑니다.
            Assert.Equal(NodeStencil.DefaultRings, st.RingsUsed);
            Assert.Equal(field[c], st.Linear(field)[0]);
            Assert.Equal((double)field32[c], st.Linear(field32)[0]);
            Assert.Equal(cat[c], st.Nearest(cat)[0]);
            Assert.Equal(c, st.NearestCell[0]);
            // 옛 방법(값이 칸 정중앙에 있다고 보는 쌍선형 보간)은 대표점 위에서 그 대표점의 값을 돌려주지 않습니다.
            if (Math.Abs(Resample.SampleSphere(field, 16, Row(unit, c))[0] - field[c]) > 1e-3)
            {
                oldMissed++;
            }
        }
        Assert.True(oldMissed > 0);
    }

    [Fact]
    public void NearestNodeMatchesBruteForceSearch()
    {
        CellGraph g = Graph.SphereGraph(16, R, 1.0, 5);
        double[] unit = g.Unit();
        foreach (int c in SampleCells(g, unit))
        {
            double[] q = PointsAround(unit, c, 400, g.Spacing / R * 1.5, 17 + c);
            NodeStencil st = NodeStencil.Build(g, q);
            for (int k = 0; k < st.Count; k++)
            {
                Assert.Equal(BruteNearest(unit, q, k), st.NearestCell[k]);
            }
        }
    }

    [Fact]
    public void EachPointLiesInATriangleWhoseCircumcircleHoldsNoOtherNode()
    {
        // 둘레에서 모은 대표점으로만 이었어도, 점을 담은 삼각형은 행성의 모든 대표점으로 이은 들로네 삼각형과 같아야 합니다.
        CellGraph g = Graph.SphereGraph(16, R, 1.0, 5);
        double[] unit = g.Unit();
        foreach (int c in SampleCells(g, unit))
        {
            double[] q = PointsAround(unit, c, 200, g.Spacing / R * 1.5, 31 + c);
            NodeStencil st = NodeStencil.Build(g, q);
            for (int k = 0; k < st.Count; k++)
            {
                int a = st.Cells[k * 3], b = st.Cells[(k * 3) + 1], d = st.Cells[(k * 3) + 2];
                double wa = st.Weights[k * 3], wb = st.Weights[(k * 3) + 1], wd = st.Weights[(k * 3) + 2];
                Assert.True(wa >= 0.0 && wb >= 0.0 && wd >= 0.0);
                // 무게의 합은 반올림 오차(몇 ulp)만큼만 1 에서 벗어납니다.
                Assert.True(Math.Abs(wa + wb + wd - 1.0) < 1e-12);
                // 무게로 섞은 세 대표점의 방향이 읽은 점의 방향과 같습니다(삼각형 평면에 비춘 점).
                double[] mix = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    mix[i] = (wa * unit[(a * 3) + i]) + (wb * unit[(b * 3) + i]) + (wd * unit[(d * 3) + i]);
                }
                double norm = Math.Sqrt((mix[0] * mix[0]) + (mix[1] * mix[1]) + (mix[2] * mix[2]));
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(Math.Abs((mix[i] / norm) - q[(k * 3) + i]) < 1e-12);
                }
                for (int other = 0; other < g.NCells; other++)
                {
                    if (other == a || other == b || other == d)
                    {
                        continue;
                    }
                    // 외접원 판정 값은 칸 크기의 네제곱(약 1e-6)쯤이라, 1e-18 은 반올림 오차만 봐줍니다.
                    Assert.False(InCircle(unit, a, b, d, other) < -1e-18, $"점 {k}: 칸 {other} 이 외접원 안에 있습니다");
                }
            }
        }
    }

    [Fact]
    public void MoreRingsGiveTheSameStencil()
    {
        CellGraph g = Graph.SphereGraph(48, R, 1.0, 9);
        double[] unit = g.Unit();
        foreach (int c in SampleCells(g, unit))
        {
            double[] q = PointsAround(unit, c, 300, g.Spacing / R * 2.0, 5 + c);
            NodeStencil st5 = NodeStencil.Build(g, q);
            NodeStencil st8 = NodeStencil.Build(g, q, 8);
            Assert.Equal(5, st5.RingsUsed);
            Assert.Equal(8, st8.RingsUsed);
            Assert.True(st8.NodeCount > st5.NodeCount);
            Assert.Equal(st5.Cells, st8.Cells);
            Assert.Equal(st5.Weights, st8.Weights);
            Assert.Equal(st5.NearestCell, st8.NearestCell);
        }
    }

    [Fact]
    public void OnePointGivesTheSameValueAsManyPoints()
    {
        CellGraph g = Graph.SphereGraph(48, R, 1.0, 9);
        double[] unit = g.Unit();
        double[] field = Field(g.NCells, 1);
        double[] q = PointsAround(unit, 700, 500, g.Spacing / R * 3.0, 3);
        double[] all = NodeStencil.Build(g, q).Linear(field);
        for (int k = 0; k < 500; k += 37)
        {
            Assert.Equal(all[k], NodeStencil.Build(g, Row(q, k)).Linear(field)[0]);
        }
    }

    [Fact]
    public void ValuesChangeContinuouslyAlongAPath()
    {
        // 삼각형 경계를 건널 때 값이 뛰지 않아야 합니다. 칸 세 개를 가로지르는 대원 위에서 촘촘히 읽습니다.
        CellGraph g = Graph.SphereGraph(16, R, 1.0, 5);
        double[] unit = g.Unit();
        double[] field = Field(g.NCells, 1);
        int c = (2 * 16 * 16) + (16 * 15) + 15;
        double[] p0 = Row(unit, c);
        double[] dir = Tangent(p0);
        double span = g.Spacing / R * 3.0;
        const int steps = 20_000;
        double[] q = new double[(steps + 1) * 3];
        for (int s = 0; s <= steps; s++)
        {
            double ang = span * ((2.0 * s / steps) - 1.0);
            double[] p = Rotate(p0, dir, ang);
            Array.Copy(p, 0, q, s * 3, 3);
        }
        double[] v = NodeStencil.Build(g, q).Linear(field);
        double maxJump = 0.0;
        for (int s = 0; s < steps; s++)
        {
            maxJump = Math.Max(maxJump, Math.Abs(v[s + 1] - v[s]));
        }
        // 한 걸음은 칸의 3/10,000 입니다. 값의 차가 1 인 두 대표점 사이의 기울기가 가는 삼각형에서 수십 배가 되어도
        // 한 걸음의 차이는 0.01 보다 작습니다. 경계에서 값이 뛰면 0.1 넘게 벌어집니다.
        Assert.True(maxJump < 0.01, $"가장 큰 한 걸음 차이 {maxJump}");
    }

    [Fact]
    public void ThreadCountDoesNotChangeTheResult()
    {
        CellGraph g = Graph.SphereGraph(48, R, 1.0, 9);
        double[] unit = g.Unit();
        double[] q = PointsAround(unit, 1234, 5000, g.Spacing / R * 3.0, 8);
        NodeStencil many = NodeStencil.Build(g, q);
        Parallelism.Configure(1);
        try
        {
            NodeStencil one = NodeStencil.Build(g, q);
            Assert.Equal(many.Cells, one.Cells);
            Assert.Equal(many.Weights, one.Weights);
            Assert.Equal(many.NearestCell, one.NearestCell);
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    [Fact]
    public void CoarseGridWithoutJitterStillWorks()
    {
        // 히어로 경계 시험이 쓰는 거친 격자(면당 8칸, 흔들기 없음)의 큐브 꼭짓점 칸. 둘레 겹을 다 넣으면 반구를 넘으므로 줄입니다.
        CellGraph g = Graph.SphereGraph(8, R);
        double[] unit = g.Unit();
        double[] field = Field(g.NCells, 1);
        int c = g.NCells / 2;
        NodeStencil st = NodeStencil.Build(g, Row(unit, c));
        Assert.True(st.RingsUsed is >= 1 and < NodeStencil.DefaultRings);
        Assert.Equal(field[c], st.Linear(field)[0]);
    }

    [Fact]
    public void RejectsPointsSpreadOverTheSphere()
    {
        CellGraph g = Graph.SphereGraph(16, R, 1.0, 5);
        double[] q = [1.0, 0.0, 0.0, -1.0, 0.0, 0.0];
        Assert.Throws<ArgumentException>(() => NodeStencil.Build(g, q));
        Assert.Throws<ArgumentException>(() => NodeStencil.Build(Graph.FlatGraph(4, 4, 1.0), [0.0, 0.0, 1.0]));
        Assert.Throws<ArgumentException>(() => NodeStencil.Build(g, [0.0, 0.0, 1.0], 0));
        NodeStencil st = NodeStencil.Build(g, [0.0, 0.0, 1.0]);
        Assert.Throws<ArgumentException>(() => st.Linear(new double[3]));
    }

    // 칸 c 의 대표점 둘레, 각 반지름 radius [rad] 안에 해시로 흩은 점 count 개 (count, 3).
    private static double[] PointsAround(double[] unit, int c, int count, double radius, long stream)
    {
        double[] p0 = Row(unit, c);
        double[] e1 = Tangent(p0);
        double[] e2 = Cross(p0, e1);
        long[] ids = new long[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = i;
        }
        double[] ua = Hashing.HashUniformArray(ids, 101, stream);
        double[] ub = Hashing.HashUniformArray(ids, 102, stream);
        double[] q = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            double x = ((2.0 * ua[i]) - 1.0) * radius;
            double y = ((2.0 * ub[i]) - 1.0) * radius;
            double[] p = new double[3];
            double norm = 0.0;
            for (int k = 0; k < 3; k++)
            {
                p[k] = p0[k] + (x * e1[k]) + (y * e2[k]);
                norm += p[k] * p[k];
            }
            norm = Math.Sqrt(norm);
            for (int k = 0; k < 3; k++)
            {
                q[(i * 3) + k] = p[k] / norm;
            }
        }
        return q;
    }

    private static int BruteNearest(double[] unit, double[] q, int k)
    {
        int best = -1;
        double bestDot = double.NegativeInfinity;
        for (int c = 0; c < unit.Length / 3; c++)
        {
            double d = ((unit[c * 3] * q[k * 3]) + (unit[(c * 3) + 1] * q[(k * 3) + 1])) + (unit[(c * 3) + 2] * q[(k * 3) + 2]);
            if (d > bestDot)
            {
                bestDot = d;
                best = c;
            }
        }
        return best;
    }

    // (a − d)·((b − d) × (c − d)): 반시계 삼각형 abc 의 외접원 안에 d 가 있으면 음수.
    private static double InCircle(double[] unit, int a, int b, int c, int d)
    {
        double[] A = new double[3], B = new double[3], C = new double[3];
        for (int i = 0; i < 3; i++)
        {
            A[i] = unit[(a * 3) + i] - unit[(d * 3) + i];
            B[i] = unit[(b * 3) + i] - unit[(d * 3) + i];
            C[i] = unit[(c * 3) + i] - unit[(d * 3) + i];
        }
        double[] bc = Cross(B, C);
        return (A[0] * bc[0]) + (A[1] * bc[1]) + (A[2] * bc[2]);
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Tangent(double[] p)
    {
        double[] axis = Math.Abs(p[0]) < 0.9 ? [1.0, 0.0, 0.0] : [0.0, 1.0, 0.0];
        double[] e = Cross(axis, p);
        double n = Math.Sqrt((e[0] * e[0]) + (e[1] * e[1]) + (e[2] * e[2]));
        return [e[0] / n, e[1] / n, e[2] / n];
    }

    // p 를 접선 방향 t 쪽으로 각 ang [rad] 만큼 대원을 따라 옮긴 단위 벡터.
    private static double[] Rotate(double[] p, double[] t, double ang) =>
        [(Math.Cos(ang) * p[0]) + (Math.Sin(ang) * t[0]), (Math.Cos(ang) * p[1]) + (Math.Sin(ang) * t[1]), (Math.Cos(ang) * p[2]) + (Math.Sin(ang) * t[2])];
}
