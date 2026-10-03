using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Bpcg.Numerics;

/// <summary>
/// numpy.fft 대체 (fft, rfft2, irfft2, fftfreq, rfftfreq). 길이가 2 의 거듭제곱이 아니면 Bluestein 방법을 씁니다.
/// </summary>
/// <remarks>TODO(port): numpy(pocketfft)와 덧셈 순서가 달라 마지막 비트가 다를 수 있습니다.</remarks>
public static class Fft
{
    /// <summary>np.fft.fftfreq(n, d).</summary>
    public static double[] FftFreq(int n, double d)
    {
        double[] o = new double[n];
        double val = 1.0 / (n * d);
        int nPos = ((n - 1) / 2) + 1;
        for (int i = 0; i < nPos; i++)
        {
            o[i] = i * val;
        }
        for (int i = nPos; i < n; i++)
        {
            o[i] = (-(n / 2) + (i - nPos)) * val;
        }
        return o;
    }

    /// <summary>np.fft.rfftfreq(n, d).</summary>
    public static double[] RfftFreq(int n, double d)
    {
        double val = 1.0 / (n * d);
        double[] o = new double[(n / 2) + 1];
        for (int i = 0; i < o.Length; i++)
        {
            o[i] = i * val;
        }
        return o;
    }

    private static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    private static void Radix2(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k];
                    Complex v = a[i + k + (len / 2)] * w;
                    a[i + k] = u + v;
                    a[i + k + (len / 2)] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    /// <summary>복소 DFT (정규화 없음; inverse 면 부호만 바꿈).</summary>
    public static Complex[] Transform(Complex[] x, bool inverse)
    {
        int n = x.Length;
        if (n <= 1)
        {
            return (Complex[])x.Clone();
        }
        if (IsPow2(n))
        {
            var a = (Complex[])x.Clone();
            Radix2(a, inverse);
            return a;
        }
        // Bluestein: x_k·w_k 를 w*_k 와 합성곱
        int m = 1;
        while (m < (2 * n) - 1)
        {
            m <<= 1;
        }
        double sgn = inverse ? 1 : -1;
        var w = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            long kk = (long)k * k % (2L * n);
            double ang = sgn * Math.PI * kk / n;
            w[k] = new Complex(Math.Cos(ang), Math.Sin(ang));
        }
        var a2 = new Complex[m];
        var b2 = new Complex[m];
        for (int k = 0; k < n; k++)
        {
            a2[k] = x[k] * w[k];
        }
        b2[0] = Complex.Conjugate(w[0]);
        for (int k = 1; k < n; k++)
        {
            b2[k] = b2[m - k] = Complex.Conjugate(w[k]);
        }
        Radix2(a2, false);
        Radix2(b2, false);
        for (int i = 0; i < m; i++)
        {
            a2[i] *= b2[i];
        }
        Radix2(a2, true);
        var o = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            o[k] = a2[k] / m * w[k];
        }
        return o;
    }

    /// <summary>np.fft.rfft2(x) (ny, nx) 행 우선 → (ny, nx/2+1) 복소.</summary>
    public static Complex[] Rfft2(double[] x, int ny, int nx)
    {
        int nh = (nx / 2) + 1;
        var half = new Complex[ny * nh];
        Parallel.For(0, ny, j =>
        {
            var row = new Complex[nx];
            for (int i = 0; i < nx; i++)
            {
                row[i] = x[(j * nx) + i];
            }
            Complex[] f = Transform(row, false);
            Array.Copy(f, 0, half, j * nh, nh);
        });
        Parallel.For(0, nh, i =>
        {
            var col = new Complex[ny];
            for (int j = 0; j < ny; j++)
            {
                col[j] = half[(j * nh) + i];
            }
            Complex[] f = Transform(col, false);
            for (int j = 0; j < ny; j++)
            {
                half[(j * nh) + i] = f[j];
            }
        });
        return half;
    }

    /// <summary>np.fft.irfft2(X, s=(ny, nx)): X 는 (ny, nx/2+1) 복소 → (ny, nx) 실수.</summary>
    public static double[] Irfft2(Complex[] spec, int ny, int nx)
    {
        int nh = (nx / 2) + 1;
        var half = (Complex[])spec.Clone();
        Parallel.For(0, nh, i =>
        {
            var col = new Complex[ny];
            for (int j = 0; j < ny; j++)
            {
                col[j] = half[(j * nh) + i];
            }
            Complex[] f = Transform(col, true);
            for (int j = 0; j < ny; j++)
            {
                half[(j * nh) + i] = f[j] / ny;
            }
        });
        double[] o = new double[ny * nx];
        Parallel.For(0, ny, j =>
        {
            var full = new Complex[nx];
            for (int k = 0; k < nh && k < nx; k++)
            {
                full[k] = half[(j * nh) + k];
            }
            // 에르미트 대칭으로 나머지 절반 (0번과 짝수 길이의 나이퀴스트 칸은 실수부만)
            full[0] = new Complex(full[0].Real, 0.0);
            if (nx % 2 == 0)
            {
                full[nx / 2] = new Complex(full[nx / 2].Real, 0.0);
            }
            for (int k = nh; k < nx; k++)
            {
                full[k] = Complex.Conjugate(full[nx - k]);
            }
            Complex[] f = Transform(full, true);
            for (int i = 0; i < nx; i++)
            {
                o[(j * nx) + i] = f[i].Real / nx;
            }
        });
        return o;
    }
}
