using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ScottPlot;
using SkiaSharp;

namespace Bpcg.Figures;

/// <summary>
/// ScottPlot 으로 matplotlib 그림을 흉내 내는 공통 도구: 한글 글꼴, 그림 크기(인치 × dpi), imshow, 색 막대, 저장.
/// </summary>
internal static class FigureKit
{
    /// <summary>render_results.py 의 figure.dpi.</summary>
    public const double Dpi = 110.0;

    private static readonly string[] KoreanFonts =
        ["AppleGothic", "Apple SD Gothic Neo", "Malgun Gothic", "NanumGothic", "Noto Sans CJK KR"];

    private static readonly object FontGate = new();
    private static string? ChosenFont;

    /// <summary>한글 글꼴을 고릅니다 (Python 과 같은 후보 순서, 없으면 글자로 찾음). 반환은 글꼴 이름.</summary>
    public static string UseKoreanFont()
    {
        lock (FontGate)
        {
            if (ChosenFont is null)
            {
                string? name = Array.Find(KoreanFonts, f => Fonts.GetTypeface(f, false, false) is { } t && t.FamilyName == f);
                ChosenFont = name ?? Fonts.Detect("가나다");
                Fonts.Default = ChosenFont;
            }
            return ChosenFont;
        }
    }

    public static Color Rgb(double r, double g, double b, double a = 1.0) => new(
        (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(b, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(a, 0, 1) * 255));

    public static Color Rgb((double R, double G, double B) c) => Rgb(c.R, c.G, c.B);

    /// <summary>글자 크기를 matplotlib 기본값(10 pt, 제목 12 pt)에 가깝게 맞춘 새 그래프.</summary>
    public static Plot NewPlot(string? title = null, string? xLabel = null, string? yLabel = null, double dpi = Dpi)
    {
        UseKoreanFont();
        var p = new Plot();
        float tick = Pt(10, dpi), label = Pt(10, dpi), head = Pt(12, dpi);
        foreach (IAxis ax in new IAxis[] { p.Axes.Bottom, p.Axes.Left, p.Axes.Top, p.Axes.Right })
        {
            ax.TickLabelStyle.FontSize = tick;
            ax.Label.FontSize = label;
            ax.Label.Bold = false;
            PlainTicks(ax);
        }
        // 오른쪽 끝 눈금 글자가 잘리지 않게 오른쪽에 여백을 둡니다.
        p.Axes.Right.MinimumSize = Pt(14, dpi);
        p.Axes.Title.Label.FontSize = head;
        p.Axes.Title.Label.Bold = false;
        p.Legend.FontSize = tick;
        p.HideGrid();
        if (title is not null)
        {
            p.Title(title);
        }
        if (xLabel is not null)
        {
            p.XLabel(xLabel);
        }
        if (yLabel is not null)
        {
            p.YLabel(yLabel);
        }
        return p;
    }

    /// <summary>matplotlib 처럼 눈금을 성기게, 작은 눈금 없이, 천 단위 쉼표 없이 (1000, 0.25).</summary>
    public static void PlainTicks(IAxis ax)
    {
        ax.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = TickLabel, TickDensity = 0.6 };
        ax.MinorTickStyle.Length = 0;
    }

    private static string TickLabel(double v)
    {
        string s = v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>pt → 픽셀.</summary>
    public static float Pt(double pt, double dpi = Dpi) => (float)(pt * dpi / 72.0);

    /// <summary>RGB (H·W·3) 또는 RGBA (H·W·4), [0, 1] → 그림.</summary>
    public static Image ToImage(double[] px, int h, int w)
    {
        int ch = px.Length / (h * w);
        if (ch is not (3 or 4) || px.Length != h * w * ch)
        {
            throw new ArgumentException("픽셀 배열 길이가 H·W·3 또는 H·W·4 가 아닙니다");
        }
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        byte[] bytes = new byte[h * w * 4];
        for (int k = 0; k < h * w; k++)
        {
            for (int c = 0; c < 3; c++)
            {
                bytes[(k * 4) + c] = ToByte(px[(k * ch) + c]);
            }
            bytes[(k * 4) + 3] = ch == 4 ? ToByte(px[(k * 4) + 3]) : (byte)255;
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
        return new Image(bmp);
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1) * 255);

    // 그래프마다 imshow 정보: aspect="equal" 이면 자료 가로/세로 비율(matplotlib 처럼 축 범위는 두고 자료 영역을 줄여 맞춤),
    // 그리고 보간을 정할 그림들.
    private static readonly ConditionalWeakTable<Plot, ImshowInfo> Imshows = [];

    private sealed class ImshowInfo
    {
        public double? Aspect { get; set; }

        public List<(ScottPlot.Plottables.ImageRect Rect, int Width, bool Smooth)> Images { get; } = [];
    }

    /// <summary>
    /// imshow(img, extent=(left, right, bottom, top)): 좌표 축 범위를 그림에 맞추고 equal 이면 가로세로 단위를 같게.
    /// smooth 이면 matplotlib 의 interpolation="antialiased" 처럼 3배 이상 키울 때만 가장 가까운 칸, 아니면 부드럽게 그립니다.
    /// smooth 가 아니면 늘 가장 가까운 칸 (interpolation="nearest").
    /// </summary>
    public static ScottPlot.Plottables.ImageRect AddImage(
        Plot p, Image img, double left, double right, double bottom, double top, bool equal = true, bool smooth = true)
    {
        var rect = p.Add.ImageRect(img, new CoordinateRect(left, right, bottom, top));
        rect.AntiAlias = smooth;
        p.Axes.SetLimits(left, right, bottom, top);
        ImshowInfo info = Imshows.GetOrCreateValue(p);
        info.Images.Add((rect, img.Width, smooth));
        if (equal)
        {
            info.Aspect = Math.Abs(right - left) / Math.Abs(top - bottom);
        }
        return rect;
    }

    /// <summary>축·눈금 없이 그림만 (ax.axis("off")).</summary>
    public static void AxisOff(Plot p)
    {
        p.Axes.Frameless(false);
        p.Axes.Bottom.IsVisible = false;
        p.Axes.Left.IsVisible = false;
        p.Axes.Top.IsVisible = false;
        p.Axes.Right.IsVisible = false;
        p.HideGrid();
    }

    /// <summary>fig.colorbar: 색표 lut 를 norm 으로 읽는 색 막대를 오른쪽에 붙입니다.</summary>
    public static ScottPlot.Panels.ColorBar AddColorBar(
        Plot p, double[] lut, Func<double, double> norm, double vmin, double vmax, string? label = null, double dpi = Dpi)
    {
        var cb = p.Add.ColorBar(new LutAxis(lut, norm, vmin, vmax), Edge.Right);
        cb.Axis.TickLabelStyle.FontSize = Pt(10, dpi);
        PlainTicks(cb.Axis);
        if (label is not null)
        {
            cb.Label = label;
            cb.LabelStyle.FontSize = Pt(10, dpi);
            cb.LabelStyle.Bold = false;
        }
        return cb;
    }

    /// <summary>그림 한 장(그래프 여러 개를 격자로)을 PNG 로 씁니다. suptitle 은 맨 위 가운데 글.</summary>
    public static void Save(string path, double widthIn, double heightIn, IReadOnlyList<Plot> plots, int rows, int cols,
        string? suptitle = null, double dpi = Dpi) =>
        SaveCanvas(path, widthIn, heightIn, suptitle, (canvas, area) =>
        {
            float cellW = area.Width / cols, cellH = area.Height / rows;
            for (int i = 0; i < plots.Count; i++)
            {
                int r = i / cols, c = i % cols;
                int pw = (int)Math.Floor(cellW), ph = (int)Math.Floor(cellH);
                using SKBitmap bmp = RenderPlot(plots[i], pw, ph);
                canvas.DrawBitmap(bmp, area.Left + (c * cellW), area.Top + (r * cellH));
            }
        }, dpi);

    /// <summary>
    /// 그래프 하나를 (w, h) 그림으로. aspect="equal" 이면 자동 배치의 자료 영역을 가로세로 비율에 맞게 줄여 가운데에 둡니다.
    /// (그래프를 한 캔버스에 차례로 그리면 앞 그래프가 지워져서 따로 그린 뒤 붙입니다.)
    /// </summary>
    private static SKBitmap RenderPlot(Plot p, int w, int h)
    {
        if (Imshows.TryGetValue(p, out ImshowInfo? info))
        {
            p.RenderInMemory(w, h);
            PixelRect d = p.LastRender.DataRect;
            if (info.Aspect is double aspect)
            {
                float dw = d.Width, dh = d.Height;
                if (dw / dh > aspect)
                {
                    float nw = (float)(dh * aspect);
                    d = new PixelRect(d.Left + ((dw - nw) / 2), d.Left + ((dw + nw) / 2), d.Bottom, d.Top);
                }
                else
                {
                    float nh = (float)(dw / aspect);
                    d = new PixelRect(d.Left, d.Right, d.Top + ((dh + nh) / 2), d.Top + ((dh - nh) / 2));
                }
                p.Layout.Fixed(d);
            }
            foreach ((ScottPlot.Plottables.ImageRect rect, int width, bool smooth) in info.Images)
            {
                rect.AntiAlias = smooth && d.Width / width < 3.0;
            }
        }
        byte[] png = p.GetImageBytes(w, h, ImageFormat.Png);
        return SKBitmap.Decode(png);
    }

    /// <summary>그래프 하나짜리 그림.</summary>
    public static void Save(string path, double widthIn, double heightIn, Plot plot, double dpi = Dpi) =>
        Save(path, widthIn, heightIn, [plot], 1, 1, null, dpi);

    /// <summary>흰 바탕에 (있으면) 맨 위 제목을 쓰고, 남은 영역을 draw 가 채운 그림을 PNG 로 씁니다.</summary>
    public static void SaveCanvas(
        string path, double widthIn, double heightIn, string? title, Action<SKCanvas, SKRect> draw, double dpi = Dpi)
    {
        int w = (int)Math.Round(widthIn * dpi), h = (int)Math.Round(heightIn * dpi);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        float top = 0;
        if (title is not null)
        {
            float size = Pt(12, dpi);
            DrawText(canvas, title, w / 2f, size * 1.6f, size, SKTextAlign.Center);
            top = size * 2.4f;
        }
        draw(canvas, new SKRect(0, top, w, h));
        WritePng(surface, path);
    }

    /// <summary>한글 글꼴로 글 한 줄 (y 는 글자 바닥선).</summary>
    public static void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKTextAlign align, SKColor? color = null)
    {
        using var font = new SKFont(Fonts.GetTypeface(UseKoreanFont(), false, false), size);
        using var paint = new SKPaint { Color = color ?? SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, x, y, align, font, paint);
    }

    public static void WritePng(SKSurface surface, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        using SKImage snap = surface.Snapshot();
        using SKData data = snap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>색 막대가 읽는 축: 값 범위 [vmin, vmax] 를 norm → 조회표로 칠합니다.</summary>
    private sealed class LutAxis(double[] lut, Func<double, double> norm, double vmin, double vmax) : IHasColorAxis
    {
        public IColormap Colormap { get; set; } = new LutColormap(lut, norm, vmin, vmax);

        public ScottPlot.Range GetRange() => new(vmin, vmax);
    }

    private sealed class LutColormap(double[] lut, Func<double, double> norm, double vmin, double vmax) : IColormap
    {
        public string Name => "lut";

        public Color GetColor(double position)
        {
            Span<double> c = stackalloc double[4];
            Raster.Lookup(lut, norm(vmin + (Math.Clamp(position, 0, 1) * (vmax - vmin))), c);
            return Rgb(c[0], c[1], c[2]);
        }
    }
}

/// <summary>선분 여러 개를 한 번에 그리는 그래프 요소 (해안선 등고선용).</summary>
internal sealed class SegmentsPlot(List<(double X0, double Y0, double X1, double Y1)> segments, Color color, float width)
    : IPlottable
{
    public bool IsVisible { get; set; } = true;

    public IAxes Axes { get; set; } = new ScottPlot.Axes();

    public IEnumerable<LegendItem> LegendItems => LegendItem.None;

    public AxisLimits GetAxisLimits() => AxisLimits.NoLimits;

    public void Render(RenderPack rp)
    {
        using var path = new SKPath();
        foreach ((double x0, double y0, double x1, double y1) in segments)
        {
            Pixel a = Axes.GetPixel(new Coordinates(x0, y0));
            Pixel b = Axes.GetPixel(new Coordinates(x1, y1));
            path.MoveTo(a.X, a.Y);
            path.LineTo(b.X, b.Y);
        }
        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            StrokeWidth = width,
            IsStroke = true,
            IsAntialias = true,
        };
        rp.Canvas.Save();
        rp.Canvas.ClipRect(new SKRect(rp.DataRect.Left, rp.DataRect.Top, rp.DataRect.Right, rp.DataRect.Bottom));
        rp.Canvas.DrawPath(path, paint);
        rp.Canvas.Restore();
    }
}
