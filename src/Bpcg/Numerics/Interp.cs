using System;
using Bpcg.Core;

namespace Bpcg.Numerics;

/// <summary>scipy.interpolate 대체 (RegularGridInterpolator linear, brentq).</summary>
public static class Interp
{
    // scipy find_interval_ascending: grid[i] ≤ x < grid[i+1] 인 i, 범위 밖은 0 또는 n−2 로 붙입니다.
    private static int FindInterval(double[] grid, double x)
    {
        int n = grid.Length;
        int lo = 0;
        int hi = n;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (grid[mid] <= x)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        int i = lo - 1;
        if (i < 0)
        {
            i = 0;
        }
        if (i > n - 2)
        {
            i = n - 2;
        }
        return i;
    }

    /// <summary>
    /// RegularGridInterpolator((ys, xs), values, method="linear", bounds_error=False, fill_value=None) 의 값.
    /// values 는 (ny, nx) 행 우선, ys·xs 는 오름차순. 범위 밖은 선형 외삽합니다.
    /// </summary>
    /// <remarks>
    /// scipy 의 Cython evaluate_linear_2d 는 macOS arm64 clang 이 곱셈·덧셈을 FMA 로 합쳐 컴파일되어 있습니다.
    /// 그 식(마지막 세 덧셈이 FMA)을 그대로 따릅니다(확인: 무작위 5000 점 비트 일치).
    /// TODO(port): 다른 OS 의 scipy 빌드는 FMA 를 쓰지 않을 수 있습니다.
    /// </remarks>
    public static double[] RegularGridLinear2D(double[] ys, double[] xs, double[] values, double[] qy, double[] qx)
    {
        int nx = xs.Length;
        if (ys.Length < 2 || nx < 2 || values.Length != ys.Length * nx || qy.Length != qx.Length)
        {
            throw new ArgumentException("격자와 값의 모양이 맞지 않습니다");
        }
        double[] output = new double[qy.Length];
        Parallelism.For(0, qy.Length, k =>
        {
            double y = qy[k];
            double x = qx[k];
            if (double.IsNaN(y) || double.IsNaN(x))
            {
                output[k] = double.NaN;
                return;
            }
            int i = FindInterval(ys, y);
            int j = FindInterval(xs, x);
            double y0 = (y - ys[i]) / (ys[i + 1] - ys[i]);
            double y1 = (x - xs[j]) / (xs[j + 1] - xs[j]);
            double a = values[(i * nx) + j];
            double b = values[(i * nx) + j + 1];
            double c = values[((i + 1) * nx) + j];
            double d = values[((i + 1) * nx) + j + 1];
            double t = a * (1 - y0) * (1 - y1);
            t = Math.FusedMultiplyAdd(b * (1 - y0), y1, t);
            t = Math.FusedMultiplyAdd(c * y0, 1 - y1, t);
            output[k] = Math.FusedMultiplyAdd(d * y0, y1, t);
        });
        return output;
    }

    /// <summary>
    /// scipy.optimize.brentq (f(a)·f(b) &lt; 0 인 구간의 근). xtol·rtol·maxiter 는 scipy 와 같은 뜻입니다.
    /// </summary>
    /// <remarks>scipy 의 C 구현(brentq.c)을 옮겼습니다. TODO(port): 반복 순서가 scipy 와 같은지 대조하지 않았습니다.</remarks>
    public static double Brentq(Func<double, double> f, double xa, double xb, double xtol = 2e-12,
        double rtol = 8.881784197001252e-16, int maxiter = 100)
    {
        double xpre = xa;
        double xcur = xb;
        double xblk = 0.0;
        double fblk = 0.0;
        double spre = 0.0;
        double scur = 0.0;
        double fpre = f(xpre);
        double fcur = f(xcur);
        if (fpre == 0)
        {
            return xpre;
        }
        if (fcur == 0)
        {
            return xcur;
        }
        if (Math.Sign(fpre) == Math.Sign(fcur))
        {
            throw new ArgumentException("f(a) 와 f(b) 의 부호가 달라야 합니다");
        }
        for (int i = 0; i < maxiter; i++)
        {
            if (fpre != 0 && fcur != 0 && Math.Sign(fpre) != Math.Sign(fcur))
            {
                xblk = xpre;
                fblk = fpre;
                spre = scur = xcur - xpre;
            }
            if (Math.Abs(fblk) < Math.Abs(fcur))
            {
                xpre = xcur;
                xcur = xblk;
                xblk = xpre;
                fpre = fcur;
                fcur = fblk;
                fblk = fpre;
            }
            double delta = (xtol + (rtol * Math.Abs(xcur))) / 2;
            double sbis = (xblk - xcur) / 2;
            if (fcur == 0 || Math.Abs(sbis) < delta)
            {
                return xcur;
            }
            if (Math.Abs(spre) > delta && Math.Abs(fcur) < Math.Abs(fpre))
            {
                double stry;
                if (xpre == xblk)
                {
                    // 할선법
                    stry = -fcur * (xcur - xpre) / (fcur - fpre);
                }
                else
                {
                    // 역 2차 보간
                    double dpre = (fpre - fcur) / (xpre - xcur);
                    double dblk = (fblk - fcur) / (xblk - xcur);
                    stry = -fcur * ((fblk * dblk) - (fpre * dpre)) / (dblk * dpre * (fblk - fpre));
                }
                if (2 * Math.Abs(stry) < Math.Min(Math.Abs(spre), (3 * Math.Abs(sbis)) - delta))
                {
                    spre = scur;
                    scur = stry;
                }
                else
                {
                    spre = sbis;
                    scur = sbis;
                }
            }
            else
            {
                spre = sbis;
                scur = sbis;
            }
            xpre = xcur;
            fpre = fcur;
            if (Math.Abs(scur) > delta)
            {
                xcur += scur;
            }
            else
            {
                xcur += sbis > 0 ? delta : -delta;
            }
            fcur = f(xcur);
        }
        // TODO(port): scipy 는 수렴하지 않으면 RuntimeError 를 냅니다.
        throw new InvalidOperationException("brentq 가 수렴하지 않았습니다");
    }
}
