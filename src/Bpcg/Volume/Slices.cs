using System;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Volume;

/// <summary>
/// 확인용 수직 단면 그림 (src/bpcg/volume/slices.py, docs/pipeline.md 10장).
/// </summary>
/// <remarks>matplotlib 로 축을 붙여 PNG 를 쓰는 _save_png 는 옮기지 않습니다(결정 D2).</remarks>
public static class Slices
{
    public static readonly byte[] AirRgb = [206, 226, 240];
    public static readonly byte[] WaterRgb = [40, 110, 200];
    public static readonly byte[] CaveAirRgb = [24, 24, 28];
    public static readonly byte[] WaterTableRgb = [20, 40, 160];

    /// <summary>p0 → p1 수직 단면의 RGB 그림 (H, W, 3) 행 우선 (vertical_slice). 반환: (rgb, H, W).</summary>
    public static (byte[] Rgb, int H, int W) VerticalSlice(
        HeroVolume volume, (double X, double Y) p0, (double X, double Y) p1, double zMin, double zMax, double resM)
    {
        if (!(double.IsFinite(p0.X) && double.IsFinite(p0.Y) && double.IsFinite(p1.X) && double.IsFinite(p1.Y)))
        {
            throw new ArgumentException("p0, p1 은 유한한 (동, 북) 두 값이어야 합니다");
        }
        double ddx = p1.X - p0.X;
        double ddy = p1.Y - p0.Y;
        double length = Math.Sqrt((ddx * ddx) + (ddy * ddy));
        if (!(length > 0))
        {
            throw new ArgumentException("p0 과 p1 이 같은 점입니다");
        }
        if (!(double.IsFinite(resM) && resM > 0))
        {
            throw new ArgumentException($"res_m 은 0 보다 커야 합니다: {resM}");
        }
        if (!(double.IsFinite(zMin) && double.IsFinite(zMax) && zMax > zMin))
        {
            throw new ArgumentException($"z_max 는 z_min 보다 커야 합니다: {zMin}, {zMax}");
        }
        int nW = Math.Max((int)Math.Ceiling(length / resM) + 1, 2);
        int nH = Math.Max((int)Math.Ceiling((zMax - zMin) / resM) + 1, 2);
        if ((long)nW * nH > 50_000_000)
        {
            throw new ArgumentException($"단면이 너무 큽니다 ({nW}×{nH} 픽셀). res_m 을 키우세요");
        }
        double[] s = NpGrid.Linspace(0.0, 1.0, nW);
        double[] x = Array.ConvertAll(s, v => p0.X + (v * (p1.X - p0.X)));
        double[] y = Array.ConvertAll(s, v => p0.Y + (v * (p1.Y - p0.Y)));
        double[] zz = NpGrid.Linspace(zMax, zMin, nH);
        double[] up = new double[nW * nH];
        for (int c = 0; c < nW; c++)
        {
            Array.Copy(zz, 0, up, c * nH, nH);
        }
        var ev = volume.EvaluateGrid(x, y, up, nH, double.PositiveInfinity, "d", "material", "water", "d1");
        byte[] mat = (byte[])ev["material"];
        bool[] water = (bool[])ev["water"];
        double[] d1 = (double[])ev["d1"];
        byte[] rgb = new byte[nH * nW * 3];
        for (int r = 0; r < nH; r++)
        {
            for (int c = 0; c < nW; c++)
            {
                int src = (c * nH) + r;
                int dst = ((r * nW) + c) * 3;
                byte[] color;
                if (mat[src] != HeroVolume.MaterialEmpty)
                {
                    int m = Math.Min((int)mat[src], Rocks.NRocks - 1);
                    color = [Rocks.ColorRgb[m * 3], Rocks.ColorRgb[(m * 3) + 1], Rocks.ColorRgb[(m * 3) + 2]];
                }
                else
                {
                    color = d1[src] < 0 ? CaveAirRgb : AirRgb;
                }
                if (water[src])
                {
                    color = WaterRgb;
                }
                Array.Copy(color, 0, rgb, dst, 3);
            }
        }
        double[] zGw = volume.WaterTable(x, y);
        for (int c = 0; c < nW; c++)
        {
            long row = (long)Math.Round((zMax - zGw[c]) / (zMax - zMin) * (nH - 1));
            if (row >= 0 && row < nH)
            {
                Array.Copy(WaterTableRgb, 0, rgb, ((row * nW) + c) * 3, 3);
            }
        }
        // TODO(port): save_path(matplotlib 축 그림)는 옮기지 않았습니다.
        return (rgb, nH, nW);
    }
}
