using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Numerics;
using SkiaSharp;

namespace Bpcg.Figures;

/// <summary>
/// 큐브스피어 셀 중심을 면별 색으로 찍어 보는 데모 그림 (analysis/demos/plot_cubesphere.py 를 옮김).
/// </summary>
/// <remarks>Python 판은 창으로 띄울 수도 있었지만 여기서는 PNG 로만 씁니다. 3D 축 판·눈금은 그리지 않습니다.</remarks>
public static class CubesphereFigure
{
    private const double Dpi = 120.0;

    // matplotlib 의 한 글자 색 'r', 'g', 'b', 'c', 'm', 'y'
    private static readonly (double R, double G, double B)[] FaceColors =
        [(1, 0, 0), (0, 0.5, 0), (0, 0, 1), (0, 0.75, 0.75), (0.75, 0, 0.75), (0.75, 0.75, 0)];

    /// <summary>면 한 변 n 칸, 반지름 r 의 격자를 그려 path 에 씁니다. 반환은 (셀 수, 전체 면적 합).</summary>
    public static (int NCells, double AreaSum) Render(int n, double r, string path)
    {
        Grid g = Cubesphere.CubesphereGrid(n, r);
        int m = n * n;
        var groups = new List<(double[] Pos, (double R, double G, double B) Color)>();
        for (int f = 0; f < 6; f++)
        {
            groups.Add((g.Pos[(f * m * 3)..((f + 1) * m * 3)], FaceColors[f]));
        }
        var scene = new Scene3D(30, -60, [1.0, 1.0, 1.0], [-r, r, -r, r, -r, r]);
        float radius = (float)(Math.Sqrt(20.0) / 2 * Dpi / 72.0); // scatter s = 20 pt²
        float fs = FigureKit.Pt(10, Dpi);
        FigureKit.SaveCanvas(path, 8, 8, $"Cubesphere grid (n={n})", (canvas, area) =>
        {
            scene.DrawPoints(canvas, area, groups, radius, 0.8, 0.8);
            DrawLegend(canvas, area, fs, radius);
        }, Dpi);
        return (g.NCells, NpReduce.Sum(g.AreaFull()));
    }

    private static void DrawLegend(SKCanvas canvas, SKRect area, float fs, float radius)
    {
        float line = fs * 1.5f, pad = fs * 0.6f;
        string[] labels = new string[6];
        float textW = 0;
        using (var font = new SKFont(ScottPlot.Fonts.GetTypeface(FigureKit.UseKoreanFont(), false, false), fs))
        {
            for (int f = 0; f < 6; f++)
            {
                labels[f] = $"Face {f} ({Cubesphere.FaceNames[f]})";
                textW = Math.Max(textW, font.MeasureText(labels[f]));
            }
        }
        float w = pad + (radius * 2) + pad + textW + pad, h = (pad * 2) + (line * 6);
        var box = new SKRect(area.Right - w - pad, area.Top + pad, area.Right - pad, area.Top + pad + h);
        using (var bg = new SKPaint { Color = new SKColor(255, 255, 255, 204), Style = SKPaintStyle.Fill })
        using (var edge = new SKPaint { Color = new SKColor(204, 204, 204), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true })
        {
            canvas.DrawRoundRect(box, 4, 4, bg);
            canvas.DrawRoundRect(box, 4, 4, edge);
        }
        using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        for (int f = 0; f < 6; f++)
        {
            float cy = box.Top + pad + (line * (f + 0.5f));
            (double cr, double cg, double cb) = FaceColors[f];
            dot.Color = new SKColor((byte)(cr * 255), (byte)(cg * 255), (byte)(cb * 255), 204);
            canvas.DrawCircle(box.Left + pad + radius, cy, radius, dot);
            FigureKit.DrawText(canvas, labels[f], box.Left + pad + (radius * 2) + pad, cy + (fs * 0.35f), fs, SKTextAlign.Left);
        }
    }
}
