using System;
using System.Collections.Generic;
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

    /// <summary>
    /// str.format(**values) 의 작은 부분: "{이름}" 과 "{이름:.Nf}", "{이름:.Ng}", "{이름:g}". 값이 글자면 그대로,
    /// 실수는 사양이 없으면 repr, 정수는 십진수입니다. "{{" 와 "}}" 는 중괄호 하나.
    /// </summary>
    public static string Format(string template, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char ch = template[i];
            if (ch == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                sb.Append('{');
                i += 2;
                continue;
            }
            if (ch == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i += 2;
                continue;
            }
            if (ch != '{')
            {
                sb.Append(ch);
                i++;
                continue;
            }
            int end = template.IndexOf('}', i);
            string body = template[(i + 1)..end];
            int colon = body.IndexOf(':');
            string name = colon < 0 ? body : body[..colon];
            string spec = colon < 0 ? "" : body[(colon + 1)..];
            sb.Append(FormatValue(values[name], spec));
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>값 하나를 Python 형식 사양(".Nf", ".Ng", "g", "")으로.</summary>
    public static string FormatValue(object? v, string spec)
    {
        if (v is string s)
        {
            return s;
        }
        if (v is null)
        {
            return "None";
        }
        if (v is bool b)
        {
            return b ? "True" : "False";
        }
        if (v is long or int)
        {
            long l = Convert.ToInt64(v, Inv);
            return spec == "" ? l.ToString(Inv) : FormatValue((double)l, spec);
        }
        double d = Convert.ToDouble(v, Inv);
        if (spec == "")
        {
            return Repr(d);
        }
        if (spec == "g")
        {
            return General(d);
        }
        if (spec.StartsWith('.') && spec.EndsWith('f'))
        {
            return Fixed(d, int.Parse(spec[1..^1], Inv));
        }
        if (spec.StartsWith('.') && spec.EndsWith('g'))
        {
            return General(d, int.Parse(spec[1..^1], Inv));
        }
        throw new ArgumentException($"지원하지 않는 형식 사양입니다: '{spec}'");
    }
}
