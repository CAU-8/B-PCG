using System;
using System.Collections.Generic;

namespace Bpcg.Numerics;

/// <summary>scipy.ndimage·scipy.signal 과 numpy 히스토그램·polyfit 대체.</summary>
public static class SciPy
{
    /// <summary>scipy _gaussian_kernel1d(sigma, 0, radius): exp(−0.5/σ²·x²) 를 합으로 나눈 값.</summary>
    public static double[] GaussianKernel1D(double sigma, int radius)
    {
        double sigma2 = sigma * sigma;
        double k = -0.5 / sigma2;
        double[] phi = new double[(2 * radius) + 1];
        for (int i = 0; i < phi.Length; i++)
        {
            long x = i - radius;
            phi[i] = Math.Exp(k * (x * x));
        }
        double s = NpReduce.Sum(phi);
        for (int i = 0; i < phi.Length; i++)
        {
            phi[i] /= s;
        }
        return phi;
    }

    /// <summary>
    /// scipy.ndimage.gaussian_filter1d(x, sigma, mode="constant", cval=0, truncate=4).
    /// </summary>
    /// <remarks>
    /// scipy C 코드(NI_Correlate1D 대칭 경로)를 macOS arm64 clang 이 FMA 로 줄인 식을 따릅니다.
    /// TODO(port): 반경 8 이하에서는 비트 일치를 확인했고, 반경 10·16 에서는 1 ulp 다른 칸이 있습니다(원인 미확인).
    /// </remarks>
    public static double[] GaussianFilter1DConstant(double[] x, double sigma, double cval = 0.0, double truncate = 4.0)
    {
        int radius = (int)((truncate * sigma) + 0.5);
        double[] w = GaussianKernel1D(sigma, radius);
        return Correlate1DSymmetric(x, w, radius, i => cval);
    }

    /// <summary>scipy.ndimage.gaussian_filter1d(x, sigma, mode="nearest").</summary>
    public static double[] GaussianFilter1DNearest(double[] x, double sigma, double truncate = 4.0)
    {
        int radius = (int)((truncate * sigma) + 0.5);
        double[] w = GaussianKernel1D(sigma, radius);
        int n = x.Length;
        return Correlate1DSymmetric(x, w, radius, i => x[i < 0 ? 0 : n - 1]);
    }

    private static double[] Correlate1DSymmetric(double[] x, double[] w, int radius, Func<int, double> outside)
    {
        int n = x.Length;
        double[] pad = new double[n + (2 * radius)];
        for (int i = 0; i < pad.Length; i++)
        {
            int j = i - radius;
            pad[i] = j >= 0 && j < n ? x[j] : outside(j);
        }
        double[] o = new double[n];
        for (int i = 0; i < n; i++)
        {
            int c = i + radius;
            double t = pad[c] * w[radius];
            for (int jj = -radius; jj < 0; jj++)
            {
                t = Math.FusedMultiplyAdd(pad[c + jj] + pad[c - jj], w[radius + jj], t);
            }
            o[i] = t;
        }
        return o;
    }

    /// <summary>
    /// scipy.ndimage.gaussian_filter(img, sigma, mode="nearest") (2차원, (ny, nx) 행 우선). 축마다 1차원 필터를 겁니다.
    /// </summary>
    public static double[] GaussianFilter2DNearest(double[] img, int ny, int nx, double sigma, double truncate = 4.0)
    {
        // scipy 는 축 0 부터 차례로 겁니다.
        double[] a = (double[])img.Clone();
        double[] col = new double[ny];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                col[j] = a[(j * nx) + i];
            }
            double[] f = GaussianFilter1DNearest(col, sigma, truncate);
            for (int j = 0; j < ny; j++)
            {
                a[(j * nx) + i] = f[j];
            }
        }
        double[] row = new double[nx];
        for (int j = 0; j < ny; j++)
        {
            Array.Copy(a, j * nx, row, 0, nx);
            double[] f = GaussianFilter1DNearest(row, sigma, truncate);
            Array.Copy(f, 0, a, j * nx, nx);
        }
        return a;
    }

    /// <summary>
    /// scipy.signal.find_peaks(x, prominence=pmin) 의 (봉우리 번호, 돌출도). wlen 없음.
    /// </summary>
    public static (int[] Peaks, double[] Prominences) FindPeaks(double[] x, double minProminence)
    {
        var peaks = new List<int>();
        int n = x.Length;
        int i = 1;
        int iMax = n - 1;
        while (i < iMax)
        {
            if (x[i - 1] < x[i])
            {
                int ahead = i + 1;
                while (ahead < iMax && x[ahead] == x[i])
                {
                    ahead++;
                }
                if (x[ahead] < x[i])
                {
                    peaks.Add((i + ahead - 1) / 2);
                    i = ahead;
                }
            }
            i++;
        }
        var keep = new List<int>();
        var prom = new List<double>();
        foreach (int p in peaks)
        {
            double leftMin = x[p];
            for (int k = p; k >= 0 && x[k] <= x[p]; k--)
            {
                if (x[k] < leftMin)
                {
                    leftMin = x[k];
                }
            }
            double rightMin = x[p];
            for (int k = p; k <= n - 1 && x[k] <= x[p]; k++)
            {
                if (x[k] < rightMin)
                {
                    rightMin = x[k];
                }
            }
            double pr = x[p] - Math.Max(leftMin, rightMin);
            if (minProminence <= pr)
            {
                keep.Add(p);
                prom.Add(pr);
            }
        }
        return (keep.ToArray(), prom.ToArray());
    }

    /// <summary>
    /// np.histogram(a, bins=edges, weights=w) (경계 배열 경로): 블록(65536)마다 정렬 → 가중치 누적합 → searchsorted.
    /// </summary>
    /// <remarks>TODO(port): numpy 는 블록 정렬에 불안정 정렬(argsort quicksort)을 써서 같은 값의 가중치 순서가 다를 수 있습니다.</remarks>
    public static double[] Histogram(double[] a, double[] w, double[] edges)
    {
        const int block = 65536;
        int nb = edges.Length;
        double[] cum = new double[nb];
        for (int start = 0; start < a.Length; start += block)
        {
            int len = Math.Min(block, a.Length - start);
            int[] idx = new int[len];
            for (int k = 0; k < len; k++)
            {
                idx[k] = start + k;
            }
            Array.Sort(idx, (p, q) =>
            {
                int c = a[p].CompareTo(a[q]);
                return c != 0 ? c : p.CompareTo(q);
            });
            double[] sa = new double[len];
            double[] cw = new double[len + 1];
            for (int k = 0; k < len; k++)
            {
                sa[k] = a[idx[k]];
                cw[k + 1] = cw[k] + w[idx[k]];
            }
            for (int e = 0; e < nb; e++)
            {
                int bi = e < nb - 1 ? SearchSorted(sa, edges[e], false) : SearchSorted(sa, edges[e], true);
                cum[e] += cw[bi];
            }
        }
        double[] h = new double[nb - 1];
        for (int e = 0; e < nb - 1; e++)
        {
            h[e] = cum[e + 1] - cum[e];
        }
        return h;
    }

    /// <summary>np.searchsorted(a, v, side): right=false 는 'left'.</summary>
    public static int SearchSorted(double[] a, double v, bool right)
    {
        int lo = 0;
        int hi = a.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            bool goRight = right ? a[mid] <= v : a[mid] < v;
            if (goRight)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// np.polyfit(x, y, 1) 의 (기울기, 절편).
    /// </summary>
    /// <remarks>
    /// numpy 는 열 크기를 맞춘 뒤 LAPACK dgelsd(SVD) 로 풉니다. 여기서는 같은 열 크기 맞춤 뒤 정규 방정식을 풉니다.
    /// TODO(port): LAPACK 과 비트까지 같지 않습니다.
    /// </remarks>
    public static (double Slope, double Intercept) Polyfit1(double[] x, double[] y)
    {
        int n = x.Length;
        double s0 = 0.0;
        for (int i = 0; i < n; i++)
        {
            s0 += x[i] * x[i];
        }
        double sc0 = Math.Sqrt(s0);
        double sc1 = Math.Sqrt(n);
        double a00 = 0.0;
        double a01 = 0.0;
        double a11 = 0.0;
        double b0 = 0.0;
        double b1 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double u = x[i] / sc0;
            double v = 1.0 / sc1;
            a00 += u * u;
            a01 += u * v;
            a11 += v * v;
            b0 += u * y[i];
            b1 += v * y[i];
        }
        double det = (a00 * a11) - (a01 * a01);
        double c0 = ((b0 * a11) - (b1 * a01)) / det;
        double c1 = ((a00 * b1) - (a01 * b0)) / det;
        return (c0 / sc0, c1 / sc1);
    }
}
