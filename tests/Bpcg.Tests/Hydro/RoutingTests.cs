using System;
using Bpcg.Core;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>hydro/routing.py 대조: 수신 셀·순서·기여 셀은 exact, 경사는 비트 단위.</summary>
public sealed class RoutingTests
{
    [Theory]
    [MemberData(nameof(HydroCases.Names), MemberType = typeof(HydroCases))]
    public void D8ReceiversAndOrder(string name)
    {
        GoldenCase g = Golden.Load("hydro", name);
        int[] nbr = g.In("nbr").AsInt();
        double[] dist = g.In("dist").AsDouble();
        bool[] outlet = g.In("is_outlet").AsBool();
        (long[] rcv, double[] slope, int n1) = Routing.D8Receivers(g.Out("zt").AsDouble(), nbr, dist, outlet);
        Compare.Equal(g.Out("rcv").AsLong(), rcv, "rcv");
        Compare.Bits(g.Out("slope").AsDouble(), slope, "slope");
        Assert.Equal(g.Out("n_changed").ScalarLong(), n1);

        (long[] rcv2, double[] slope2, int n2) = Routing.D8Receivers(
            g.Out("zt2").AsDouble(), nbr, dist, outlet, rcv, g.In("eta").ScalarDouble());
        Compare.Equal(g.Out("rcv2").AsLong(), rcv2, "rcv2 (히스테리시스)");
        Compare.Bits(g.Out("slope2").AsDouble(), slope2, "slope2");
        Assert.Equal(g.Out("n_changed2").ScalarLong(), n2);

        (long[] start, long[] donors) = Routing.DonorLists(rcv2);
        Compare.Equal(g.Out("start").AsLong(), start, "start");
        Compare.Equal(g.Out("donors").AsLong(), donors, "donors");
        Compare.Equal(g.Out("order").AsLong(), Routing.TopoOrder(rcv2), "order");
    }

    [Fact]
    public void ZeroDistanceToLowerNeighbourThrowsLikeNumba()
    {
        Assert.Throws<DivideByZeroException>(() =>
            Routing.D8Receivers([1.0, 0.0], [1, 0], [0.0, 0.0], [false, true]));
    }

    // 출구가 드문 숲(큰 나무 하나)과 흔한 숲에서, 병렬 순서가 한 줄 순서와 같은지 봅니다. 부모는 늘 번호가 더 작은 칸이라
    // 순환이 없고, 절반쯤은 바로 앞 칸을 부모로 삼아 긴 줄기가 생깁니다. 끝 두 칸은 출구에 닿지 않는 고리입니다.
    // 나눌 자리 짐작은 없음·정확한 나무 크기·모두 큼(가장 잘게 나눔)·모두 0 네 가지로 바꿔 봅니다.
    [Theory]
    [InlineData(1, 0.0005)]
    [InlineData(3, 0.0005)]
    [InlineData(8, 0.0005)]
    [InlineData(8, 0.05)]
    public void TopoIntoParallelMatchesSerial(int threads, double rootRate)
    {
        const int n = 60_000;
        int[] rcv = new int[n];
        for (int c = 0; c < n; c++)
        {
            bool root = c == 0 || Hashing.HashUnit(31, c, 0) < rootRate;
            int parent = Hashing.HashUnit(31, c, 1) < 0.6 ? c - 1 : (int)(Hashing.HashUnit(31, c, 2) * c);
            rcv[c] = root ? c : parent;
        }
        rcv[n - 2] = n - 1;
        rcv[n - 1] = n - 2;
        int[] start = new int[n + 1];
        int[] donors = new int[n];
        Routing.DonorCsrInto(rcv, start, donors, new int[n]);
        int[] expected = new int[n];
        int mExpected = Routing.TopoInto(rcv, start, donors, new int[n], expected, out int rootsExpected);
        Assert.Equal(n - 2, mExpected);
        double[] size = new double[n];
        for (int k = mExpected - 1; k >= 0; k--)
        {
            int c = expected[k];
            size[c] += 1.0;
            if (rcv[c] != c)
            {
                size[rcv[c]] += size[c];
            }
        }
        double[] huge = new double[n];
        Array.Fill(huge, 1e9);
        Parallelism.Configure(threads);
        try
        {
            foreach (double[]? hint in new[] { null, size, huge, new double[n] })
            {
                int[] actual = new int[n];
                int m = Routing.TopoIntoParallel(
                    rcv, start, donors, new int[n], actual, new Routing.TopoScratch(n), hint, n / 64.0, out int roots);
                Assert.Equal(mExpected, m);
                Assert.Equal(rootsExpected, roots);
                Compare.Equal(expected[..m], actual[..m], "order");
            }
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    [Fact]
    public void CycleThrows()
    {
        Assert.Throws<ArgumentException>(() => Routing.TopoOrder([1, 0]));
    }
}
