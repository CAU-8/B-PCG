using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>core/graph.py 대조: 이웃 표는 exact, 위치·거리·면적·spacing 은 macOS 비트 일치.</summary>
public sealed class GraphTests
{
    [Theory]
    [InlineData("sphere_n16_j0.0_s0", 16, 0.0, 0L)]
    [InlineData("sphere_n32_j1.0_s0", 32, 1.0, 0L)]
    [InlineData("sphere_n7_j1.0_s123456789", 7, 1.0, 123456789L)]
    [InlineData("sphere_n16_j0.4_s5", 16, 0.4, 5L)]
    public void SphereGraph(string name, int n, double jitter, long seed)
    {
        GoldenCase g = Golden.Load("core/graph", name);
        CellGraph gr = Graph.SphereGraph(n, g.In("R").ScalarDouble(), jitter, seed);
        Compare.Bits(g.Out("pos").AsDouble(), gr.Pos, name + " pos");
        Compare.Equal(g.Out("nbr").AsInt(), gr.Nbr, name + " nbr");
        Compare.Bits(g.Out("dist").AsDouble(), gr.Dist, name + " dist");
        Compare.Bits(g.Out("area").AsDouble(), gr.Area, name + " area");
        Compare.Bits(g.Out("spacing").ScalarDouble(), gr.Spacing, name + " spacing");
        Compare.Bits(g.Out("unit").AsDouble(), gr.Unit(), name + " unit");
    }

    [Theory]
    [InlineData("flat_64x64_j1.0_s1738104521")]
    [InlineData("flat_16x16_j1.0_s7919")]
    [InlineData("flat_1x5_j0.0_s0")]
    [InlineData("flat_20x30_j0.4_s2")]
    [InlineData("flat_12x9_j0.0_s0")]
    public void FlatGraph(string name)
    {
        GoldenCase g = Golden.Load("core/graph", name);
        double[] origin = g.In("origin").AsDouble();
        CellGraph gr = Graph.FlatGraph(
            (int)g.In("ny").ScalarLong(), (int)g.In("nx").ScalarLong(), g.In("dx").ScalarDouble(),
            g.In("jitter").ScalarDouble(), g.In("seed").ScalarLong(), (origin[0], origin[1]));
        Compare.Bits(g.Out("pos").AsDouble(), gr.Pos, name + " pos");
        Compare.Equal(g.Out("nbr").AsInt(), gr.Nbr, name + " nbr");
        Compare.Bits(g.Out("dist").AsDouble(), gr.Dist, name + " dist");
        Compare.Bits(g.Out("area").AsDouble(), gr.Area, name + " area");
        Compare.Bits(g.Out("spacing").ScalarDouble(), gr.Spacing, name + " spacing");
        Compare.Equal(g.Out("boundary").AsBool(), gr.BoundaryMask(), name + " boundary");
    }
}
