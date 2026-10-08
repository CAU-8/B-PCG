using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Bpcg.Figures;

/// <summary>
/// matplotlib mplot3d 의 plot_surface·scatter 를 흉내 내는 작은 3D 그리기 (정사영, 화가 알고리즘).
/// </summary>
/// <remarks>
/// view_init(elev, azim) 과 set_box_aspect 를 따릅니다: 각 축의 자료 범위를 상자 비율로 늘린 뒤 돌려서 화면에 투영합니다.
/// matplotlib 의 원근(초점 거리 1)·축 판·눈금은 그리지 않습니다.
/// </remarks>
internal sealed class Scene3D
{
    private readonly double[] _eye;
    private readonly double[] _right;
    private readonly double[] _up;
    private readonly double[] _lo = new double[3];
    private readonly double[] _span = new double[3];
    private readonly double[] _box;

    /// <summary>elev·azim 은 도, _box 는 (x, y, z) 상자 비율, bounds 는 자료 범위 (xmin, xmax, ymin, ymax, zmin, zmax).</summary>
    public Scene3D(double elevDeg, double azimDeg, double[] box, double[] bounds)
    {
        double el = elevDeg * Math.PI / 180.0, az = azimDeg * Math.PI / 180.0;
        _eye = [Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el)];
        _right = [-Math.Sin(az), Math.Cos(az), 0.0];
        _up = [-Math.Sin(el) * Math.Cos(az), -Math.Sin(el) * Math.Sin(az), Math.Cos(el)];
        _box = box;
        for (int k = 0; k < 3; k++)
        {
            _lo[k] = bounds[2 * k];
            _span[k] = bounds[(2 * k) + 1] > bounds[2 * k] ? bounds[(2 * k) + 1] - bounds[2 * k] : 1.0;
        }
    }

    /// <summary>자료 좌표 → (화면 x, 화면 y 위쪽 +, 깊이: 클수록 눈에 가까움). 상자 가운데가 원점.</summary>
    public (double X, double Y, double Depth) Project(double x, double y, double z)
    {
        double bx = (((x - _lo[0]) / _span[0]) - 0.5) * _box[0];
        double by = (((y - _lo[1]) / _span[1]) - 0.5) * _box[1];
        double bz = (((z - _lo[2]) / _span[2]) - 0.5) * _box[2];
        return (
            (bx * _right[0]) + (by * _right[1]) + (bz * _right[2]),
            (bx * _up[0]) + (by * _up[1]) + (bz * _up[2]),
            (bx * _eye[0]) + (by * _eye[1]) + (bz * _eye[2]));
    }

    /// <summary>상자 꼭짓점 8개가 rect 의 fill 비율 안에 들어가게 하는 (배율, 가운데 x, 가운데 y).</summary>
    private (double Scale, double Cx, double Cy) Fit(SKRect rect, double fill)
    {
        double xmin = double.PositiveInfinity, xmax = double.NegativeInfinity;
        double ymin = double.PositiveInfinity, ymax = double.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            double x = _lo[0] + ((i & 1) * _span[0]), y = _lo[1] + (((i >> 1) & 1) * _span[1]), z = _lo[2] + (((i >> 2) & 1) * _span[2]);
            (double px, double py, _) = Project(x, y, z);
            xmin = Math.Min(xmin, px);
            xmax = Math.Max(xmax, px);
            ymin = Math.Min(ymin, py);
            ymax = Math.Max(ymax, py);
        }
        double s = fill * Math.Min(rect.Width / (xmax - xmin), rect.Height / (ymax - ymin));
        return (s, rect.MidX - (s * 0.5 * (xmin + xmax)), rect.MidY + (s * 0.5 * (ymin + ymax)));
    }

    /// <summary>
    /// plot_surface(X, Y, Z, facecolors, rstride=1, cstride=1, linewidth=0, antialiased=False, shade=False).
    /// 격자는 (rows, cols) 행 우선이고, 네모 (r, c) 의 색은 꼭짓점 (r, c) 의 색입니다.
    /// </summary>
    public void DrawSurface(SKCanvas canvas, SKRect rect, double[] x, double[] y, double[] z, int rows, int cols,
        double[] faceRgb, double fill = 0.8)
    {
        (double s, double cx, double cy) = Fit(rect, fill);
        var px = new SKPoint[rows * cols];
        double[] depth = new double[rows * cols];
        for (int k = 0; k < px.Length; k++)
        {
            (double sx, double sy, double d) = Project(x[k], y[k], z[k]);
            px[k] = new SKPoint((float)(cx + (s * sx)), (float)(cy - (s * sy)));
            depth[k] = d;
        }
        var quads = new List<(double Depth, int R, int C)>((rows - 1) * (cols - 1));
        for (int r = 0; r + 1 < rows; r++)
        {
            for (int c = 0; c + 1 < cols; c++)
            {
                int a = (r * cols) + c;
                double d = 0.25 * (depth[a] + depth[a + 1] + depth[a + cols] + depth[a + cols + 1]);
                quads.Add((d, r, c));
            }
        }
        quads.Sort((p, q) => p.Depth.CompareTo(q.Depth));
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        foreach ((_, int r, int c) in quads)
        {
            int a = (r * cols) + c;
            path.Reset();
            path.MoveTo(px[a]);
            path.LineTo(px[a + 1]);
            path.LineTo(px[a + cols + 1]);
            path.LineTo(px[a + cols]);
            path.Close();
            paint.Color = ToColor(faceRgb[a * 3], faceRgb[(a * 3) + 1], faceRgb[(a * 3) + 2], 1.0);
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// scatter(x, y, z, s, c, alpha) 여러 묶음: 먼 점부터 그리고, matplotlib depthshade 처럼 먼 점일수록 흐리게(0.3~1배).
    /// radius 는 픽셀입니다.
    /// </summary>
    public void DrawPoints(SKCanvas canvas, SKRect rect, IReadOnlyList<(double[] Pos, (double R, double G, double B) Color)> groups,
        float radius, double alpha, double fill = 0.8)
    {
        (double s, double cx, double cy) = Fit(rect, fill);
        var pts = new List<(double Depth, float X, float Y, int G)>();
        double dmin = double.PositiveInfinity, dmax = double.NegativeInfinity;
        for (int g = 0; g < groups.Count; g++)
        {
            double[] pos = groups[g].Pos;
            for (int k = 0; k + 2 < pos.Length; k += 3)
            {
                (double sx, double sy, double d) = Project(pos[k], pos[k + 1], pos[k + 2]);
                pts.Add((d, (float)(cx + (s * sx)), (float)(cy - (s * sy)), g));
                dmin = Math.Min(dmin, d);
                dmax = Math.Max(dmax, d);
            }
        }
        pts.Sort((p, q) => p.Depth.CompareTo(q.Depth));
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach ((double d, float x, float y, int g) in pts)
        {
            double shade = dmax > dmin ? 0.3 + (0.7 * (d - dmin) / (dmax - dmin)) : 1.0;
            (double r, double gg, double b) = groups[g].Color;
            paint.Color = ToColor(r, gg, b, alpha * shade);
            canvas.DrawCircle(x, y, radius, paint);
        }
    }

    private static SKColor ToColor(double r, double g, double b, double a) => new(
        (byte)Math.Round(Math.Clamp(r, 0, 1) * 255), (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(b, 0, 1) * 255), (byte)Math.Round(Math.Clamp(a, 0, 1) * 255));
}
