using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bpcg.IO;

namespace Bpcg.Core;

/// <summary>TOML 값으로 읽히지 않은 글자 (따옴표 없는 글자). 기존 값이 글자일 때만 받습니다 (BareText).</summary>
public sealed record BareText(string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 점(.)으로 읽는 설정 묶음 (src/bpcg/core/config.py Section). 값은 바꾸지 않습니다.
/// </summary>
/// <remarks>
/// 값 모델: 절은 <c>OrderedDictionary&lt;string, object?&gt;</c>(TOML 문서 순서), 배열은 <c>List&lt;object?&gt;</c>,
/// 정수 long, 실수 double, bool, string. 절을 읽으면 <see cref="Section"/> 으로 감싸 돌려줍니다.
/// </remarks>
public class Section
{
    public Section(OrderedDictionary<string, object?> data)
    {
        Data = data;
    }

    /// <summary>감싼 dict (복사하지 않음). 바꾸지 않습니다.</summary>
    protected OrderedDictionary<string, object?> Data { get; }

    /// <summary>__getattr__: 없으면 KeyNotFoundException, 절은 Section.</summary>
    public object? this[string key]
    {
        get
        {
            if (!Data.TryGetValue(key, out object? v))
            {
                throw new KeyNotFoundException($"설정에 '{key}' 가 없습니다");
            }
            return Wrap(v);
        }
    }

    /// <summary>__contains__.</summary>
    public bool Contains(string key) => Data.ContainsKey(key);

    /// <summary>get(key, default): 저장된 null 은 null 그대로, dict 기본값도 Section 으로 감쌉니다.</summary>
    public object? Get(string key, object? @default = null) =>
        Wrap(Data.TryGetValue(key, out object? v) ? v : @default);

    /// <summary>as_dict: 깊은 복사.</summary>
    public OrderedDictionary<string, object?> AsDict() => ConfigValue.DeepCopyDict(Data);

    /// <summary>절 읽기.</summary>
    public Section Sec(string key) => (Section)this[key]!;

    /// <summary>float(v): 정수·실수·bool 을 double 로.</summary>
    public double F(string key) => ConfigValue.ToDouble(this[key], key);

    /// <summary>int(v): 실수는 0 쪽으로 자르고 NaN·inf·int64 밖이면 예외.</summary>
    public long I(string key) => ConfigValue.ToLong(this[key], key);

    /// <summary>bool(v).</summary>
    public bool B(string key) => this[key] switch
    {
        bool b => b,
        long l => l != 0,
        double d => d != 0.0,
        string s => s.Length > 0,
        null => false,
        _ => true,
    };

    /// <summary>str(v) (글자 값).</summary>
    public string S(string key) => this[key] switch
    {
        string s => s,
        object o => ConfigValue.PyStr(o),
        null => "None",
    };

    /// <summary>리스트 값.</summary>
    public IReadOnlyList<object?> L(string key) => (List<object?>)this[key]!;

    /// <summary>실수 리스트 값 (원소마다 float(v)).</summary>
    public double[] FArray(string key) => L(key).Select(v => ConfigValue.ToDouble(v, key)).ToArray();

    /// <summary>예외 없는 float(v) 읽기 (globe._cfg_float: 없거나 바꿀 수 없으면 false).</summary>
    public bool TryF(string key, out double value)
    {
        value = 0.0;
        if (!Data.TryGetValue(key, out object? v))
        {
            return false;
        }
        try
        {
            value = ConfigValue.ToDouble(v, key);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    protected static object? Wrap(object? v) => v is OrderedDictionary<string, object?> d ? new Section(d) : v;

    public override string ToString() => $"Section({ConfigValue.PyRepr(Data)})";
}

/// <summary>
/// 행성 설정 + 프로필 (src/bpcg/core/config.py Config). cfg["a.b"] 로 점 경로를 읽고,
/// <see cref="Digest"/> 는 Python 과 같은 설정 해시를 냅니다.
/// </summary>
/// <remarks>
/// config.py 의 모듈 함수(load_config, parse_assignment, checked_overrides)는 클래스 이름 Config 와 겹치므로
/// 이 클래스의 static 으로 둡니다(docs/csharp_port.md 0장 쟁점 6).
/// </remarks>
public sealed class Config : Section
{
    public Config(OrderedDictionary<string, object?> data) : base(data)
    {
    }

    /// <summary>__getitem__: 점 경로 ("landscape.theta"). 빈 조각도 키로 봅니다.</summary>
    public new object? this[string dotted]
    {
        get
        {
            object? node = Data;
            foreach (string part in dotted.Split('.'))
            {
                if (node is not OrderedDictionary<string, object?> d)
                {
                    throw new InvalidOperationException($"'{dotted}' 의 중간 값이 절이 아닙니다");
                }
                if (!d.TryGetValue(part, out node))
                {
                    throw new KeyNotFoundException($"설정에 '{dotted}' 가 없습니다");
                }
            }
            return Wrap(node);
        }
    }

    /// <summary>
    /// with_overrides: 점 경로 값을 덮어쓴 새 Config. 없는 절은 끝에 새로 만들고, 있는 키는 자리를 지킵니다.
    /// 값은 int → long, 배열 → List, BareText → string 으로 바꿔 넣습니다.
    /// </summary>
    public Config WithOverrides(IEnumerable<KeyValuePair<string, object?>> overrides)
    {
        OrderedDictionary<string, object?> data = AsDict();
        foreach ((string dotted, object? value) in overrides)
        {
            OrderedDictionary<string, object?> node = data;
            string[] parts = dotted.Split('.');
            foreach (string part in parts[..^1])
            {
                if (!node.TryGetValue(part, out object? next))
                {
                    next = new OrderedDictionary<string, object?>();
                    node[part] = next;
                }
                node = next as OrderedDictionary<string, object?>
                    ?? throw new InvalidOperationException($"'{dotted}' 의 중간 값이 절이 아닙니다");
            }
            node[parts[^1]] = ConfigValue.Normalize(value);
        }
        return new Config(data);
    }

    /// <summary>설정 내용의 짧은 해시: json.dumps(sort_keys=True, ensure_ascii=False) 의 SHA-256 앞 12자 (소문자).</summary>
    public string Digest()
    {
        string json = PyJson.DumpsSorted(Data);
        byte[] hash = SHA256.HashData(new UTF8Encoding(false).GetBytes(json));
        return Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>manifest 의 config 에서 다시 만듭니다 (bundle.config_from_manifest).</summary>
    public static Config FromDict(OrderedDictionary<string, object?> data) => new(ConfigValue.DeepCopyDict(data));

    // ------------------------------------------------------------------ 읽기

    /// <summary>
    /// load_config: configs/planets/&lt;행성&gt;.toml + (있으면) configs/learned/&lt;행성 파일 이름&gt; +
    /// configs/profiles/&lt;프로필&gt;.toml. 이름 대신 '.toml' 로 끝나는 경로를 줘도 됩니다.
    /// </summary>
    public static Config LoadConfig(
        string planet = "earth", string profile = "laptop", IEnumerable<KeyValuePair<string, object?>>? overrides = null)
    {
        string planetPath = planet.EndsWith(".toml", StringComparison.Ordinal)
            ? planet
            : Path.Combine(Paths.Configs, "planets", planet + ".toml");
        string profilePath = profile.EndsWith(".toml", StringComparison.Ordinal)
            ? profile
            : Path.Combine(Paths.Configs, "profiles", profile + ".toml");
        OrderedDictionary<string, object?> data = ReadToml(planetPath);
        string learned = Path.Combine(Paths.Configs, "learned", Path.GetFileName(planetPath));
        if (File.Exists(learned))
        {
            data = Merge(data, ReadToml(learned));
        }
        var profileWrap = new OrderedDictionary<string, object?> { ["profile"] = ReadToml(profilePath) };
        data = Merge(data, profileWrap);
        ((OrderedDictionary<string, object?>)data["profile"]!)["name"] = Path.GetFileNameWithoutExtension(profilePath);
        var cfg = new Config(data);
        return overrides is null ? cfg : cfg.WithOverrides(overrides);
    }

    private static OrderedDictionary<string, object?> ReadToml(string path) => Toml.Load(File.ReadAllBytes(path));

    /// <summary>_merge: base 를 깊이 복사한 뒤 extra 를 겹칩니다(둘 다 절이면 재귀, 아니면 덮어씀).</summary>
    internal static OrderedDictionary<string, object?> Merge(
        OrderedDictionary<string, object?> @base, OrderedDictionary<string, object?> extra)
    {
        OrderedDictionary<string, object?> output = ConfigValue.DeepCopyDict(@base);
        foreach ((string k, object? v) in extra)
        {
            if (v is OrderedDictionary<string, object?> vd
                && output.TryGetValue(k, out object? existing) && existing is OrderedDictionary<string, object?> ed)
            {
                output[k] = Merge(ed, vd);
            }
            else
            {
                output[k] = ConfigValue.DeepCopy(v);
            }
        }
        return output;
    }

    // ------------------------------------------------------------------ 바깥에서 받은 덮어쓰기

    /// <summary>덮어쓸 수 없는 키 → 대신 고르는 곳 (FIXED_KEYS).</summary>
    public static IReadOnlyDictionary<string, string> FixedKeys { get; } = new OrderedDictionary<string, string>
    {
        ["planet.name"] = "행성은 --planet (스튜디오는 '행성 설정' 칸)으로 고르세요",
        ["profile.name"] = "프로필은 --profile (스튜디오는 '프로필' 칸)으로 고르세요",
    };

    /// <summary>
    /// parse_assignment: '키=값' 하나를 (점 경로, 값) 으로. 값은 TOML 값 문법으로 읽고, 읽히지 않으면 BareText.
    /// '=' 가 없거나 키·값이 비면 ArgumentException.
    /// </summary>
    public static (string Key, object? Value) ParseAssignment(string text)
    {
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        string key = ConfigValue.PyStrip(eq < 0 ? text : text[..eq]);
        string raw = eq < 0 ? "" : ConfigValue.PyStrip(text[(eq + 1)..]);
        if (eq < 0 || key.Length == 0 || raw.Length == 0)
        {
            throw new ArgumentException(
                $"'키=값' 꼴이어야 합니다: {ConfigValue.PyRepr(text)} (예: landscape.theta=0.5)");
        }
        object? value;
        try
        {
            OrderedDictionary<string, object?> doc = Toml.Loads("v = " + raw);
            value = doc["v"];
        }
        catch (TomlDecodeException)
        {
            value = new BareText(raw);
        }
        return (key, value);
    }

    private static string Kind(object? v) => v switch
    {
        bool => "참·거짓(true/false)",
        long or int => "정수",
        double => "실수",
        string => "글자",
        List<object?> => "리스트",
        OrderedDictionary<string, object?> => "dict",
        null => "NoneType",
        TomlDateTime t => t.Kind,
        _ => v.GetType().Name,
    };

    private static double Finite(string key, double v)
    {
        if (!double.IsFinite(v))
        {
            throw new ArgumentException(
                $"'{key}' 는 유한한 실수여야 합니다 (nan·inf 는 받지 않습니다): {PyJson.Repr(v)}");
        }
        return v;
    }

    /// <summary>_coerce: new 를 old 와 같은 형식으로 맞춥니다. 맞출 수 없으면 ArgumentException.</summary>
    private static object? Coerce(string key, object? old, object? @new)
    {
        if (@new is BareText bt && old is not string)
        {
            throw new ArgumentException(
                $"'{key}' 값 {ConfigValue.PyRepr(bt.Text)} 를 읽지 못했습니다. {Kind(old)} 값을 TOML 문법으로 주세요 "
                + $"(지금 값: {ConfigValue.PyRepr(old)})");
        }
        switch (old)
        {
            case bool:
                if (@new is bool nb)
                {
                    return nb;
                }
                break;
            case long:
                if (@new is long nl)
                {
                    return nl;
                }
                if (@new is double nd && double.IsFinite(nd) && Math.Floor(nd) == nd)
                {
                    if (!(nd >= -9223372036854775808.0 && nd < 9223372036854775808.0))
                    {
                        // Python int 는 크기 제한이 없지만 C# 은 long 이라 막습니다(의도한 차이).
                        throw new ArgumentException($"'{key}' 는 int64 범위의 정수여야 합니다: {PyJson.Repr(nd)}");
                    }
                    return (long)nd;
                }
                break;
            case double:
                if (@new is long l2)
                {
                    return Finite(key, l2);
                }
                if (@new is double d2)
                {
                    return Finite(key, d2);
                }
                break;
            case string:
                if (@new is string s2)
                {
                    return s2;
                }
                if (@new is BareText b2)
                {
                    return b2.Text;
                }
                break;
            case List<object?> oldList:
                if (@new is List<object?> newList)
                {
                    if (oldList.Count > 0 && newList.Count != oldList.Count)
                    {
                        throw new ArgumentException(
                            $"'{key}' 는 원소 {oldList.Count}개 리스트여야 합니다: {ConfigValue.PyRepr(newList)}");
                    }
                    if (oldList.Count == 0)
                    {
                        for (int i = 0; i < newList.Count; i++)
                        {
                            if (newList[i] is double dv)
                            {
                                Finite($"{key}[{i}]", dv);
                            }
                        }
                        return new List<object?>(newList);
                    }
                    var coerced = new List<object?>();
                    for (int i = 0; i < oldList.Count; i++)
                    {
                        coerced.Add(Coerce($"{key}[{i}]", oldList[i], newList[i]));
                    }
                    return coerced;
                }
                break;
        }
        throw new ArgumentException(
            $"'{key}' 는 {Kind(old)} 값이어야 합니다: {ConfigValue.PyRepr(@new)} (지금 값: {ConfigValue.PyRepr(old)})");
    }

    private static List<string> AllKeys(OrderedDictionary<string, object?> data, string prefix = "")
    {
        var output = new List<string>();
        foreach ((string k, object? v) in data)
        {
            string dotted = prefix + k;
            if (v is OrderedDictionary<string, object?> d)
            {
                output.AddRange(AllKeys(d, dotted + "."));
            }
            else
            {
                output.Add(dotted);
            }
        }
        return output;
    }

    /// <summary>
    /// checked_overrides: 바깥에서 받은 덮어쓰기를 설정의 기존 키·형식과 맞춰 봅니다. 반환: 형식을 맞춘 새 dict.
    /// 키는 이미 있는 값이어야 하고(비슷한 키를 알려 줌), 절과 FIXED_KEYS 는 못 바꿉니다.
    /// </summary>
    public static OrderedDictionary<string, object?> CheckedOverrides(
        Config cfg, IEnumerable<KeyValuePair<string, object?>> overrides)
    {
        OrderedDictionary<string, object?> data = cfg.AsDict();
        var output = new OrderedDictionary<string, object?>();
        foreach ((string dotted, object? value) in overrides)
        {
            if (FixedKeys.TryGetValue(dotted, out string? why))
            {
                throw new ArgumentException($"'{dotted}' 는 덮어쓸 수 없습니다. {why}");
            }
            object? node = data;
            foreach (string part in dotted.Split('.'))
            {
                if (node is not OrderedDictionary<string, object?> d || !d.TryGetValue(part, out object? next))
                {
                    List<string> close = DiffLib.CloseMatches(dotted, AllKeys(data), 3, 0.6);
                    string hint = close.Count > 0 ? $" 비슷한 키: {string.Join(", ", close)}" : "";
                    throw new ArgumentException($"설정에 '{dotted}' 키가 없습니다.{hint}");
                }
                node = next;
            }
            if (node is OrderedDictionary<string, object?>)
            {
                throw new ArgumentException($"'{dotted}' 는 절(section)이라 통째로 바꿀 수 없습니다");
            }
            output[dotted] = Coerce(dotted, node, ConfigValue.Normalize(value));
        }
        return output;
    }
}

/// <summary>설정 값 모델 도우미: 복사·형 바꾸기·Python repr·str.strip.</summary>
public static class ConfigValue
{
    /// <summary>C# 값을 값 모델로: int → long, float → double, 배열·리스트 → List, BareText 는 그대로.</summary>
    public static object? Normalize(object? v) => v switch
    {
        null or bool or long or double or string or BareText or TomlDateTime => v,
        int i => (long)i,
        short s => (long)s,
        sbyte sb => (long)sb,
        byte b => (long)b,
        uint u => (long)u,
        float f => (double)f,
        OrderedDictionary<string, object?> d => d,
        IEnumerable<KeyValuePair<string, object?>> kv => new OrderedDictionary<string, object?>(kv),
        IEnumerable e => e.Cast<object?>().Select(Normalize).ToList(),
        _ => v,
    };

    /// <summary>copy.deepcopy.</summary>
    public static object? DeepCopy(object? v) => v switch
    {
        OrderedDictionary<string, object?> d => DeepCopyDict(d),
        List<object?> l => l.Select(DeepCopy).ToList(),
        _ => v,
    };

    /// <summary>dict 깊은 복사 (순서 유지).</summary>
    public static OrderedDictionary<string, object?> DeepCopyDict(OrderedDictionary<string, object?> d)
    {
        var o = new OrderedDictionary<string, object?>(d.Count);
        foreach ((string k, object? v) in d)
        {
            o[k] = DeepCopy(v);
        }
        return o;
    }

    /// <summary>float(v).</summary>
    public static double ToDouble(object? v, string key) => v switch
    {
        double d => d,
        long l => l,
        bool b => b ? 1.0 : 0.0,
        string s when double.TryParse(PyStrip(s), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) => p,
        _ => throw new InvalidOperationException($"'{key}' 값을 실수로 바꿀 수 없습니다: {PyRepr(v)}"),
    };

    /// <summary>int(v): 실수는 0 쪽으로 자르고, NaN·inf·int64 밖이면 예외(포화하지 않음).</summary>
    public static long ToLong(object? v, string key)
    {
        switch (v)
        {
            case long l:
                return l;
            case bool b:
                return b ? 1L : 0L;
            case double d:
                double t = Math.Truncate(d);
                if (!double.IsFinite(t) || !(t >= -9223372036854775808.0 && t < 9223372036854775808.0))
                {
                    throw new InvalidOperationException($"'{key}' 값을 정수로 바꿀 수 없습니다: {PyJson.Repr(d)}");
                }
                return (long)t;
            default:
                throw new InvalidOperationException($"'{key}' 값을 정수로 바꿀 수 없습니다: {PyRepr(v)}");
        }
    }

    // Python str.strip() 이 지우는 공백 (str.isspace): .NET Trim() 에 없는 U+001C–U+001F 포함 29자
    private static readonly char[] PyWhitespace =
    [
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u001C', '\u001D', '\u001E', '\u001F', ' ',
        '\u0085', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
        '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
    ];

    /// <summary>Python str.strip().</summary>
    public static string PyStrip(string s) => s.Trim(PyWhitespace);

    /// <summary>Python str(v) (오류 문구·형 바꾸기용).</summary>
    public static string PyStr(object? v) => v switch
    {
        string s => s,
        BareText b => b.Text,
        _ => PyRepr(v),
    };

    /// <summary>Python repr(v) 근사 (오류 문구용).</summary>
    public static string PyRepr(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        long or int => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        double d => PyJson.Repr(d),
        string s => Toml.PyRepr(s),
        BareText bt => Toml.PyRepr(bt.Text),
        TomlDateTime t => t.Text,
        OrderedDictionary<string, object?> d =>
            "{" + string.Join(", ", d.Select(kv => Toml.PyRepr(kv.Key) + ": " + PyRepr(kv.Value))) + "}",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(PyRepr)) + "]",
        _ => v.ToString() ?? "",
    };
}

/// <summary>
/// difflib.get_close_matches (CPython 3.13 SequenceMatcher 를 code point 단위로 옮김, autojunk 포함).
/// </summary>
internal static class DiffLib
{
    public static List<string> CloseMatches(string word, IReadOnlyList<string> possibilities, int n, double cutoff)
    {
        int[] b = CodePoints(word);
        var scored = new List<(double Ratio, string X)>();
        foreach (string x in possibilities)
        {
            double r = Ratio(CodePoints(x), b);
            if (r >= cutoff)
            {
                scored.Add((r, x));
            }
        }
        // heapq.nlargest: (ratio, 글자) 내림차순
        return scored
            .OrderByDescending(t => t.Ratio)
            .ThenByDescending(t => t.X, PyJson.CodePointComparer.Instance)
            .Take(n)
            .Select(t => t.X)
            .ToList();
    }

    private static int[] CodePoints(string s)
    {
        var o = new List<int>();
        foreach (Rune r in s.EnumerateRunes())
        {
            o.Add(r.Value);
        }
        return o.ToArray();
    }

    /// <summary>SequenceMatcher(None, a, b).ratio() = 2·M/(len a + len b).</summary>
    public static double Ratio(int[] a, int[] b)
    {
        // __chain_b: b 의 원소 → 위치 목록. b 가 200 이상이면 흔한 원소(autojunk)를 뺍니다.
        var b2j = new Dictionary<int, List<int>>();
        for (int i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out List<int>? idx))
            {
                idx = [];
                b2j[b[i]] = idx;
            }
            idx.Add(i);
        }
        if (b.Length >= 200)
        {
            int ntest = (b.Length / 100) + 1;
            foreach (int elt in b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList())
            {
                b2j.Remove(elt);
            }
        }
        int matches = 0;
        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, a.Length, 0, b.Length));
        while (queue.Count > 0)
        {
            (int alo, int ahi, int blo, int bhi) = queue.Pop();
            (int i, int j, int k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                matches += k;
                if (alo < i && blo < j)
                {
                    queue.Push((alo, i, blo, j));
                }
                if (i + k < ahi && j + k < bhi)
                {
                    queue.Push((i + k, ahi, j + k, bhi));
                }
            }
        }
        int length = a.Length + b.Length;
        return length > 0 ? 2.0 * matches / length : 1.0;
    }

    private static (int, int, int) FindLongestMatch(
        int[] a, int[] b, Dictionary<int, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo;
        int bestj = blo;
        int bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (int i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out List<int>? js))
            {
                foreach (int j in js)
                {
                    if (j < blo)
                    {
                        continue;
                    }
                    if (j >= bhi)
                    {
                        break;
                    }
                    int k = (j2len.TryGetValue(j - 1, out int prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        // 흔한(autojunk) 원소는 b2j 에 없으므로 양 끝으로 늘려 붙입니다 (junk 는 없음).
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--;
            bestj--;
            bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
        {
            bestsize++;
        }
        return (besti, bestj, bestsize);
    }
}
