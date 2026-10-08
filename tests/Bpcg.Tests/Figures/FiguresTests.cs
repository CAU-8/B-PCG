using System;
using System.IO;
using Bpcg.Figures;
using Xunit;

namespace Bpcg.Tests.Figures;

/// <summary>결과 그림의 래스터 계산을 matplotlib 3.11.2 값과 맞대어 봅니다 (그림 파일은 tests/test_figures.py).</summary>
public sealed class FiguresTests
{
    [Fact]
    public void TwoSlopeNormMatchesMatplotlib()
    {
        Assert.Equal(0.25, Raster.TwoSlope(-3500, -7000, 0, 6000));
        Assert.Equal(0.5, Raster.TwoSlope(0, -7000, 0, 6000));
        Assert.Equal(0.75, Raster.TwoSlope(3000, -7000, 0, 6000));
        Assert.Equal(double.NegativeInfinity, Raster.TwoSlope(-8000, -7000, 0, 6000));
        Assert.Equal(double.PositiveInfinity, Raster.TwoSlope(7000, -7000, 0, 6000));
        Assert.True(double.IsNaN(Raster.TwoSlope(double.NaN, -7000, 0, 6000)));
    }

    [Fact]
    public void LookupClampsLikeColormapCall()
    {
        double[] lut = [0, 0, 0, 1, 0.5, 0.5, 0.5, 1, 1, 1, 1, 1];
        Span<double> c = stackalloc double[4];
        Raster.Lookup(lut, double.NegativeInfinity, c);
        Assert.Equal(0.0, c[0]);
        Raster.Lookup(lut, 0.5, c); // 0.5·3 = 1.5 → 1번
        Assert.Equal(0.5, c[0]);
        Raster.Lookup(lut, 1.0, c); // N 은 N − 1 로
        Assert.Equal(1.0, c[0]);
        Raster.Lookup(lut, double.PositiveInfinity, c);
        Assert.Equal(1.0, c[0]);
        Raster.Lookup(lut, double.NaN, c); // 투명
        Assert.Equal(0.0, c[3]);
    }

    [Fact]
    public void ShadeMatchesLightSourceSoftBlend()
    {
        double[] z =
        [
            0, 10, 30, 20, 0,
            10, 40, 60, 30, 10,
            20, 50, 90, 40, 20,
            0, 20, 30, 10, 0,
        ];
        double[] rgb = new double[20 * 3];
        for (int k = 0; k < 20; k++)
        {
            rgb[k * 3] = 0.1 + (0.8 * k / 19.0);
            rgb[(k * 3) + 1] = 0.8 - (0.6 * k / 19.0);
            rgb[(k * 3) + 2] = 0.5;
        }
        // LightSource(azdeg=315, altdeg=40).shade_rgb(rgb, z, vert_exag=1.5, dx=7, dy=7, blend_mode="soft")
        double[] expected =
        [
            0.19, 0.96, 0.75, 0.245183008569076, 0.918880365633494, 0.711378471332859, 0.293018870714346,
            0.877239969003231, 0.681012960036309, 0.196418755329987, 0.669770776275641, 0.457313600198249,
            0.204315657424735, 0.6019194459105, 0.418387474715507, 0.48147539739779, 0.825595181779009,
            0.699613851923283, 0.556689954715208, 0.823077838370609, 0.723471282769658, 0.513808006627424,
            0.700434469614677, 0.624593305485507, 0.330751795199084, 0.440525527998575, 0.392189500244537,
            0.397479530161995, 0.434258334851525, 0.418387474715507, 0.645425051425921, 0.608679583629621,
            0.624593305485507, 0.658373029228684, 0.548522150155006, 0.596758989842233, 0.414133448789725,
            0.226044928376153, 0.300006304385771, 0.433353004265057, 0.166550696987551, 0.265624422217223,
            0.51173191399581, 0.167113817486266, 0.292454460319859, 0.720453603690078, 0.313861179876938,
            0.485836334222451, 0.706301876908403, 0.214743639847896, 0.40379282377762, 0.684364002783736,
            0.093576641962036, 0.281361313386768, 0.735983379501385, 0.053628808864266, 0.25,
            0.815899734203272, 0.050488416361372, 0.266388150564644,
        ];
        double[] actual = Raster.Shade(rgb, z, 4, 5, 1.5, 7.0, 7.0);
        for (int k = 0; k < expected.Length; k++)
        {
            Assert.Equal(expected[k], actual[k], 1e-12);
        }
    }

    [Fact]
    public void FaceEdgesMarksLeftAndTopChanges()
    {
        int[] face = [0, 0, 1, 0, 0, 1, 2, 2, 2];
        bool[] e = Raster.FaceEdges(face, 3, 3);
        Assert.Equal([false, false, true, false, false, true, true, true, true], e);
    }

    [Fact]
    public void ContourAroundOneCellIsAClosedDiamond()
    {
        double[] v = new double[25];
        v[12] = 1.0; // 가운데 한 칸만 1
        var seg = Contours.Segments(v, 5, 5, 0.5, 0, 4, 0, 4);
        Assert.Equal(4, seg.Count);
        foreach ((double x0, double y0, double x1, double y1) in seg)
        {
            // 꼭짓점은 가운데 (2, 2) 에서 한 축으로 0.5 떨어진 점
            Assert.Equal(0.5, Math.Abs(x0 - 2) + Math.Abs(y0 - 2), 12);
            Assert.Equal(0.5, Math.Abs(x1 - 2) + Math.Abs(y1 - 2), 12);
        }
    }

    [Fact]
    public void EquirectPixelCentersCoverAllFaces()
    {
        (long[] cells, int h, int w) = Projections.Equirect(8, 64);
        Assert.Equal((32, 64), (h, w));
        int[] faces = Projections.Faces(cells, 8);
        for (int f = 0; f < 6; f++)
        {
            Assert.Contains(f, faces);
        }
        Assert.Equal(4, faces[0]); // 맨 윗줄은 북극 면 (+Z)
        Assert.Equal(5, faces[^1]); // 맨 아랫줄은 남극 면 (−Z)
    }

    [Fact]
    public void SliceFigureWritesPngWithMatplotlibSize()
    {
        string path = Path.Combine(Path.GetTempPath(), "bpcg-slice-" + Guid.NewGuid() + ".png");
        try
        {
            byte[] rgb = new byte[40 * 100 * 3];
            Array.Fill(rgb, (byte)200);
            SliceFigure.Save(rgb, 40, 100, 300.0, 100.0, 160.0, path);
            byte[] head = File.ReadAllBytes(path)[..24];
            Assert.Equal(0x89, head[0]);
            int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            Assert.Equal((1500, 720), (w, h)); // (10, max(2, 10·0.4) + 0.8) 인치 × 150 dpi
        }
        finally
        {
            File.Delete(path);
        }
    }
}
