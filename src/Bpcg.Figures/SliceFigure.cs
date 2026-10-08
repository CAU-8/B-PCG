using System;

namespace Bpcg.Figures;

/// <summary>
/// 수직 단면 RGB 에 축(거리, 고도)을 붙인 PNG (src/bpcg/volume/slices.py 의 _save_png).
/// </summary>
/// <remarks>단면 RGB 계산은 <see cref="Bpcg.Volume.Slices.VerticalSlice"/> 가 합니다.</remarks>
public static class SliceFigure
{
    private const double Dpi = 150.0;

    /// <summary>rgb 는 (H, W, 3) 행 우선 바이트, length 는 단면 길이 [m], z 범위 [m].</summary>
    public static void Save(byte[] rgb, int h, int w, double length, double zMin, double zMax, string path)
    {
        if (rgb.Length != h * w * 3)
        {
            throw new ArgumentException("rgb 길이가 H·W·3 과 다릅니다");
        }
        double[] px = new double[rgb.Length];
        for (int k = 0; k < px.Length; k++)
        {
            px[k] = rgb[k] / 255.0;
        }
        double aspect = h / (double)Math.Max(w, 1);
        double heightIn = Math.Max(2.0, Math.Min(10.0, 10.0 * aspect)) + 0.8;
        // 한글 글꼴이 없는 기기에서도 깨지지 않게 축 이름은 영어로 둡니다 (Python 과 같음).
        var p = FigureKit.NewPlot(null, "distance [m]", "elevation [m]", Dpi);
        FigureKit.AddImage(p, FigureKit.ToImage(px, h, w), 0.0, length, zMin, zMax, equal: false, smooth: false);
        FigureKit.Save(path, 10.0, heightIn, p, Dpi);
    }
}
