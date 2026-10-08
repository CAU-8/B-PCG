using System;
using System.Collections.Generic;
using System.IO;
using Bpcg.Numerics;
using ScottPlot;

namespace Bpcg.Figures;

/// <summary>
/// 행성 그림 네 가지 (render_results.py 의 1~4): 고도 지도, 지구본 세 방향, 판·나이·강수·융기, 고도 분포.
/// </summary>
internal static class PlanetFigures
{
    private const double VMin = -7000, VCenter = 0, VMax = 6000;
    private static readonly (double R, double G, double B) EdgeRed = (1.0, 0.15, 0.15);

    private static double TerrainNorm(double z) => Raster.TwoSlope(z, VMin, VCenter, VMax);

    /// <summary>고도 → 색표 → 음영 (shaded).</summary>
    private static double[] ShadedTerrain(double[] z, int h, int w, double dx, double vert) =>
        Raster.Shade(Raster.DropAlpha(Raster.Colorize(z, FigureColormapData.Terrain, TerrainNorm)), z, h, w, vert, dx, dx);

    /// <summary>1. 행성 평균 지표 고도 지도 (면 경계 없이 / 겹쳐서).</summary>
    public static void Elevation(double[] zm, int n, (long[] Cells, int H, int W) eq, string outDir)
    {
        (long[] cells, int h, int w) = eq;
        double[] img = Projections.Sample(zm, cells);
        bool[] edges = Raster.FaceEdges(Projections.Faces(cells, n), h, w);
        double dxDeg = 360.0 / w * 111e3;
        double[] rgb = ShadedTerrain(img, h, w, dxDeg, 8);
        foreach ((string tag, bool withEdges) in new[] { ("plain", false), ("faces", true) })
        {
            double[] show = (double[])rgb.Clone();
            string title = $"B-PCG 행성 평균 지표 고도 (L0 면당 {n}칸, 기복 보정 포함)";
            if (withEdges)
            {
                Raster.Paint(show, edges, EdgeRed);
                title += " — 빨간 선: 정육면체 면 경계";
            }
            Plot p = FigureKit.NewPlot(title, "경도 (°)", "위도 (°)");
            FigureKit.AddImage(p, FigureKit.ToImage(show, h, w), -180, 180, -90, 90);
            FigureKit.AddColorBar(p, FigureColormapData.Terrain, TerrainNorm, VMin, VMax, "고도 (m)");
            FigureKit.Save(Path.Combine(outDir, $"planet_elevation_{tag}.png"), 16, 8.4, p);
        }
    }

    /// <summary>2. 같은 행성을 세 방향에서 본 지구본 (위 줄 그대로, 아래 줄 면 경계).</summary>
    public static void Globes(double[] zm, int n, double[]? siteUnit, string outDir)
    {
        const int size = 900;
        var views = new (string Label, double[] Dir)[]
        {
            ("히어로 유역 쪽", siteUnit ?? [1.0, 0.0, 0.0]),
            ("정육면체 꼭짓점 쪽 (세 면이 만나는 곳)", [1.0, 1.0, 1.0]),
            ("면 가운데 쪽 (+Z, 북극)", [0.2, 0.0, 1.0]),
        };
        var plots = new Plot[6];
        for (int k = 0; k < views.Length; k++)
        {
            (long[] cells, bool[] inside) = Projections.Orthographic(n, views[k].Dir, size);
            double[] g = Projections.Sample(zm, cells);
            for (int i = 0; i < g.Length; i++)
            {
                if (!inside[i])
                {
                    g[i] = 0.0; // np.nan_to_num(NaN) = 0
                }
            }
            double[] rgb = ShadedTerrain(g, size, size, 6371e3 * 2 / size, 10);
            bool[] outside = Array.ConvertAll(inside, v => !v);
            Raster.Paint(rgb, outside, (1.0, 1.0, 1.0));
            double[] withEdges = (double[])rgb.Clone();
            bool[] edges = Raster.FaceEdges(Projections.Faces(cells, n), size, size);
            for (int i = 0; i < edges.Length; i++)
            {
                edges[i] &= inside[i];
            }
            Raster.Paint(withEdges, edges, EdgeRed);
            plots[k] = GlobePanel(rgb, size, views[k].Label, k == 0 && siteUnit is not null);
            plots[3 + k] = GlobePanel(withEdges, size, views[k].Label + " + 면 경계", k == 0 && siteUnit is not null);
        }
        FigureKit.Save(Path.Combine(outDir, "planet_globes.png"), 16, 11, plots, 2, 3,
            "같은 행성을 세 방향에서 본 모습. 아래 줄의 빨간 선이 정육면체 면 경계입니다");
    }

    private static Plot GlobePanel(double[] rgb, int size, string title, bool markCenter)
    {
        Plot p = FigureKit.NewPlot(title);
        FigureKit.AddImage(p, FigureKit.ToImage(rgb, size, size), 0, size, 0, size);
        if (markCenter)
        {
            var m = p.Add.Marker(size / 2.0, size / 2.0, MarkerShape.OpenCircle, FigureKit.Pt(14), Colors.Yellow);
            m.MarkerLineWidth = FigureKit.Pt(1);
        }
        FigureKit.AxisOff(p);
        return p;
    }

    /// <summary>3. 판·경계·해안, 해양저 나이, 연강수량, 융기 속도.</summary>
    public static void Layers(
        double[] plate, byte[] boundary, double[] ageMyr, double[] precip, double[] uplift, bool[] ocean,
        (long[] Cells, int H, int W) eq, string outDir)
    {
        (long[] cells, int h, int w) = eq;
        double[] plateImg = Projections.Sample(plate, cells);
        double[] oceanImg = Projections.Sample(Array.ConvertAll(ocean, v => v ? 1.0 : 0.0), cells);
        double[] ageImg = Projections.Sample(ageMyr, cells);
        double[] pImg = Projections.Sample(precip, cells);
        double[] uImg = Projections.Sample(uplift, cells);

        // 판 색: imshow(plate % 20, cmap="tab20") 는 값 범위를 자료의 최소~최대로 잡습니다.
        double[] mod = Array.ConvertAll(plateImg, v => (((v % 20) + 20) % 20));
        (double pmin, double pmax) = Raster.NanMinMax(mod);
        double[] plateRgb = Raster.DropAlpha(Raster.Colorize(mod, FigureColormapData.Tab20, v => Raster.Normalize(v, pmin, pmax)));
        Plot a = FigureKit.NewPlot("판 (색) · 경계: 빨강 수렴, 파랑 발산, 회색 변환 · 검은 선 해안");
        FigureKit.AddImage(a, FigureKit.ToImage(plateRgb, h, w), -180, 180, -90, 90, smooth: false);
        // 경계 칸 픽셀마다 점 (scatter s=0.3: 지름 0.55 pt + 테두리 1 pt). 빨강 수렴, 파랑 발산, 회색 변환 순서로 덮어 찍음.
        foreach ((byte t, string col) in new[] { ((byte)1, "#ff0000"), ((byte)2, "#00bfff"), ((byte)3, "#d3d3d3") })
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < cells.Length; i++)
            {
                if (boundary[cells[i]] == t)
                {
                    xs.Add(((i % w) / (double)w * 360) - 180);
                    ys.Add(90 - ((i / w) / (double)h * 180));
                }
            }
            if (xs.Count > 0)
            {
                a.Add.Markers(xs.ToArray(), ys.ToArray(), MarkerShape.FilledCircle, FigureKit.Pt(1.55), Color.FromHex(col));
            }
        }
        a.Add.Plottable(new SegmentsPlot(Contours.Segments(oceanImg, h, w, 0.5, -180, 180, 90, -90), Colors.Black, FigureKit.Pt(0.4)));

        (double amin, double amax) = Raster.NanMinMax(ageImg);
        Plot b = ScalarPanel("해양저 나이 (Myr) — 해령에서 멀수록 늙음", ageImg, h, w, FigureColormapData.ViridisR, amin, amax);
        (double prMin, _) = Raster.NanMinMax(pImg);
        Plot c = ScalarPanel("연강수량 (m/yr) — 적도와 중위도 띠, 30° 근처 건조", pImg, h, w, FigureColormapData.YlGnBu, prMin, 3);
        double[] landUplift = new double[uImg.Length];
        for (int i = 0; i < uImg.Length; i++)
        {
            landUplift[i] = oceanImg[i] > 0 ? double.NaN : uImg[i] * 1e3;
        }
        Plot d = ScalarPanel("융기 속도 (mm/yr, 지각 세기 한계 적용 후)", landUplift, h, w, FigureColormapData.Magma, 0, 2);
        FigureKit.Save(Path.Combine(outDir, "planet_layers.png"), 17, 9.5, [a, b, c, d], 2, 2);
    }

    private static Plot ScalarPanel(string title, double[] img, int h, int w, double[] lut, double vmin, double vmax)
    {
        Plot p = FigureKit.NewPlot(title);
        double Norm(double v) => Raster.Normalize(v, vmin, vmax);
        FigureKit.AddImage(p, FigureKit.ToImage(Raster.Colorize(img, lut, Norm), h, w), -180, 180, -90, 90);
        FigureKit.AddColorBar(p, lut, Norm, vmin, vmax);
        return p;
    }

    /// <summary>
    /// 4. 고도 분포: B-PCG (셀 면적 가중) 와 지구 (ETOPO 2022, 위도 cos 가중). ETOPO 파일이 없으면 B-PCG 만.
    /// summary 에 earth_ocean_fraction (ETOPO 가 있을 때) 를 더합니다.
    /// </summary>
    public static void Hypsometry(double[] zm, double[] area, string? etopoPath, string outDir, OrderedDictionary<string, object?> summary)
    {
        double[] bins = new double[81];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = -11000 + (250.0 * i);
        }
        double aSum = NpReduce.Sum(area);
        double[] hb = Histogram(zm, Array.ConvertAll(area, v => v / aSum), bins);
        Plot p = FigureKit.NewPlot("고도 분포: 대륙과 해양 두 봉우리 (설계도 7장의 '반쯤 입력' 검사)", "고도 (m)", "면적 비율 (%, 250 m 구간)");
        AddStep(p, bins, hb, "B-PCG (L0 평균 지표)", Color.FromHex("#ff7f0e"));
        if (etopoPath is not null && File.Exists(etopoPath))
        {
            (double[] z, double[] lat, int ny, int nx) = Etopo.ReadStrided(etopoPath, 10);
            double[] wt = new double[z.Length];
            for (int r = 0; r < ny; r++)
            {
                double cw = Math.Cos(lat[r] * Math.PI / 180.0);
                for (int c = 0; c < nx; c++)
                {
                    wt[(r * nx) + c] = cw;
                }
            }
            double wSum = NpReduce.Sum(wt);
            double[] he = Histogram(z, Array.ConvertAll(wt, v => v / wSum), bins);
            AddStep(p, bins, he, "지구 (ETOPO 2022)", Color.FromHex("#1f77b4"));
            double[] wOcean = new double[z.Length];
            for (int i = 0; i < z.Length; i++)
            {
                wOcean[i] = z[i] < 0 ? wt[i] : 0.0;
            }
            summary["earth_ocean_fraction"] = NpReduce.Sum(wOcean) / wSum;
        }
        p.ShowLegend(Alignment.UpperRight);
        p.ShowGrid();
        p.Grid.MajorLineColor = Color.FromHex("#b0b0b0").WithAlpha(0.3);
        FigureKit.Save(Path.Combine(outDir, "hypsometry_vs_earth.png"), 10, 5.2, p);
    }

    private static void AddStep(Plot p, double[] bins, double[] h, string label, Color color)
    {
        double[] xs = bins[..^1];
        double[] ys = Array.ConvertAll(h, v => v * 100);
        var s = p.Add.Scatter(xs, ys, color);
        s.ConnectStyle = ConnectStyle.StepHorizontal;
        s.MarkerSize = 0;
        s.LineWidth = FigureKit.Pt(1.5);
        s.LegendText = label;
    }

    /// <summary>np.histogram(v, bins, weights): [e_i, e_{i+1}) 구간 (마지막은 닫힘), 범위 밖·NaN 은 버림.</summary>
    private static double[] Histogram(double[] v, double[] weights, double[] bins)
    {
        int nb = bins.Length - 1;
        double[] h = new double[nb];
        double lo = bins[0], hi = bins[^1], width = bins[1] - bins[0];
        for (int i = 0; i < v.Length; i++)
        {
            double x = v[i];
            if (!(x >= lo && x <= hi))
            {
                continue;
            }
            int k = x == hi ? nb - 1 : Math.Min((int)((x - lo) / width), nb - 1);
            if (x < bins[k])
            {
                k--;
            }
            else if (k + 1 < nb && x >= bins[k + 1])
            {
                k++;
            }
            h[k] += weights[i];
        }
        return h;
    }
}
