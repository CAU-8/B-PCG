using System;
using System.Globalization;

namespace Bpcg.IO;

/// <summary>Python 숫자 서식 (f"{x:.3g}", f"{x:.4f}", f"{x:g}", str(float)).</summary>
public static class PyFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string? Special(double x) =>
        double.IsNaN(x) ? "nan" : double.IsPositiveInfinity(x) ? "inf" : double.IsNegativeInfinity(x) ? "-inf" : null;

    /// <summary>f"{x:.{p}f}". TODO(port): .NET 의 정확히 반인 경우 반올림 방향이 Python(짝수 쪽)과 다를 수 있습니다.</summary>
    public static string Fixed(double x, int p) => Special(x) ?? x.ToString("F" + p, Inv);

    /// <summary>f"{x:.{p}g}" (p 기본 6). 지수가 −4 미만이거나 p 이상이면 과학 표기, 끝의 0 은 지웁니다.</summary>
    public static string General(double x, int p = 6)
    {
        string? s = Special(x);
        if (s is not null)
        {
            return s;
        }
        if (p == 0)
        {
            p = 1;
        }
        if (x == 0.0)
        {
            return double.IsNegative(x) ? "-0" : "0";
        }
        string e = x.ToString("E" + (p - 1), Inv);
        int ePos = e.IndexOf('E');
        int exp = int.Parse(e[(ePos + 1)..], Inv);
        if (exp >= -4 && exp < p)
        {
            string f = x.ToString("F" + Math.Max(p - 1 - exp, 0), Inv);
            if (f.Contains('.'))
            {
                f = f.TrimEnd('0').TrimEnd('.');
            }
            return f;
        }
        string mant = e[..ePos];
        if (mant.Contains('.'))
        {
            mant = mant.TrimEnd('0').TrimEnd('.');
        }
        string sign = exp < 0 ? "-" : "+";
        return $"{mant}e{sign}{Math.Abs(exp):00}";
    }

    /// <summary>str(float) (Python repr).</summary>
    public static string Repr(double x) => PyJson.Repr(x);
}
