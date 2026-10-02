using System.Collections.Generic;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>hydro/network.py 대조: 유역 번호와 강 구간(순서 포함)이 같아야 합니다.</summary>
public sealed class NetworkTests
{
    [Theory]
    [MemberData(nameof(HydroCases.Names), MemberType = typeof(HydroCases))]
    public void OutletAndRiverSegments(string name)
    {
        GoldenCase g = Golden.Load("hydro", name);
        long[] rcv = g.Out("rcv2").AsLong();
        long[] order = g.Out("order").AsLong();
        Compare.Equal(g.Out("outlet_of").AsLong(), Network.OutletOf(rcv, order), "outlet_of");
        List<long[]> segs = Network.RiverSegments(rcv, order, g.In("is_river").AsBool());
        (long[] cells, long[] offsets) = Network.RiverSegmentsCsr(segs);
        Compare.Equal(g.Out("seg_cells").AsLong(), cells, "구간 칸");
        Compare.Equal(g.Out("seg_offsets").AsLong(), offsets, "구간 시작");
    }
}
