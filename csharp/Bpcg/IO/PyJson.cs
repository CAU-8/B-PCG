using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Bpcg.IO;

/// <summary>
/// Python <c>json.dumps</c> 와 같은 글자를 내는 JSON 쓰기 (docs/csharp_port.md 4장 'JSON 쓰기').
/// </summary>
/// <remarks>
/// 값 모델: null, bool, string, 정수(long·int·…·ulong), double(float 은 double 로 넓혀 씀),
/// <c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>(삽입 순서 dict), 그 밖의 IEnumerable(list).
/// 실수는 Python <c>float.__repr__</c> 규칙(최단 왕복 자릿수, −4 &lt; decpt ≤ 16 이면 고정 소수)으로 씁니다.
/// System.Text.Json 의 writer 는 지수 꼴·정수 값 실수의 '.0'·비 ASCII 이스케이프가 달라 쓰지 않습니다.
/// </remarks>
public static class PyJson
{
    /// <summary>json.dumps 의 인자 묶음.</summary>
    public sealed record Options(
        int? Indent = null,
        bool SortKeys = false,
        bool EnsureAscii = true,
        bool AllowNan = true);

    /// <summary>
    /// json.dumps(value, indent=…, sort_keys=…, ensure_ascii=…, allow_nan=…).
    /// 구분자는 Python 기본값: indent 가 없으면 (", ", ": "), 있으면 (",", ": ").
    /// </summary>
    public static string Dumps(object? value, Options options)
    {
        var sb = new StringBuilder();
        string itemSep = options.Indent is null ? ", " : ",";
        Write(sb, value, options, itemSep, 0);
        return sb.ToString();
    }

    /// <summary>
    /// json.loads: 객체는 순서 있는 dict, 배열은 List, 소수점·지수가 없는 수는 long(넘치면 double), 나머지 수는 double.
    /// </summary>
    public static object? Loads(string text)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = false });
        return FromElement(doc.RootElement);
    }

    private static object? FromElement(System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var d = new OrderedDictionary<string, object?>();
                foreach (System.Text.Json.JsonProperty p in e.EnumerateObject())
                {
                    d[p.Name] = FromElement(p.Value);
                }
                return d;
            case System.Text.Json.JsonValueKind.Array:
                var l = new List<object?>();
                foreach (System.Text.Json.JsonElement x in e.EnumerateArray())
                {
                    l.Add(FromElement(x));
                }
                return l;
            case System.Text.Json.JsonValueKind.String:
                return e.GetString();
            case System.Text.Json.JsonValueKind.Number:
                string raw = e.GetRawText();
                bool isInt = raw.IndexOfAny(['.', 'e', 'E']) < 0;
                if (isInt && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long lv))
                {
                    return lv;
                }
                // TODO(port): Python 은 아주 큰 정수를 임의 정밀도 int 로 읽습니다.
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            case System.Text.Json.JsonValueKind.True:
                return true;
            case System.Text.Json.JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>config digest 용: json.dumps(v, sort_keys=True, ensure_ascii=False).</summary>
    public static string DumpsSorted(object? value) =>
        Dumps(value, new Options(SortKeys: true, EnsureAscii: false));

    /// <summary>묶음 manifest 용: json.dumps(v, ensure_ascii=False, indent=2, allow_nan=False).</summary>
    public static string DumpsManifest(object? value) =>
        Dumps(value, new Options(Indent: 2, EnsureAscii: false, AllowNan: false));

    private static void Write(StringBuilder sb, object? v, Options o, string itemSep, int level)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(sb, s, o.EnsureAscii);
                return;
            case double d:
                sb.Append(FloatJson(d, o.AllowNan));
                return;
            case float f:
                sb.Append(FloatJson(f, o.AllowNan));
                return;
            case long or int or short or sbyte or byte or ushort or uint or ulong:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            case System.Numerics.BigInteger bi:
                sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                return;
            case IEnumerable<KeyValuePair<string, object?>> dict:
                WriteDict(sb, dict, o, itemSep, level);
                return;
            case IEnumerable list:
                WriteList(sb, list.Cast<object?>(), o, itemSep, level);
                return;
            default:
                throw new ArgumentException($"JSON 으로 쓸 수 없는 값입니다: {v.GetType()}");
        }
    }

    private static void WriteDict(
        StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> dict, Options o, string itemSep, int level)
    {
        List<KeyValuePair<string, object?>> items = dict.ToList();
        if (items.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        if (o.SortKeys)
        {
            items = items.OrderBy(kv => kv.Key, CodePointComparer.Instance).ToList();
        }
        sb.Append('{');
        string? newline = null;
        if (o.Indent is int ind)
        {
            newline = "\n" + new string(' ', ind * (level + 1));
            sb.Append(newline);
        }
        bool first = true;
        foreach (KeyValuePair<string, object?> kv in items)
        {
            if (!first)
            {
                sb.Append(itemSep);
                if (newline is not null)
                {
                    sb.Append(newline);
                }
            }
            first = false;
            WriteString(sb, kv.Key, o.EnsureAscii);
            sb.Append(": ");
            Write(sb, kv.Value, o, itemSep, level + 1);
        }
        if (o.Indent is int ind2)
        {
            sb.Append('\n').Append(' ', ind2 * level);
        }
        sb.Append('}');
    }

    private static void WriteList(StringBuilder sb, IEnumerable<object?> list, Options o, string itemSep, int level)
    {
        List<object?> items = list.ToList();
        if (items.Count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[');
        string? newline = null;
        if (o.Indent is int ind)
        {
            newline = "\n" + new string(' ', ind * (level + 1));
            sb.Append(newline);
        }
        bool first = true;
        foreach (object? item in items)
        {
            if (!first)
            {
                sb.Append(itemSep);
                if (newline is not null)
                {
                    sb.Append(newline);
                }
            }
            first = false;
            Write(sb, item, o, itemSep, level + 1);
        }
        if (o.Indent is int ind2)
        {
            sb.Append('\n').Append(' ', ind2 * level);
        }
        sb.Append(']');
    }

    private static string FloatJson(double d, bool allowNan)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            if (!allowNan)
            {
                throw new ArgumentException($"JSON 에 쓸 수 없는 실수입니다(allow_nan=False): {Repr(d)}");
            }
            return double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity";
        }
        return Repr(d);
    }

    /// <summary>
    /// Python repr(float): 최단 왕복 자릿수, −4 &lt; decpt ≤ 16 이면 고정 소수(정수 값은 '.0'),
    /// 아니면 'd.ddde±XX'. nan·inf·-inf 는 Python 처럼 씁니다.
    /// </summary>
    public static string Repr(double v)
    {
        if (double.IsNaN(v))
        {
            return "nan";
        }
        if (double.IsInfinity(v))
        {
            return v > 0 ? "inf" : "-inf";
        }
        if (v == 0.0)
        {
            return BitConverter.DoubleToInt64Bits(v) < 0 ? "-0.0" : "0.0";
        }
        (bool neg, string digits, int decpt) = ShortestDigits(v);
        var sb = new StringBuilder();
        if (neg)
        {
            sb.Append('-');
        }
        int n = digits.Length;
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0)
            {
                sb.Append("0.").Append('0', -decpt).Append(digits);
            }
            else if (decpt >= n)
            {
                sb.Append(digits).Append('0', decpt - n).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, n - decpt);
            }
        }
        else
        {
            int exp = decpt - 1;
            sb.Append(digits[0]);
            if (n > 1)
            {
                sb.Append('.').Append(digits, 1, n - 1);
            }
            sb.Append('e').Append(exp < 0 ? '-' : '+');
            sb.Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 최단 왕복 자릿수와 소수점 자리: 값 = 0.d₁d₂…dₙ × 10^decpt. .NET "R" 서식(.NET Core 3.0 부터 최단 왕복)의
    /// 숫자열과 지수만 뽑아 씁니다.
    /// </summary>
    private static (bool Negative, string Digits, int Decpt) ShortestDigits(double v)
    {
        string r = v.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r[0] == '-';
        if (neg)
        {
            r = r[1..];
        }
        int e = r.IndexOfAny(['E', 'e']);
        string mant = e >= 0 ? r[..e] : r;
        int exp10 = e >= 0 ? int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        int dot = mant.IndexOf('.', StringComparison.Ordinal);
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string digits = intPart + frac;
        int decpt = intPart.Length + exp10;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
        }
        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        return (neg, digits, decpt);
    }

    private static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    continue;
                case '\\':
                    sb.Append("\\\\");
                    continue;
                case '\n':
                    sb.Append("\\n");
                    continue;
                case '\r':
                    sb.Append("\\r");
                    continue;
                case '\t':
                    sb.Append("\\t");
                    continue;
                case '\b':
                    sb.Append("\\b");
                    continue;
                case '\f':
                    sb.Append("\\f");
                    continue;
            }
            if (c < 0x20)
            {
                AppendU(sb, c);
                continue;
            }
            if (ensureAscii && c > 0x7E)
            {
                AppendU(sb, c); // BMP 밖 글자는 UTF-16 대리쌍 두 개로 나가 Python 과 같음
                continue;
            }
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }
                // Python 은 짝 없는 대리 문자를 UTF-8 로 쓰지 못해 실패합니다
                throw new ArgumentException("짝 없는 UTF-16 대리 문자는 JSON 으로 쓸 수 없습니다");
            }
            sb.Append(c);
        }
        sb.Append('"');
    }

    private static void AppendU(StringBuilder sb, char c)
    {
        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
    }

    /// <summary>Python 문자열 비교(코드 포인트 순). UTF-16 서수 비교와는 BMP 밖 글자에서 다릅니다.</summary>
    public sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            StringRuneEnumerator ex = x.EnumerateRunes();
            StringRuneEnumerator ey = y.EnumerateRunes();
            while (true)
            {
                bool hx = ex.MoveNext();
                bool hy = ey.MoveNext();
                if (!hx || !hy)
                {
                    return hx == hy ? 0 : hx ? 1 : -1;
                }
                int d = ex.Current.Value.CompareTo(ey.Current.Value);
                if (d != 0)
                {
                    return d;
                }
            }
        }
    }
}
