using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Metrics;
using Bpcg.Numerics;

namespace Bpcg.Compare;

/// <summary>지표 계산 재료 (고도 하나에 한 번 만듭니다).</summary>
public sealed class Analysis
{
    public required double[] Z { get; init; }

    public required int N { get; init; }

    public required double Dx { get; init; }

    public required CellGraph Graph { get; init; }

    /// <summary>웅덩이를 넘침 높이까지 채운 고도 [m].</summary>
    public required double[] Zf { get; init; }

    /// <summary>ε 채움 고도 (물길용) [m].</summary>
    public required double[] Zt { get; init; }

    public required long[] Rcv { get; init; }

    public required long[] Order { get; init; }

    /// <summary>상류 면적 [m²].</summary>
    public required double[] AreaUp { get; init; }

    /// <summary>수신 셀까지 거리 [m].</summary>
    public required double[] DistR { get; init; }

    /// <summary>경사 크기 [m/m] (numpy.gradient 꼴 중앙 차분).</summary>
    public required double[] Slope { get; init; }

    /// <summary>하천 칸 (상류 면적 ≥ 문턱, 출구 아님).</summary>
    public required bool[] Channel { get; init; }
}

/// <summary>지표 하나. Fn 은 잴 수 없으면 NaN 을 돌려줍니다.</summary>
public sealed record CompareMetric(string Name, string Label, string Unit, string Group, string Help, Func<Analysis, double> Fn)
{
    public OrderedDictionary<string, object?> Describe() => new()
    {
        ["name"] = Name,
        ["label"] = Label,
        ["unit"] = Unit,
        ["help"] = Help,
        ["group"] = Group,
    };
}

/// <summary>
/// 비교 지표: 모든 방법의 고도에 같은 코드로 잽니다 (docs/compare.md 6장).
/// </summary>
/// <remarks>
/// 물길은 같은 평면 격자(노드 흔들기 없음)에서 네 가장자리를 출구로 두고 이 저장소의 Hydro 코드로 다시 계산합니다. 그래서 방법이
/// 경계를 어떻게 다뤘는지와 상관없이 같은 잣대입니다. Galin 2019 6.2 가 말하는 '물리적 일관성 검사'(물을 흘려 물길이
/// 이어지는지)와 지형 통계가 중심입니다. 새 지표는 <see cref="All"/> 에 한 줄을 더합니다. B-PCG 와 stream_power 의 오목도 θ 는
/// 입력(각각 0.45, m/n = 0.5)이 그대로 나오는 값이라 지구다움의 근거가 아닙니다(설계도 7장의 '강제' 지표).
/// </remarks>
public static class CompareMetrics
{
    public const double ChannelAreaM2 = 5.0e5; // 하천으로 보는 상류 면적 문턱 0.5 km² (우리가 정한 값)
    public const double DepressionTolM = 0.01; // 웅덩이로 보는 채움 깊이
    public const double FillEpsM = 1.0e-3;
    public const double MinSlope = 1.0e-4; // 경사-면적 맞추기에 쓰는 최소 경사

    public static readonly CompareMetric[] All =
    [
        new("relief_m", "기복", "m", "모양", "가장 높은 곳 − 가장 낮은 곳", Relief),
        new("hypsometric_integral", "고도 적분", "", "모양", "(평균 − 최저) / 기복. 젊은 산지는 크고 오래 깎인 땅은 작음 (보통 0.3~0.6)", Hypsometric),
        new("slope_mean_deg", "평균 경사", "°", "모양", "칸마다 경사(중앙 차분)의 평균", a => a.Slope.Average(s => Math.Atan(s) * 180.0 / Math.PI)),
        new("slope_p90_deg", "경사 90%", "°", "모양", "경사 분포의 90번째 백분위", a => Math.Atan(NpStats.Percentile(a.Slope, 90.0)) * 180.0 / Math.PI),
        new("spectral_beta", "스펙트럼 기울기 β", "", "모양", "고도 파워 스펙트럼 P(k) ∝ k^−β 의 β (파장 L/4 ~ 4칸). 단일 프랙탈은 모든 크기에서 같음", SpectralBeta),
        new("pit_density_per_km2", "웅덩이 점", "개/km²", "물길", "가장자리가 아니고 여덟 이웃보다 모두 낮은 칸의 밀도. 물이 갇히는 곳 (실제 지형은 거의 0)", Pits),
        new("depression_area_pct", "웅덩이 넓이", "%", "물길", "웅덩이를 넘침 높이까지 채웠을 때 1 cm 넘게 잠기는 칸의 비율 (바다로 못 나가는 땅)", DepressionArea),
        new("depression_depth_m", "웅덩이 평균 깊이", "m", "물길", "채운 높이 − 원래 높이의 영역 평균 (갇힌 물의 부피 / 넓이)", a => a.Zf.Zip(a.Z, (f, z) => f - z).Average()),
        new("drainage_density_km", "하천 밀도", "km/km²", "물길", "상류 면적 0.5 km² 이상인 칸의 물길 길이 합 / 넓이", DrainageDensity),
        new("concavity_theta", "오목도 θ", "", "물길", "하천 경사-면적 관계 S ∝ A^−θ 의 θ (지구 보통 0.3~0.7). B-PCG·stream_power 는 입력이 나옴", a => SlopeArea(a).Theta),
        new("slope_area_r2", "경사-면적 R²", "", "물길", "위 맞추기의 설명력. 강이 하류로 갈수록 완만해지는 규칙이 있으면 높음", a => SlopeArea(a).R2),
        new("hack_exponent", "Hack 지수", "", "물길", "본류 L ∝ A^h 의 h (지구 약 0.5~0.6). 무작위 물길망에서도 비슷하게 나올 수 있음", a => Drainage.HackFit(a.Graph, a.Rcv, a.Order, a.AreaUp).Exponent),
    ];

    public static List<object?> DescribeAll() => [.. All.Select(m => (object?)m.Describe())];

    /// <summary>고도 (n·n,) 에서 지표 계산 재료를 만듭니다.</summary>
    public static Analysis Analyze(double[] z, int n, double dx)
    {
        if (z.Length != n * n)
        {
            throw new ArgumentException($"고도는 (n·n,) 이어야 합니다: {z.Length} ≠ {n}²");
        }
        if (z.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("고도에 NaN 이나 inf 가 있습니다");
        }
        CellGraph g = Graph.FlatGraph(n, n, dx);
        bool[] outlet = g.BoundaryMask();
        double[] zf = Depressions.FillDepressions(z, g.Nbr, outlet);
        double[] zt = Depressions.FillEpsilon(z, g.Nbr, outlet, FillEpsM);
        (long[] rcv, _, _) = Routing.D8Receivers(zt, g.Nbr, g.Dist, outlet);
        long[] order = Routing.TopoOrder(rcv);
        double[] areaUp = AccumulateModule.Accumulate(rcv, order, g.Area);
        double[] distR = Drainage.ReceiverDistance(g, rcv);
        bool[] channel = new bool[z.Length];
        for (int c = 0; c < z.Length; c++)
        {
            channel[c] = areaUp[c] >= ChannelAreaM2 && !outlet[c];
        }
        return new Analysis
        {
            Z = z,
            N = n,
            Dx = dx,
            Graph = g,
            Zf = zf,
            Zt = zt,
            Rcv = rcv,
            Order = order,
            AreaUp = areaUp,
            DistR = distR,
            Slope = Gradient(z, n, dx),
            Channel = channel,
        };
    }

    /// <summary>등록된 지표를 모두 잽니다. 잴 수 없으면 null.</summary>
    public static OrderedDictionary<string, object?> Compute(double[] z, int n, double dx)
    {
        Analysis a = Analyze(z, n, dx);
        var o = new OrderedDictionary<string, object?>();
        foreach (CompareMetric m in All)
        {
            double v;
            try
            {
                v = m.Fn(a);
            }
            catch (ArgumentException)
            {
                v = double.NaN;
            }
            o[m.Name] = double.IsFinite(v) ? v : null;
        }
        return o;
    }

    /// <summary>numpy.gradient(z, dx) 의 경사 크기: 안쪽은 중앙 차분, 가장자리는 한쪽 차분.</summary>
    public static double[] Gradient(double[] z, int n, double dx)
    {
        double[] s = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double gx = i == 0 ? (z[(j * n) + 1] - z[j * n]) / dx
                    : i == n - 1 ? (z[(j * n) + i] - z[(j * n) + i - 1]) / dx
                    : (z[(j * n) + i + 1] - z[(j * n) + i - 1]) / (2 * dx);
                double gy = j == 0 ? (z[n + i] - z[i]) / dx
                    : j == n - 1 ? (z[(j * n) + i] - z[((j - 1) * n) + i]) / dx
                    : (z[((j + 1) * n) + i] - z[((j - 1) * n) + i]) / (2 * dx);
                s[(j * n) + i] = Math.Sqrt((gx * gx) + (gy * gy));
            }
        }
        return s;
    }

    private static double Relief(Analysis a)
    {
        (double lo, double hi) = CompareGrid.MinMax(a.Z);
        return hi - lo;
    }

    private static double Hypsometric(Analysis a)
    {
        (double lo, double hi) = CompareGrid.MinMax(a.Z);
        return hi > lo ? (a.Z.Average() - lo) / (hi - lo) : double.NaN;
    }

    private static double SpectralBeta(Analysis a)
    {
        int n = a.N;
        // 평면을 빼고(최소제곱) 한 창(Hann)을 곱한 뒤 2D FFT 의 반지름별 평균 세기
        double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = a.Z[(j * n) + i];
                sx += i;
                sy += j;
                sz += v;
                sxx += (double)i * i;
                syy += (double)j * j;
                sxy += (double)i * j;
                sxz += i * v;
                syz += j * v;
            }
        }
        double cnt = (double)n * n;
        double mx = sx / cnt, my = sy / cnt, mz = sz / cnt;
        double cxx = (sxx / cnt) - (mx * mx), cyy = (syy / cnt) - (my * my), cxy = (sxy / cnt) - (mx * my);
        double cxz = (sxz / cnt) - (mx * mz), cyz = (syz / cnt) - (my * mz);
        double det = (cxx * cyy) - (cxy * cxy);
        double bx = det != 0 ? ((cxz * cyy) - (cyz * cxy)) / det : 0.0;
        double by = det != 0 ? ((cyz * cxx) - (cxz * cxy)) / det : 0.0;
        double[] w = new double[n];
        for (int k = 0; k < n; k++)
        {
            w[k] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * k / (n - 1)));
        }
        double[] d = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double plane = mz + (bx * (i - mx)) + (by * (j - my));
                d[(j * n) + i] = (a.Z[(j * n) + i] - plane) * w[j] * w[i];
            }
        }
        // rfft 는 kx ≥ 0 반쪽만 줍니다. 전체 평면 평균(numpy fft2)과 같도록 짝(±kx)이 있는 열은 두 번 셉니다.
        Complex[] spec = Fft.Rfft2(d, n, n);
        int nh = (n / 2) + 1;
        double[] edges = new double[16];
        for (int e = 0; e < 16; e++)
        {
            edges[e] = 4.0 * Math.Pow(n / 16.0, e / 15.0); // geomspace(4, n/4, 16)
        }
        double[] sum = new double[15];
        int[] num = new int[15];
        for (int j = 0; j < n; j++)
        {
            int ky = j <= n / 2 ? j : j - n;
            for (int i = 0; i < nh; i++)
            {
                int twin = i == 0 || (n % 2 == 0 && i == n / 2) ? 1 : 2;
                double k = Math.Sqrt(((double)ky * ky) + ((double)i * i));
                for (int b = 0; b < 15; b++)
                {
                    if (k >= edges[b] && k < edges[b + 1])
                    {
                        Complex c = spec[(j * nh) + i];
                        sum[b] += twin * ((c.Real * c.Real) + (c.Imaginary * c.Imaginary));
                        num[b] += twin;
                        break;
                    }
                }
            }
        }
        var xs = new List<double>();
        var ys = new List<double>();
        for (int b = 0; b < 15; b++)
        {
            if (num[b] > 0 && sum[b] > 0)
            {
                xs.Add(Math.Log10(Math.Sqrt(edges[b] * edges[b + 1])));
                ys.Add(Math.Log10(sum[b] / num[b]));
            }
        }
        return xs.Count < 3 ? double.NaN : -SciPy.Polyfit1([.. xs], [.. ys]).Slope;
    }

    private static double Pits(Analysis a)
    {
        int n = a.N;
        int count = 0;
        for (int j = 1; j < n - 1; j++)
        {
            for (int i = 1; i < n - 1; i++)
            {
                double c = a.Z[(j * n) + i];
                bool lower = true;
                for (int dj = -1; dj <= 1 && lower; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        if ((dj != 0 || di != 0) && !(c < a.Z[((j + dj) * n) + i + di]))
                        {
                            lower = false;
                            break;
                        }
                    }
                }
                if (lower)
                {
                    count++;
                }
            }
        }
        double areaKm2 = (n - 2) * (double)(n - 2) * a.Dx * a.Dx / 1e6;
        return count / areaKm2;
    }

    private static double DepressionArea(Analysis a)
    {
        int c = 0;
        for (int k = 0; k < a.Z.Length; k++)
        {
            if (a.Zf[k] - a.Z[k] > DepressionTolM)
            {
                c++;
            }
        }
        return 100.0 * c / a.Z.Length;
    }

    private static double DrainageDensity(Analysis a)
    {
        double lengthKm = 0.0;
        for (int c = 0; c < a.Z.Length; c++)
        {
            if (a.Channel[c])
            {
                lengthKm += a.DistR[c] / 1e3;
            }
        }
        return lengthKm / (a.N * (double)a.N * a.Dx * a.Dx / 1e6);
    }

    /// <summary>하천 칸의 log S = log k_s − θ log A 맞추기 → (θ, R²). 웅덩이 칸은 뺍니다.</summary>
    public static (double Theta, double R2) SlopeArea(Analysis a)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (int c = 0; c < a.Z.Length; c++)
        {
            if (!a.Channel[c] || a.Zf[c] - a.Z[c] > DepressionTolM || !(a.DistR[c] > 0))
            {
                continue;
            }
            double s = (a.Zt[c] - a.Zt[a.Rcv[c]]) / a.DistR[c];
            if (s > MinSlope)
            {
                xs.Add(Math.Log10(a.AreaUp[c]));
                ys.Add(Math.Log10(s));
            }
        }
        if (xs.Count < 20 || xs.Max() - xs.Min() <= 0)
        {
            return (double.NaN, double.NaN);
        }
        (double slope, double icept) = SciPy.Polyfit1([.. xs], [.. ys]);
        double mean = ys.Average();
        double ss = ys.Sum(y => (y - mean) * (y - mean));
        double res = xs.Zip(ys, (x, y) => (y - (icept + (slope * x))) * (y - (icept + (slope * x)))).Sum();
        return (-slope, ss > 0 ? 1.0 - (res / ss) : double.NaN);
    }
}
