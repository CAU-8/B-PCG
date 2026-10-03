using System;
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

    [Fact]
    public void CycleThrows()
    {
        Assert.Throws<ArgumentException>(() => Routing.TopoOrder([1, 0]));
    }
}
