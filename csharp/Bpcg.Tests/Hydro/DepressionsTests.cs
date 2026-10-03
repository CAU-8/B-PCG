using System;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>hydro/depressions.py 대조: 채운 고도가 0 의 부호까지 비트 단위로 같아야 합니다.</summary>
public sealed class DepressionsTests
{
    [Theory]
    [MemberData(nameof(HydroCases.Names), MemberType = typeof(HydroCases))]
    public void FillDepressionsAndEpsilon(string name)
    {
        GoldenCase g = Golden.Load("hydro", name);
        int[] nbr = g.In("nbr").AsInt();
        bool[] outlet = g.In("is_outlet").AsBool();
        double eps = g.In("eps").ScalarDouble();
        Compare.Bits(g.Out("zhat").AsDouble(), Depressions.FillDepressions(g.In("z").AsDouble(), nbr, outlet), "zhat");
        Compare.Bits(g.Out("zt").AsDouble(), Depressions.FillEpsilon(g.In("z").AsDouble(), nbr, outlet, eps), "zt");
        Compare.Bits(g.Out("zt2").AsDouble(), Depressions.FillEpsilon(g.In("z2").AsDouble(), nbr, outlet, eps), "zt2");
    }

    [Fact]
    public void SignedZeroFollowsFifoOrder()
    {
        GoldenCase g = Golden.Load("hydro", "signed_zero");
        double[] got = Depressions.FillDepressions(g.In("z").AsDouble(), g.In("nbr").AsInt(), g.In("is_outlet").AsBool());
        Compare.Bits(g.Out("zhat").AsDouble(), got, "signed zero");
    }

    [Fact]
    public void Errors()
    {
        int[] nbr = [1, 0];
        Assert.Throws<ArgumentException>(() => Depressions.FillDepressions([1.0, 2.0], nbr, [false, false]));
        Assert.Throws<ArgumentException>(() => Depressions.FillDepressions([1.0, double.NaN], nbr, [true, false]));
        Assert.Throws<ArgumentException>(() => Depressions.FillEpsilon([1.0, 2.0], nbr, [true, false], 0.0));
        // 출구와 이어지지 않은 칸
        Assert.Throws<ArgumentException>(() => Depressions.FillDepressions([1.0, 2.0], [-1, -1], [true, false]));
    }
}
