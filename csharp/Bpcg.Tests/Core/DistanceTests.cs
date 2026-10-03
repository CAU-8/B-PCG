using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>core/distance.py 대조: 힙 전파의 src 는 exact, 거리는 macOS 비트 일치.</summary>
public sealed class DistanceTests
{
    internal static CellGraph GraphFrom(GoldenCase g)
    {
        bool sphere = g.In("kind_sphere").ScalarBool();
        double[] origin = g.In("origin").AsDouble();
        return new CellGraph(
            sphere ? "sphere" : "flat", g.In("shape").AsLong(), g.In("pos").AsDouble(), g.In("nbr").AsInt(),
            g.In("dist").AsDouble(), g.In("area").AsDouble(), g.In("spacing").ScalarDouble(),
            sphere ? g.In("R").ScalarDouble() : null, (origin[0], origin[1]));
    }

    [Theory]
    [InlineData("sphere32_inf")]
    [InlineData("sphere32_3sp")]
    [InlineData("sphere32_zero")]
    [InlineData("sphere7_inf")]
    [InlineData("sphere7_3sp")]
    [InlineData("flat64_inf")]
    [InlineData("flat64_1000")]
    [InlineData("flat1x5_tie")]
    [InlineData("flat5x5_tie")]
    [InlineData("flat5x5_none")]
    [InlineData("flat20x30_j1")]
    public void NearestSource(string name)
    {
        GoldenCase g = Golden.Load("core/distance", name);
        CellGraph gr = GraphFrom(g);
        bool[] src = g.In("is_source").AsBool();
        double maxDist = g.In("max_dist").ScalarDouble();
        double maxKey = Distance.MaxKey(gr, maxDist);
        Compare.Bits(g.Out("max_key").ScalarDouble(), maxKey, name + " max_key");
        (double[] key, long[] psrc) = Distance.PropagateSources(gr.Pos, gr.Nbr, CellGraph.NSlots, src, maxKey);
        Compare.Equal(g.Out("prop_src").AsLong(), psrc, name + " 전파 src");
        Compare.Bits(g.Out("prop_key").AsDouble(), key, name + " 전파 key");
        (double[] dist, long[] s) = Distance.NearestSource(gr, src, maxDist);
        Compare.Equal(g.Out("src").AsLong(), s, name + " src");
        Compare.Bits(g.Out("dist").AsDouble(), dist, name + " dist");
    }

    [Fact]
    public void NearestSourceValues()
    {
        GoldenCase g = Golden.Load("core/distance", "values_flat20x30");
        CellGraph gr = GraphFrom(g);
        bool[] src = g.In("is_source").AsBool();
        double maxDist = g.In("max_dist").ScalarDouble();

        (double[] d1, long[] s1, double[] v1) = Distance.NearestSourceValues(gr, src, g.In("values.f8").AsDouble(), 1, maxDist);
        Compare.Bits(g.Out("f8.dist").AsDouble(), d1, "f8 dist");
        Compare.Equal(g.Out("f8.src").AsLong(), s1, "f8 src");
        Compare.Bits(g.Out("f8.values").AsDouble(), v1, "f8 values");

        (_, _, int[] v2) = Distance.NearestSourceValues(gr, src, g.In("values.i4").AsInt(), 1, maxDist);
        Compare.Equal(g.Out("i4.values").AsInt(), v2, "i4 values");
        (_, _, byte[] v3) = Distance.NearestSourceValues(gr, src, g.In("values.u1").AsByte(), 1, maxDist);
        Compare.Equal(g.Out("u1.values").AsByte(), v3, "u1 values");
        (_, _, bool[] v4) = Distance.NearestSourceValues(gr, src, g.In("values.b1").AsBool(), 1, maxDist);
        Compare.Equal(g.Out("b1.values").AsBool(), v4, "b1 values");
        (_, _, float[] v5) = Distance.NearestSourceValues(gr, src, g.In("values.f4").AsFloat(), 1, maxDist);
        Compare.Bits(g.Out("f4.values").AsFloat(), v5, "f4 values");
        (_, _, double[] v6) = Distance.NearestSourceValues(gr, src, g.In("values.f8x2").AsDouble(), 2, maxDist);
        Compare.Bits(g.Out("f8x2.values").AsDouble(), v6, "f8x2 values");
    }
}
