using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Bpcg.Numerics;
using Bpcg.Volume;

namespace Bpcg.Bake;

/// <summary>
/// 회랑 지표의 프랙탈 디테일 (src/bpcg/bake/detail.py). 보기용 디테일이며 솔버 결과가 아닙니다.
/// 2차원 배열은 (ny, nx) 행 우선입니다.
/// </summary>
public static class Detail
{
    private const long StreamFractal = 7301;
    public const double TaperOctaves = 0.5;
    public const double PadWavelengths = 3.0;
    public const int PadMaxCells = 512;
    public const double SlopeSmoothM = 10.0;
    public const double SoilProbeDepthM = 0.5;
    public const double SoilSmoothM = 6.0;
    public const double WaterRampM = 20.0;
    public const double CaveMarginM = 2.0;
    public const double CaveRampM = 10.0;
    public const double SinkRampM = 6.0;
    public static readonly (double Lo, double Hi) BetaWavelengthM = (4.0, 50.0);
    public const int BinsPerOctave = 4;
    public const string Note = "보기용 디테일, 솔버 결과 아님";

    private static long SubSeed(long seed, long k) => (long)(Hashing.Hash3(seed, StreamFractal, k) >> 1);

    /// <summary>전역 칸 번호의 정수 해시로 만든 표준 정규 흰 잡음 (white_noise).</summary>
    public static double[] WhiteNoise(int ny, int nx, long seed, (long Row0, long Col0) cellOffset = default)
    {
        long sa = SubSeed(seed, 0);
        long sb = SubSeed(seed, 1);
        double twoPi = 2.0 * Math.PI;
        double[] o = new double[ny * nx];
        Parallel.For(0, ny, j =>
        {
            long r = cellOffset.Row0 + j;
            for (int i = 0; i < nx; i++)
            {
                long c = cellOffset.Col0 + i;
                double u1 = Hashing.HashUnit(sa, r, c);
                double u2 = Hashing.HashUnit(sb, r, c);
                o[(j * nx) + i] = Math.Sqrt(-2.0 * Math.Log(1.0 - u1)) * Math.Cos(twoPi * u2);
            }
        });
        return o;
    }

    private static double[] RfftK(int ny, int nx, double spacing)
    {
        double[] ky = Fft.FftFreq(ny, spacing);
        double[] kx = Fft.RfftFreq(nx, spacing);
        int nh = kx.Length;
        double[] k = new double[ny * nh];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nh; i++)
            {
                // TODO(port): np.hypot(libm) 대신 double.Hypot
                k[(j * nh) + i] = double.Hypot(ky[j], kx[i]);
            }
        }
        return k;
    }

    private static double[] RfftWeights(int nx)
    {
        double[] w = Enumerable.Repeat(2.0, (nx / 2) + 1).ToArray();
        w[0] = 1.0;
        if (nx % 2 == 0)
        {
            w[^1] = 1.0;
        }
        return w;
    }

    private static double RaisedCosine(double t)
    {
        t = NpMath.Clip(t, 0.0, 1.0);
        return 0.5 * (1.0 - Math.Cos(Math.PI * t));
    }

    /// <summary>더할 파수 띠 (k_lo, k_hi) [1/m] (band_limits).</summary>
    public static (double KLo, double KHi) BandLimits(double spacingM, double minWavelengthM, double maxWavelengthM) =>
        (1.0 / maxWavelengthM, NpMath.PyMin(1.0 / minWavelengthM, 0.5 / spacingM));

    /// <summary>띠 제한 자기 아핀 잡음 (ny, nx), 표준편차 1 (fractal_field).</summary>
    public static (double[] Field, OrderedDictionary<string, object?> Diag) FractalField(
        int ny, int nx, double spacingM, double minWavelengthM, double maxWavelengthM, double hurst, long seed,
        (long Row0, long Col0) cellOffset = default, double? padM = null)
    {
        double dx = spacingM;
        double h = hurst;
        if (ny < 2 || nx < 2)
        {
            throw new ArgumentException($"shape 는 한 변이 2 이상이어야 합니다: ({ny}, {nx})");
        }
        if (!(double.IsFinite(dx) && dx > 0))
        {
            throw new ArgumentException($"spacing_m 은 0 보다 커야 합니다: {spacingM}");
        }
        if (!(h > 0.0 && h <= 1.0))
        {
            throw new ArgumentException($"hurst 는 0 < H ≤ 1 이어야 합니다: {hurst}");
        }
        if (!(minWavelengthM > 0.0 && minWavelengthM < maxWavelengthM))
        {
            throw new ArgumentException($"0 < min_wavelength_m < max_wavelength_m 이어야 합니다: {minWavelengthM}, {maxWavelengthM}");
        }
        (double kLo, double kHi) = BandLimits(dx, minWavelengthM, maxWavelengthM);
        if (kHi <= kLo)
        {
            throw new ArgumentException($"간격 {IO.PyFormat.General(dx)} m 격자로는 {IO.PyFormat.General(maxWavelengthM)} m 보다 짧은 파장을 그릴 수 없습니다");
        }
        double padLen = padM ?? (PadWavelengths * maxWavelengthM);
        int p = Math.Min(Math.Max((int)Math.Ceiling(padLen / dx), 0), PadMaxCells);
        int nyP = ny + (2 * p);
        int nxP = nx + (2 * p);
        double[] noise = WhiteNoise(nyP, nxP, seed, (cellOffset.Row0 - p, cellOffset.Col0 - p));
        double[] k = RfftK(nyP, nxP, dx);
        bool taperTop = 1.0 / minWavelengthM < 0.5 / dx * (1.0 - 1e-9);
        int nh = (nxP / 2) + 1;
        double[] amp = new double[k.Length];
        double[] powerLaw = new double[k.Length];
        for (int q = 0; q < k.Length; q++)
        {
            double kk = k[q] > 0 ? k[q] : 1.0;
            bool inside = k[q] >= kLo && k[q] <= kHi && k[q] > 0;
            double taper = RaisedCosine(Math.Log2(kk / kLo) / TaperOctaves);
            if (taperTop)
            {
                taper *= RaisedCosine(Math.Log2(kHi / kk) / TaperOctaves);
            }
            powerLaw[q] = inside ? Math.Pow(kk, -(1.0 + h)) : 0.0;
            amp[q] = (inside ? taper : 0.0) * powerLaw[q];
        }
        double[] w = RfftWeights(nxP);
        double nAll = (double)nyP * nxP;
        double[] terms = new double[k.Length];
        double[] topTerms = new double[k.Length];
        for (int q = 0; q < k.Length; q++)
        {
            double wq = w[q % nh];
            terms[q] = wq * amp[q] * amp[q];
            bool top = k[q] >= kLo && k[q] < 2.0 * kLo;
            double pl = top ? powerLaw[q] : 0.0;
            topTerms[q] = wq * (pl * pl);
        }
        double var = NpReduce.Sum(terms) / nAll;
        if (!(var > 0.0))
        {
            throw new ArgumentException(
                $"[{IO.PyFormat.General(1.0 / kHi, 3)}, {IO.PyFormat.General(1.0 / kLo, 3)}] m 띠에 이 격자({nyP}×{nxP}, {IO.PyFormat.General(dx)} m)의 FFT 칸이 없습니다. min_wavelength_m 과 max_wavelength_m 사이를 넓히세요");
        }
        double sigma = Math.Sqrt(var);
        double topVar = NpReduce.Sum(topTerms) / nAll;
        Complex[] spec = Fft.Rfft2(noise, nyP, nxP);
        for (int q = 0; q < spec.Length; q++)
        {
            spec[q] *= amp[q];
        }
        double[] full = Fft.Irfft2(spec, nyP, nxP);
        double[] field = new double[ny * nx];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                field[(j * nx) + i] = full[((j + p) * nxP) + i + p] / sigma;
            }
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["pad_cells"] = (long)p,
            ["k_lo_per_m"] = kLo,
            ["k_hi_per_m"] = kHi,
            ["min_wavelength_eff_m"] = 1.0 / kHi,
            ["sigma_raw"] = sigma,
            ["top_octave_rms_per_std"] = Math.Sqrt(topVar) / sigma,
            ["taper_top"] = taperTop,
            ["seed_stream"] = StreamFractal,
        };
        return (field, diag);
    }

    private static double[] DetrendPlane(double[] z, int ny, int nx)
    {
        double[] ic = new double[nx];
        double[] jc = new double[ny];
        for (int i = 0; i < nx; i++)
        {
            ic[i] = i - (0.5 * (nx - 1));
        }
        for (int j = 0; j < ny; j++)
        {
            jc[j] = j - (0.5 * (ny - 1));
        }
        // TODO(port): numpy 는 z @ ic 를 BLAS 로 계산합니다(덧셈 순서가 다를 수 있음).
        double[] zic = new double[ny];
        for (int j = 0; j < ny; j++)
        {
            double s = 0.0;
            for (int i = 0; i < nx; i++)
            {
                s += z[(j * nx) + i] * ic[i];
            }
            zic[j] = s;
        }
        double[] jcz = new double[nx];
        for (int i = 0; i < nx; i++)
        {
            double s = 0.0;
            for (int j = 0; j < ny; j++)
            {
                s += jc[j] * z[(j * nx) + i];
            }
            jcz[i] = s;
        }
        double icic = 0.0;
        foreach (double v in ic)
        {
            icic += v * v;
        }
        double jcjc = 0.0;
        foreach (double v in jc)
        {
            jcjc += v * v;
        }
        double b = NpReduce.Sum(zic) / (ny * icic);
        double c = NpReduce.Sum(jcz) / (nx * jcjc);
        double mean = NpReduce.Mean(z);
        double[] o = new double[z.Length];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                o[(j * nx) + i] = z[(j * nx) + i] - (mean + (b * ic[i]) + (c * jc[j]));
            }
        }
        return o;
    }

    private static double[] Hanning(int m)
    {
        if (m == 1)
        {
            return [1.0];
        }
        double[] o = new double[m];
        for (int k = 0; k < m; k++)
        {
            double n = (1 - m) + (2 * k);
            o[k] = 0.5 + (0.5 * Math.Cos(Math.PI * n / (m - 1)));
        }
        return o;
    }

    private static (double[] P, double[] W, double[] K, int Nh) Periodogram(double[] z, int ny, int nx, double spacingM)
    {
        if (Math.Min(ny, nx) < 4)
        {
            throw new ArgumentException($"z 는 한 변이 4 이상인 2차원 배열이어야 합니다: ({ny}, {nx})");
        }
        if (z.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("z 에 NaN 이나 inf 가 있습니다");
        }
        double[] hy = Hanning(ny);
        double[] hx = Hanning(nx);
        double[] win = new double[ny * nx];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                win[(j * nx) + i] = hy[j] * hx[i];
            }
        }
        double[] dt = DetrendPlane(z, ny, nx);
        double[] prod = new double[z.Length];
        double[] w2 = new double[z.Length];
        for (int q = 0; q < z.Length; q++)
        {
            prod[q] = dt[q] * win[q];
            w2[q] = win[q] * win[q];
        }
        Complex[] f = Fft.Rfft2(prod, ny, nx);
        double denom = ((double)z.Length * z.Length) * NpReduce.Mean(w2);
        double[] p = new double[f.Length];
        for (int q = 0; q < f.Length; q++)
        {
            p[q] = ((f[q].Real * f[q].Real) + (f[q].Imaginary * f[q].Imaginary)) / denom;
        }
        int nh = (nx / 2) + 1;
        double[] wRow = RfftWeights(nx);
        double[] wAll = new double[f.Length];
        for (int q = 0; q < f.Length; q++)
        {
            wAll[q] = wRow[q % nh];
        }
        return (p, wAll, RfftK(ny, nx, spacingM), nh);
    }

    /// <summary>파장 [lamLo, lamHi] 띠의 RMS [m] (band_rms).</summary>
    public static double BandRms(double[] z, int ny, int nx, double spacingM, double lamLoM, double lamHiM)
    {
        (double[] p, double[] w, double[] k, _) = Periodogram(z, ny, nx, spacingM);
        var sel = new List<double>();
        for (int q = 0; q < p.Length; q++)
        {
            if (k[q] >= 1.0 / lamHiM && k[q] <= 1.0 / lamLoM)
            {
                sel.Add(p[q] * w[q]);
            }
        }
        return Math.Sqrt(NpReduce.Sum(sel.ToArray()));
    }

    /// <summary>방사 평균 2D 파워 스펙트럼 P(k) ∝ k^−β 의 β (psd_slope). 칸이 3개보다 적으면 NaN.</summary>
    public static double PsdSlope(double[] z, int ny, int nx, double spacingM, double lamLoM, double lamHiM)
    {
        (double[] p, _, double[] k, _) = Periodogram(z, ny, nx, spacingM);
        double kLo = 1.0 / lamHiM;
        double kHi = NpMath.PyMin(1.0 / lamLoM, 0.5 / spacingM);
        if (kHi <= kLo)
        {
            return double.NaN;
        }
        int nBins = Math.Max((int)Math.Ceiling(Math.Log2(kHi / kLo) * BinsPerOctave), 1);
        double[] edges = new double[nBins + 1];
        for (int i = 0; i <= nBins; i++)
        {
            edges[i] = kLo * Math.Pow(kHi / kLo, (double)i / nBins);
        }
        double[] cnt = new double[nBins];
        double[] sumP = new double[nBins];
        double[] sumLogK = new double[nBins];
        for (int q = 0; q < p.Length; q++)
        {
            if (!(k[q] >= kLo && k[q] <= kHi && k[q] > 0))
            {
                continue;
            }
            int idx = Math.Clamp(SciPy.SearchSorted(edges, k[q], true) - 1, 0, nBins - 1);
            cnt[idx] += 1;
            sumP[idx] += p[q];
            sumLogK[idx] += Math.Log10(k[q]);
        }
        var lx = new List<double>();
        var ly = new List<double>();
        for (int b = 0; b < nBins; b++)
        {
            if (cnt[b] > 0 && sumP[b] > 0)
            {
                lx.Add(sumLogK[b] / cnt[b]);
                ly.Add(Math.Log10(sumP[b] / cnt[b]));
            }
        }
        if (lx.Count < 3)
        {
            return double.NaN;
        }
        (double slope, _) = SciPy.Polyfit1(lx.ToArray(), ly.ToArray());
        return -slope;
    }

    /// <summary>가운데 정사각 창 (central_window). 반환: (창, 한 변).</summary>
    public static (double[] Window, int Side) CentralWindow(double[] z, int ny, int nx)
    {
        int s = Math.Min(ny, nx);
        int r0 = (ny - s) / 2;
        int c0 = (nx - s) / 2;
        double[] o = new double[s * s];
        for (int j = 0; j < s; j++)
        {
            Array.Copy(z, ((r0 + j) * nx) + c0, o, j * s, s);
        }
        return (o, s);
    }

    private static int[] GridNeighbors(int ny, int nx)
    {
        int n = ny * nx;
        int[] nbr = new int[n * 8];
        Array.Fill(nbr, -1);
        for (int c = 0; c < n; c++)
        {
            int jj = c / nx;
            int ii = c % nx;
            for (int s = 0; s < 8; s++)
            {
                (int dj, int di) = Cubesphere.NeighborSlots[s];
                int j2 = jj + dj;
                int i2 = ii + di;
                if (j2 >= 0 && j2 < ny && i2 >= 0 && i2 < nx)
                {
                    nbr[(c * 8) + s] = (j2 * nx) + i2;
                }
            }
        }
        return nbr;
    }

    /// <summary>웅덩이 채우기의 기본 출구: 물 칸과 회랑 가장자리 칸 (edge_outlets).</summary>
    public static bool[] EdgeOutlets(bool[] wet, int ny, int nx)
    {
        bool[] o = (bool[])wet.Clone();
        for (int i = 0; i < nx; i++)
        {
            o[i] = true;
            o[((ny - 1) * nx) + i] = true;
        }
        for (int j = 0; j < ny; j++)
        {
            o[j * nx] = true;
            o[(j * nx) + nx - 1] = true;
        }
        return o;
    }

    /// <summary>원래 지표의 닫힌 웅덩이 칸 (base_sinks).</summary>
    public static bool[] BaseSinks(double[] surf, bool[] wet, int ny, int nx, int[]? nbr = null)
    {
        nbr ??= GridNeighbors(ny, nx);
        double[] filled = Depressions.FillDepressions(surf, nbr, EdgeOutlets(wet, ny, nx));
        bool[] o = new bool[surf.Length];
        for (int q = 0; q < o.Length; q++)
        {
            o[q] = filled[q] > surf[q];
        }
        return o;
    }

    /// <summary>설정 [detail] 을 읽어 검사합니다 (detail_settings). 절이 없거나 fractal_gain ≤ 0 이면 null.</summary>
    public static OrderedDictionary<string, double>? DetailSettings(Config cfg)
    {
        if (!cfg.Contains("detail"))
        {
            return null;
        }
        Section det = cfg.Sec("detail");
        if (!det.Contains("fractal_gain"))
        {
            return null;
        }
        var s = new OrderedDictionary<string, double>
        {
            ["gain"] = det.F("fractal_gain"),
            ["hurst"] = det.F("hurst"),
            ["min_wavelength_m"] = det.F("min_wavelength_m"),
            ["max_wavelength_m"] = det.F("max_wavelength_m"),
            ["soil_factor"] = det.F("soil_factor"),
            ["slope_ref"] = det.F("slope_ref"),
            ["water_margin_m"] = det.F("water_margin_m"),
            ["edge_fade_m"] = det.Contains("edge_fade_m") ? det.F("edge_fade_m") : det.F("max_wavelength_m"),
        };
        if (s.Values.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("detail 설정에 NaN 이나 inf 가 있습니다");
        }
        if (s["gain"] <= 0)
        {
            return null;
        }
        if (!(s["hurst"] > 0.0 && s["hurst"] <= 1.0))
        {
            throw new ArgumentException($"detail.hurst 는 0 < H ≤ 1 이어야 합니다: {s["hurst"]}");
        }
        if (!(s["min_wavelength_m"] > 0.0 && s["min_wavelength_m"] < s["max_wavelength_m"]))
        {
            throw new ArgumentException(
                $"detail 은 0 < min_wavelength_m < max_wavelength_m 이어야 합니다: {s["min_wavelength_m"]}, {s["max_wavelength_m"]}");
        }
        if (!(s["soil_factor"] >= 0.0 && s["soil_factor"] <= 1.0))
        {
            throw new ArgumentException($"detail.soil_factor 는 0~1 이어야 합니다: {s["soil_factor"]}");
        }
        if (s["slope_ref"] <= 0 || s["water_margin_m"] < 0 || s["edge_fade_m"] < 0)
        {
            throw new ArgumentException("detail.slope_ref 는 0 보다 크고 water_margin_m, edge_fade_m 은 0 이상이어야 합니다");
        }
        return s;
    }

    // np.gradient(f, dx) 의 (축 0, 축 1) 미분 (안쪽은 중앙 차분, 끝은 한쪽 차분)
    private static (double[] Gy, double[] Gx) Gradient(double[] f, int ny, int nx, double dx)
    {
        double[] gy = new double[f.Length];
        double[] gx = new double[f.Length];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = (j * nx) + i;
                gy[c] = j == 0 ? (f[c + nx] - f[c]) / dx
                    : j == ny - 1 ? (f[c] - f[c - nx]) / dx
                    : (f[c + nx] - f[c - nx]) / (2.0 * dx);
                gx[c] = i == 0 ? (f[c + 1] - f[c]) / dx
                    : i == nx - 1 ? (f[c] - f[c - 1]) / dx
                    : (f[c + 1] - f[c - 1]) / (2.0 * dx);
            }
        }
        return (gy, gx);
    }

    /// <summary>디테일 계수 m = 경사 항 × 흙 항 × 물 항 (detail_weight). 반환: (m, 흙 칸).</summary>
    public static (double[] M, bool[] Soil) DetailWeight(
        double[] surf, double[] gx, double[] gy, int ny, int nx, double voxelM, bool[] wet, HeroVolume vol, OrderedDictionary<string, double> settings)
    {
        double dx = voxelM;
        double[] smooth = SciPy.GaussianFilter2DNearest(surf, ny, nx, SlopeSmoothM / dx);
        (double[] gyy, double[] gxx) = Gradient(smooth, ny, nx, dx);
        double[] mSlope = new double[surf.Length];
        double[] probe = new double[surf.Length];
        for (int q = 0; q < surf.Length; q++)
        {
            double slope = double.Hypot(gxx[q], gyy[q]);
            mSlope[q] = NpMath.Clip(slope / settings["slope_ref"], 0.0, 1.0);
            probe[q] = surf[q] - SoilProbeDepthM;
        }
        byte[] mat = (byte[])vol.EvaluateGrid(gx, gy, probe, 1, double.PositiveInfinity, "solid_material")["solid_material"];
        bool[] soil = new bool[surf.Length];
        double[] soilW = new double[surf.Length];
        for (int q = 0; q < surf.Length; q++)
        {
            soil[q] = mat[q] == Rocks.Soil || mat[q] == Rocks.Alluvium;
            soilW[q] = soil[q] ? settings["soil_factor"] : 1.0;
        }
        double[] mSoil = SciPy.GaussianFilter2DNearest(soilW, ny, nx, SoilSmoothM / dx);
        double[] mWater;
        if (wet.Any(b => b))
        {
            double[] dist = SciPy.DistanceTransformEdt(Array.ConvertAll(wet, b => !b), ny, nx);
            mWater = new double[surf.Length];
            for (int q = 0; q < surf.Length; q++)
            {
                mWater[q] = wet[q] ? 0.0 : NpMath.Clip(((dist[q] * dx) - settings["water_margin_m"]) / WaterRampM, 0.0, 1.0);
            }
        }
        else
        {
            mWater = Enumerable.Repeat(1.0, surf.Length).ToArray();
        }
        double[] m = new double[surf.Length];
        for (int q = 0; q < m.Length; q++)
        {
            m[q] = mSlope[q] * mSoil[q] * mWater[q];
        }
        return (m, soil);
    }

    private static double[] RampFrom(bool[] mask, int ny, int nx, double voxelM, double marginM, double rampM)
    {
        if (!mask.Any(b => b))
        {
            return Enumerable.Repeat(1.0, mask.Length).ToArray();
        }
        double[] dist = SciPy.DistanceTransformEdt(Array.ConvertAll(mask, b => !b), ny, nx);
        double[] o = new double[mask.Length];
        for (int q = 0; q < o.Length; q++)
        {
            o[q] = mask[q] ? 0.0 : NpMath.Clip(((dist[q] * voxelM) - marginM) / rampM, 0.0, 1.0);
        }
        return o;
    }

    private static double[] EdgeRamp(int ny, int nx, double voxelM, double fadeM)
    {
        double[] o = new double[ny * nx];
        if (fadeM <= 0.0)
        {
            Array.Fill(o, 1.0);
            return o;
        }
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int r = Math.Min(j, ny - 1 - j);
                int c = Math.Min(i, nx - 1 - i);
                o[(j * nx) + i] = NpMath.Clip(Math.Min(r, c) * voxelM / fadeM, 0.0, 1.0);
            }
        }
        return o;
    }

    /// <summary>
    /// 회랑 지표 surf 에 프랙탈 디테일을 더하고 웅덩이를 채웁니다 (add_fractal_detail). 반환: (surf_detail, meta).
    /// </summary>
    public static (double[] Surface, OrderedDictionary<string, object?> Meta) AddFractalDetail(
        double[] surf, double[] gx, double[] gy, int ny, int nx, double voxelM, bool[] wet, HeroVolume vol, Config cfg, double[]? mouth = null)
    {
        OrderedDictionary<string, double> st = DetailSettings(cfg)
            ?? throw new ArgumentException("detail.fractal_gain 이 0 보다 커야 프랙탈 디테일을 더합니다");
        if (surf.Length != ny * nx || wet.Length != surf.Length || gx.Length != surf.Length)
        {
            throw new ArgumentException($"surf, wet, gx, gy 는 같은 2차원 모양이어야 합니다: ({ny}, {nx})");
        }
        double dx = voxelM;
        double lamMax = st["max_wavelength_m"];
        double hurst = st["hurst"];
        double anchor = BandRms(surf, ny, nx, dx, lamMax, 2.0 * lamMax);
        (long, long) offset = ((long)Math.Round(-gy[0] / dx), (long)Math.Round(gx[0] / dx));
        (double[] field, OrderedDictionary<string, object?> fdiag) = FractalField(
            ny, nx, dx, st["min_wavelength_m"], lamMax, hurst, cfg.I("planet.seed"), offset);
        double ampM = st["gain"] * anchor * Math.Pow(2.0, -hurst) / (double)fdiag["top_octave_rms_per_std"]!;
        (double[] m, bool[] soil) = DetailWeight(surf, gx, gy, ny, nx, dx, wet, vol, st);
        int[] nbr = GridNeighbors(ny, nx);
        bool[] sink = BaseSinks(surf, wet, ny, nx, nbr);
        bool[] cave = mouth is null ? new bool[surf.Length] : Array.ConvertAll(mouth, v => v < 0.0);
        double[] rc = RampFrom(cave, ny, nx, dx, CaveMarginM, CaveRampM);
        double[] rs = RampFrom(sink, ny, nx, dx, 0.0, SinkRampM);
        double[] re = EdgeRamp(ny, nx, dx, st["edge_fade_m"]);
        double[] z = new double[surf.Length];
        bool[] outlet = EdgeOutlets(wet, ny, nx);
        for (int q = 0; q < z.Length; q++)
        {
            m[q] = m[q] * rc[q] * rs[q] * re[q];
            z[q] = surf[q] + (ampM * m[q] * field[q]);
            if (wet[q] || sink[q] || cave[q])
            {
                z[q] = surf[q];
            }
            outlet[q] |= sink[q] || cave[q];
        }
        double[] filled = Depressions.FillDepressions(z, nbr, outlet);
        long nFilled = 0;
        double[] diffSq = new double[z.Length];
        double maxAbs = 0.0;
        for (int q = 0; q < z.Length; q++)
        {
            nFilled += filled[q] > z[q] ? 1 : 0;
            if (wet[q])
            {
                filled[q] = surf[q];
            }
            double d = filled[q] - surf[q];
            diffSq[q] = d * d;
            maxAbs = Math.Max(maxAbs, Math.Abs(d));
        }
        (double lamLo, double lamHi) = BetaWavelengthM;
        (double[] wb, int sb) = CentralWindow(surf, ny, nx);
        (double[] wa, int sa) = CentralWindow(filled, ny, nx);
        var meta = new OrderedDictionary<string, object?>
        {
            ["gain"] = st["gain"],
            ["hurst"] = hurst,
            ["min_wavelength_m"] = st["min_wavelength_m"],
            ["max_wavelength_m"] = lamMax,
            ["min_wavelength_eff_m"] = fdiag["min_wavelength_eff_m"],
            ["anchor_rms_m"] = anchor,
            ["amplitude_m"] = ampM,
            ["rms_m"] = Math.Sqrt(NpReduce.Mean(diffSq)),
            ["max_abs_m"] = maxAbs,
            ["beta_before"] = PsdSlope(wb, sb, sb, dx, lamLo, lamHi),
            ["beta_after"] = PsdSlope(wa, sa, sa, dx, lamLo, lamHi),
            ["beta_wavelength_m"] = new List<object?> { lamLo, lamHi },
            ["n_filled"] = nFilled,
            ["n_base_sink_cells"] = (long)sink.Count(b => b),
            ["mean_weight"] = NpReduce.Mean(m),
            ["soil_fraction"] = (double)soil.Count(b => b) / soil.Length,
            ["soil_factor"] = st["soil_factor"],
            ["slope_ref"] = st["slope_ref"],
            ["water_margin_m"] = st["water_margin_m"],
            ["edge_fade_m"] = st["edge_fade_m"],
            ["n_cave_mouth_cells"] = (long)cave.Count(b => b),
            ["cell_offset"] = new List<object?> { offset.Item1, offset.Item2 },
            ["seed_stream"] = StreamFractal,
            ["note"] = Note,
        };
        return (filled, meta);
    }
}
