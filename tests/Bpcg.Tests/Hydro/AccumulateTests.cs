using System.Linq;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>hydro/accumulate.py 대조: 같은 순서의 순차 합이라 비트 단위로 같아야 합니다.</summary>
public sealed class AccumulateTests
{
    [Theory]
    [MemberData(nameof(HydroCases.Names), MemberType = typeof(HydroCases))]
    public void AccumulateAndDonorsCount(string name)
    {
        GoldenCase g = Golden.Load("hydro", name);
        long[] rcv = g.Out("rcv2").AsLong();
        long[] order = g.Out("order").AsLong();
        double[] area = g.In("area").AsDouble();
        Compare.Bits(g.Out("area_acc").AsDouble(), AccumulateModule.Accumulate(rcv, order, area), "area_acc");
        double[] w = area.Select(a => a * 0.37).ToArray();
        Compare.Bits(g.Out("q").AsDouble(), AccumulateModule.Accumulate(rcv, order, w), "q");
        Compare.Bits(g.Out("one").AsDouble(), AccumulateModule.Accumulate(rcv, order, 1.0), "스칼라 가중치");
        Compare.Equal(g.Out("donors_count").AsInt(), AccumulateModule.DonorsCount(rcv), "donors_count");
    }
}
