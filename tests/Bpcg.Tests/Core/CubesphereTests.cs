using System.Linq;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>
/// core/cubesphere.py 대조. tan·atan·atan2·pow 는 macOS 에서 numpy 와 같은 libm 을 쓰므로 비트 단위로 비교합니다
/// (docs/csharp_port.md 6장 core ②). 다른 OS 에서 어긋나면 ulp 허용 오차로 바꾸지 말고 먼저 기록합니다.
/// </summary>
public sealed class CubesphereTests
{
    [Theory]
    [InlineData("grid_n1_r6371000")]
    [InlineData("grid_n2_r6371000")]
    [InlineData("grid_n3_r15")]
    [InlineData("grid_n5_r1")]
    [InlineData("grid_n16_r6371000")]
    [InlineData("grid_n16_r9964050")]
    [InlineData("grid_n32_r6371000")]
    [InlineData("grid_n33_r6371000")]
    public void CubesphereGrid(string name)
    {
        GoldenCase g = Golden.Load("core/cubesphere", name);
        Grid grid = Cubesphere.CubesphereGrid((int)g.In("n").ScalarLong(), g.In("R").ScalarDouble());
        Compare.Bits(g.Out("pos").AsDouble(), grid.Pos, name + " pos");
        Compare.Bits(g.Out("area").AsDouble(), grid.Area, name + " area");
    }

    [Fact]
    public void ToSphereAndOmega()
    {
        GoldenCase g = Golden.Load("core/cubesphere", "to_sphere_omega");
        double[] a = g.In("a").AsDouble();
        double[] b = g.In("b").AsDouble();
        for (int f = 0; f < 6; f++)
        {
            Compare.Bits(g.Out($"f{f}").AsDouble(), Cubesphere.ToSphere(f, a, b), $"to_sphere f{f}");
        }
        double[] X = g.In("X").AsDouble();
        double[] Y = g.In("Y").AsDouble();
        Compare.Bits(g.Out("omega").AsDouble(), X.Select((x, i) => Cubesphere.Omega(x, Y[i])).ToArray(), "omega");
    }

    [Fact]
    public void NeighborDistance()
    {
        GoldenCase g = Golden.Load("core/cubesphere", "neighbor_distance");
        double[] got = Cubesphere.NeighborDistance(g.In("p1").AsDouble(), g.In("p2").AsDouble(), g.In("R").ScalarDouble());
        Compare.Bits(g.Out("d").AsDouble(), got, "neighbor_distance");
    }

    [Fact]
    public void ToFaceAndCellOf()
    {
        GoldenCase g = Golden.Load("core/cubesphere", "to_face_cell_of");
        double[] p = g.In("p").AsDouble();
        (long[] f, double[] a, double[] b) = Cubesphere.ToFace(p);
        Compare.Equal(g.Out("f").AsLong(), f, "to_face f");
        Compare.Bits(g.Out("a").AsDouble(), a, "to_face a");
        Compare.Bits(g.Out("b").AsDouble(), b, "to_face b");
        foreach (int n in new[] { 1, 3, 16, 32 })
        {
            Compare.Equal(g.Out($"cell_n{n}").AsLong(), Cubesphere.CellOf(p, n), $"cell_of n={n}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(33)]
    public void NeighborTable(int n)
    {
        GoldenCase g = Golden.Load("core/cubesphere", $"neighbor_table_n{n}");
        Compare.Equal(g.Out("nbr").AsInt(), Cubesphere.NeighborTable(n), $"neighbor_table n={n}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void CrossFacePairs(int n)
    {
        GoldenCase g = Golden.Load("core/cubesphere", $"cross_face_pairs_n{n}");
        Compare.Equal(g.Out("pairs").AsLong(), Cubesphere.CrossFacePairs(n), $"cross_face_pairs n={n}");
    }
}
