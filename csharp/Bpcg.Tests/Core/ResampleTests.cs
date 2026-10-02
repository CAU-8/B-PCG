using System;
using Bpcg.Core;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>core/resample.py 대조: 스텐실 번호는 exact, 가중치·보간 값은 macOS 비트 일치.</summary>
public sealed class ResampleTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(7, 1)]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    [InlineData(16, 8)]
    [InlineData(32, 1)]
    [InlineData(33, 3)]
    public void GhostStencil(int n, int layers)
    {
        GoldenCase g = Golden.Load("core/resample", $"ghost_stencil_n{n}_l{layers}");
        GhostStencil st = Resample.GetGhostStencil(n, layers);
        Compare.Equal(g.Out("edge_dst").AsLong(), st.EdgeDst, "edge_dst");
        Compare.Equal(g.Out("edge_src").AsLong(), st.EdgeSrc, "edge_src");
        Compare.Bits(g.Out("edge_w").AsDouble(), st.EdgeW, "edge_w");
        Compare.Equal(g.Out("corner_dst").AsLong(), st.CornerDst, "corner_dst");
        Compare.Equal(g.Out("corner_src").AsLong(), st.CornerSrc, "corner_src");
        Compare.Bits(g.Out("corner_w").AsDouble(), st.CornerW, "corner_w");
    }

    [Theory]
    [InlineData("add_ghost_n4_l1_f8")]
    [InlineData("add_ghost_n7_l2_f8")]
    [InlineData("add_ghost_n16_l1_f8")]
    [InlineData("add_ghost_n16_l3_f4")]
    [InlineData("add_ghost_n5_l1_i4")]
    [InlineData("add_ghost_n6_l1_nan")]
    [InlineData("add_ghost_n8_l2_negzero")]
    [InlineData("add_ghost_n10_l2_const")]
    public void AddGhostLayers(string name)
    {
        GoldenCase g = Golden.Load("core/resample", name);
        NpyArray faces = g.In("faces");
        int n = (int)faces.Shape[1];
        int layers = (int)g.In("layers").ScalarLong();
        double[] got = faces.Data switch
        {
            double[] d => Resample.AddGhostLayers(d, n, layers),
            float[] f => Resample.AddGhostLayers(f, n, layers),
            int[] i => Resample.AddGhostLayers(i, n, layers),
            _ => throw new InvalidOperationException(faces.Descr),
        };
        Compare.Bits(g.Out("padded").AsDouble(), got, name);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void SampleSphere(int n)
    {
        GoldenCase g = Golden.Load("core/resample", $"sample_sphere_n{n}");
        double[] unit = g.In("unit").AsDouble();
        Compare.Bits(g.Out("linear").AsDouble(), Resample.SampleSphere(g.In("field").AsDouble(), n, unit), "linear");
        Compare.Equal(g.Out("nearest").AsByte(), Resample.SampleSphereNearest(g.In("cat").AsByte(), n, unit), "nearest");
    }

    [Fact]
    public void ResampleCoarseToL0()
    {
        GoldenCase g = Golden.Load("core/resample", "resample_16_to_32");
        CellGraph dst = Graph.SphereGraph(32, 6_371_000.0, 1.0, 0);
        Compare.Bits(g.In("unit").AsDouble(), dst.Unit(), "L0 unit");
        Compare.Bits(g.Out("linear").AsDouble(), Resample.ResampleSphere(g.In("field").AsDouble(), 16, dst), "linear");
        Compare.Equal(g.Out("nearest").AsInt(), Resample.ResampleSphereNearest(g.In("cat").AsInt(), 16, dst), "nearest");
    }
}
