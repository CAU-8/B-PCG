using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.IO;
using Bpcg.Numerics;
using Bpcg.Volume;
using ScottPlot;

namespace Bpcg.Figures;

/// <summary>
/// 히어로 그림 네 가지 (render_results.py 의 5~8): 지도, 땅속 지도, 회랑을 가로지르는 단면, 3D 조감.
/// </summary>
internal sealed class HeroFigures
{
    private static readonly (double R, double G, double B) RiverRgb = (0.1, 0.35, 0.9);
    private static readonly (double R, double G, double B) LakeRgb = (0.25, 0.6, 1.0);

    private readonly HeroState _hero;
    private readonly int _ny;
    private readonly int _nx;
    private readonly double _dx;
    private readonly double[] _z;
    private readonly double _zMin;
    private readonly double _zMax;
    private readonly bool[] _river;
    private readonly bool[] _entrance;

    /// <summary>imshow extent (왼, 오른, 아래, 위) [km]: 히어로 가운데가 원점.</summary>
    private readonly (double L, double R, double B, double T) _ext;

    public HeroFigures(HeroState hero)
    {
        _hero = hero;
        _ny = (int)_hero.Graph.Shape[0];
        _nx = (int)_hero.Graph.Shape[1];
        _dx = _hero.Graph.Spacing;
        _z = _hero.Fields.GetFloat64Any("z_m");
        (_zMin, _zMax) = Raster.NanMinMax(_z);
        _river = Bools("is_river");
        _entrance = Array.ConvertAll(Bytes("cave_entrance"), v => v > 0);
        _ext = (-_nx * _dx / 2000, _nx * _dx / 2000, -_ny * _dx / 2000, _ny * _dx / 2000);
    }

    public double ZMin => _zMin;

    public double ZMax => _zMax;

    private bool[] Bools(string name) => (bool[])FieldSet.CastTo(_hero.Fields[name], typeof(bool));

    private byte[] Bytes(string name) => (byte[])FieldSet.CastTo(_hero.Fields[name], typeof(byte));

    private double HeroNorm(double v) => Raster.Normalize(v, _zMin, _zMax);

    private double[] ShadedHeight(double[] zz, int h, int w, double step, double vert) =>
        Raster.Shade(Raster.DropAlpha(Raster.Colorize(zz, FigureColormapData.GistEarth, HeroNorm)), zz, h, w, vert, step, step);

    private Plot MapPanel(string title, double[] px)
    {
        Plot p = FigureKit.NewPlot(title, "동 (km)", "북 (km)");
        FigureKit.AddImage(p, FigureKit.ToImage(px, _ny, _nx), _ext.L, _ext.R, _ext.B, _ext.T);
        return p;
    }

    /// <summary>5. 히어로 지도: 음영 고도 + 강·호수·선상지, 동굴 입구, 걷는 회랑.</summary>
    public void Map(OrderedDictionary<string, double> rect, string outDir)
    {
        double[] show = ShadedHeight(_z, _ny, _nx, _dx, 1.5);
        bool[] fan = Bools("fan");
        for (int k = 0; k < fan.Length; k++)
        {
            if (fan[k])
            {
                show[k * 3] = (0.6 * show[k * 3]) + (0.4 * 1.0);
                show[(k * 3) + 1] = (0.6 * show[(k * 3) + 1]) + (0.4 * 0.85);
                show[(k * 3) + 2] = (0.6 * show[(k * 3) + 2]) + (0.4 * 0.3);
            }
        }
        Raster.Paint(show, _river, RiverRgb);
        Raster.Paint(show, Bools("is_lake"), LakeRgb);
        string title = $"히어로 유역 {_dx.ToString("0.##", CultureInfo.InvariantCulture)} m ({_nx}×{_ny}칸) — "
            + $"고도 {PyFormat.Fixed(_zMin, 0)}~{PyFormat.Fixed(_zMax, 0)} m · 파랑 강·호수, 노랑 선상지, 분홍 동굴 입구";
        Plot p = MapPanel(title, show);
        var xs = new List<double>();
        var ys = new List<double>();
        for (int k = 0; k < _entrance.Length; k++)
        {
            if (_entrance[k])
            {
                int yy = k / _nx, xx = k % _nx;
                xs.Add(((xx + 0.5) * _dx / 1000) + _ext.L);
                ys.Add(_ext.T - ((yy + 0.5) * _dx / 1000));
            }
        }
        var m = p.Add.Markers(xs.ToArray(), ys.ToArray(), MarkerShape.FilledCircle, FigureKit.Pt(2), Colors.Magenta); // s=1: 지름 1 pt + 테두리 1 pt
        m.LegendText = "동굴 입구";
        var r = p.Add.Rectangle(rect["x_min"] / 1000, rect["x_max"] / 1000, rect["y_min"] / 1000, rect["y_max"] / 1000);
        r.FillColor = Colors.Transparent;
        r.LineColor = Colors.Red;
        r.LineWidth = FigureKit.Pt(2);
        r.LegendText = "걷는 회랑 (1 × 6 km)";
        FigureKit.AddColorBar(p, FigureColormapData.GistEarth, HeroNorm, _zMin, _zMax, "고도 (m)");
        p.ShowLegend(Alignment.LowerRight);
        FigureKit.Save(Path.Combine(outDir, "hero_map.png"), 11, 10, p);
    }

    /// <summary>6. 땅속 지도: 지표 암석, 흙 두께, 지하수면 깊이, 동굴 층.</summary>
    public void Subsurface(string outDir)
    {
        byte[] rock = Bytes("surface_rock");
        double[] rockRgb = new double[rock.Length * 3];
        for (int k = 0; k < rock.Length; k++)
        {
            for (int c = 0; c < 3; c++)
            {
                rockRgb[(k * 3) + c] = Rocks.ColorRgb[(rock[k] * 3) + c] / 255.0;
            }
        }
        Plot a = MapPanel("지표에 드러난 암석 (솔버가 경사를 정한 바로 그 암석)", Raster.Shade(rockRgb, _z, _ny, _nx, 1.5, _dx, _dx));
        foreach (byte k in rock.Distinct().OrderBy(v => v))
        {
            a.Legend.ManualItems.Add(new LegendItem
            {
                LabelText = Rocks.RockNames[k],
                FillColor = FigureKit.Rgb(Rocks.ColorRgb[k * 3] / 255.0, Rocks.ColorRgb[(k * 3) + 1] / 255.0, Rocks.ColorRgb[(k * 3) + 2] / 255.0),
            });
        }
        a.Legend.FontSize = FigureKit.Pt(8);
        a.ShowLegend(Alignment.LowerRight);

        double[] soil = _hero.Fields.GetFloat64Any("soil_thickness_m");
        Plot b = ScalarMap("흙 두께 (m) — 가파른 비탈은 0 (맨 암반)", soil, FigureColormapData.CopperR);

        double[] wt = _hero.Fields.GetFloat64Any("water_table_m");
        double[] wtd = new double[_z.Length];
        for (int k = 0; k < _z.Length; k++)
        {
            wtd[k] = Math.Clamp(_z[k] - wt[k], 0, 300);
        }
        Plot c2 = ScalarMap("지하수면 깊이 (m) — 강가에서 0, 능선 아래에서 깊음", wtd, FigureColormapData.BluesR);

        double[] cave0 = _hero.Fields.GetFloat64Any("cave_level_0_m");
        double[] cave1 = _hero.Fields.GetFloat64Any("cave_level_1_m");
        double[] cave = new double[_z.Length * 3];
        Array.Fill(cave, 0.92);
        Raster.Paint(cave, Array.ConvertAll(rock, v => Rocks.Soluble[v]), (0.85, 0.85, 0.75));
        Raster.Paint(cave, Array.ConvertAll(cave0, double.IsFinite), (0.15, 0.35, 0.85));
        Raster.Paint(cave, Array.ConvertAll(cave1, double.IsFinite), (0.95, 0.55, 0.1));
        Raster.Paint(cave, _entrance, (0.9, 0.0, 0.6));
        Plot d = MapPanel("동굴 층: 파랑 아래층(잠김), 주황 위층(마름), 분홍 입구, 연한 노랑 녹는 암석", cave);
        FigureKit.Save(Path.Combine(outDir, "hero_subsurface.png"), 14, 13, [a, b, c2, d], 2, 2);
    }

    private Plot ScalarMap(string title, double[] v, double[] lut)
    {
        (double lo, double hi) = Raster.NanMinMax(v);
        double Norm(double x) => Raster.Normalize(x, lo, hi);
        Plot p = MapPanel(title, Raster.Colorize(v, lut, Norm));
        FigureKit.AddColorBar(p, lut, Norm, lo, hi);
        return p;
    }

    /// <summary>
    /// 7. 회랑을 가로지르는 동서 단면 (동굴 입구가 많은 줄). 반환은 summary 의 cross_section.
    /// </summary>
    public OrderedDictionary<string, object?> CrossSection(OrderedDictionary<string, double> rect, Config cfg, string outDir)
    {
        var vol = new HeroVolume(_hero, cfg);
        double cx = 0.5 * (rect["x_min"] + rect["x_max"]);
        var ysInside = new List<double>();
        for (int row = 0; row < _ny; row++)
        {
            bool any = false;
            for (int col = 0; col < _nx && !any; col++)
            {
                any = _entrance[(row * _nx) + col];
            }
            if (any)
            {
                double y = (_ext.T * 1000) - ((row + 0.5) * _dx);
                if (y > rect["y_min"] && y < rect["y_max"])
                {
                    ysInside.Add(y);
                }
            }
        }
        double yCut = ysInside.Count > 0 ? ysInside[ysInside.Count / 2] : 0.5 * (rect["y_min"] + rect["y_max"]);
        const double half = 1500.0;
        double[] xs = NpGrid.Linspace(cx - half, cx + half, 200);
        double[] ys = new double[200];
        Array.Fill(ys, yCut);
        (double sLo, double sHi) = Raster.NanMinMax(vol.SurfaceHeight(xs, ys));
        double zLo = sLo - 120, zHi = sHi + 40;
        (byte[] rgb, int h, int w) = Slices.VerticalSlice(vol, (cx - half, yCut), (cx + half, yCut), zLo, zHi, 1.5);
        SliceFigure.Save(rgb, h, w, 2 * half, zLo, zHi, Path.Combine(outDir, "cross_section.png"));
        return new OrderedDictionary<string, object?>
        {
            ["y_m"] = yCut,
            ["x_m"] = new List<object?> { cx - half, cx + half },
            ["z"] = new List<object?> { zLo, zHi },
        };
    }

    /// <summary>8. 히어로 3D 조감 (4칸마다 표본, 상자 비율 1:1:0.25, elev 35°, azim −60°).</summary>
    public void Bird3D(string outDir)
    {
        const int step = 4;
        int rows = ((_ny - 1) / step) + 1, cols = ((_nx - 1) / step) + 1;
        double[] zs = new double[rows * cols];
        double[] x = new double[rows * cols];
        double[] y = new double[rows * cols];
        bool[] riv = new bool[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int k = (r * cols) + c, src = (r * step * _nx) + (c * step);
                zs[k] = _z[src];
                riv[k] = _river[src];
                x[k] = c * _dx * step / 1000;
                y[k] = -r * _dx * step / 1000;
            }
        }
        double[] face = ShadedHeight(zs, rows, cols, _dx * step, 1.0);
        Raster.Paint(face, riv, RiverRgb);
        double[] zKm = Array.ConvertAll(zs, v => v / 1000);
        (double zl, double zh) = Raster.NanMinMax(zKm);
        var scene = new Scene3D(35, -60, [1.0, 1.0, 0.25],
            [0, (cols - 1) * _dx * step / 1000, -(rows - 1) * _dx * step / 1000, 0, zl, zh]);
        FigureKit.SaveCanvas(Path.Combine(outDir, "hero_3d.png"), 14, 9, "히어로 유역 조감 (고도 과장 없음, 4칸마다 표본)",
            (canvas, area) => scene.DrawSurface(canvas, area, x, y, zKm, rows, cols, face, 0.62));
    }
}
