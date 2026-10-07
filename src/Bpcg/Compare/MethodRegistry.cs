using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bpcg.Compare;

/// <summary>매개변수 하나. Kind: float, int, bool, str. Lo·Hi: 허용 범위(같음 포함). Choices: 후보.</summary>
public sealed record Param(
    string Name,
    object Default,
    string Help,
    string Kind = "float",
    double? Lo = null,
    double? Hi = null,
    string[]? Choices = null,
    string Unit = "")
{
    /// <summary>값을 검사해 이 매개변수의 형식(double, long, bool, string)으로 돌려줍니다. 틀리면 ArgumentException.</summary>
    public object Coerce(object? value, string where)
    {
        switch (Kind)
        {
            case "bool":
                return value is bool b ? b : throw new ArgumentException($"{where}: 참·거짓이어야 합니다: {Show(value)}");
            case "str":
                if (value is not string s)
                {
                    throw new ArgumentException($"{where}: 글자여야 합니다: {Show(value)}");
                }
                if (Choices is not null && !Choices.Contains(s))
                {
                    throw new ArgumentException($"{where}: [{string.Join(", ", Choices)}] 가운데 하나여야 합니다: '{s}'");
                }
                return s;
            case "int":
                {
                    double d = Number(value, where);
                    if (d != Math.Floor(d))
                    {
                        throw new ArgumentException($"{where}: 정수여야 합니다: {Show(value)}");
                    }
                    CheckRange(d, where);
                    return (long)d;
                }
            default:
                {
                    double d = Number(value, where);
                    CheckRange(d, where);
                    return d;
                }
        }
    }

    private void CheckRange(double v, string where)
    {
        if (Lo is double lo && v < lo)
        {
            throw new ArgumentException($"{where}: {Show(lo)} 이상이어야 합니다: {Show(v)}");
        }
        if (Hi is double hi && v > hi)
        {
            throw new ArgumentException($"{where}: {Show(hi)} 이하여야 합니다: {Show(v)}");
        }
    }

    private static double Number(object? value, string where)
    {
        double d = value switch
        {
            long l => l,
            int i => i,
            double x => x,
            float f => f,
            _ => throw new ArgumentException($"{where}: 숫자여야 합니다: {Show(value)}"),
        };
        if (!double.IsFinite(d))
        {
            throw new ArgumentException($"{where}: 유한한 값이어야 합니다: {Show(value)}");
        }
        return d;
    }

    internal static string Show(object? v) => v switch
    {
        null => "없음",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public OrderedDictionary<string, object?> Describe() => new()
    {
        ["name"] = Name,
        ["default"] = Default,
        ["kind"] = Kind,
        ["help"] = Help,
        ["unit"] = Unit,
        ["lo"] = Lo,
        ["hi"] = Hi,
        ["choices"] = Choices?.Cast<object?>().ToList(),
    };
}

/// <summary>검사를 마친 매개변수 값 (이름 → double·long·bool·string). 순서는 방법의 매개변수 순서입니다.</summary>
public sealed class ParamValues
{
    private readonly OrderedDictionary<string, object> _values;

    public ParamValues(OrderedDictionary<string, object> values) => _values = values;

    public double F(string name) => Convert.ToDouble(_values[name], CultureInfo.InvariantCulture);

    public int I(string name) => (int)(long)_values[name];

    public bool B(string name) => (bool)_values[name];

    public string S(string name) => (string)_values[name];

    public OrderedDictionary<string, object?> AsDict()
    {
        var d = new OrderedDictionary<string, object?>();
        foreach ((string k, object v) in _values)
        {
            d[k] = v;
        }
        return d;
    }

    internal static double ToDouble(object? v, string where) => v switch
    {
        long l => l,
        int i => i,
        double d => d,
        _ => throw new ArgumentException($"{where}: 숫자여야 합니다: {Param.Show(v)}"),
    };

    internal static long ToLong(object? v, string where) => v switch
    {
        long l => l,
        int i => i,
        double d when d == Math.Floor(d) => (long)d,
        _ => throw new ArgumentException($"{where}: 정수여야 합니다: {Param.Show(v)}"),
    };
}

/// <summary>방법 하나의 결과. Z: (n·n,) 고도 [m]. Layers: 덧붙일 (n·n,) 지도. Info: 진단값.</summary>
public sealed class MethodOutput(double[] z)
{
    public double[] Z { get; } = z;

    public OrderedDictionary<string, double[]> Layers { get; } = [];

    public OrderedDictionary<string, object?> Info { get; } = [];
}

/// <summary>
/// 등록된 방법 하나. Generate 는 같은 (격자, 시드, 매개변수)면 늘 같은 고도를 내야 합니다(결정성).
/// 무작위 값은 <see cref="Core.Hashing"/> 의 정수 해시로 만듭니다.
/// </summary>
public sealed record CompareMethod(
    string Name,
    string Label,
    string Family,
    string Ref,
    string Summary,
    Param[] Params,
    Func<CompareGrid, long, ParamValues, CompareContext, MethodOutput> Generate)
{
    /// <summary>기본값에 덮어쓰기를 얹고 모두 검사합니다. 모르는 키는 ArgumentException.</summary>
    public ParamValues Resolve(IReadOnlyDictionary<string, object?>? overrides, string where)
    {
        var values = new OrderedDictionary<string, object>();
        foreach (Param p in Params)
        {
            values[p.Name] = p.Coerce(p.Default, $"{Name}.{p.Name} 기본값");
        }
        if (overrides is not null)
        {
            foreach ((string key, object? value) in overrides)
            {
                Param p = Params.FirstOrDefault(x => x.Name == key)
                    ?? throw new ArgumentException($"{where}: 방법 '{Name}' 에 없는 매개변수입니다: {key}");
                values[key] = p.Coerce(value, $"{where}.{key}");
            }
        }
        return new ParamValues(values);
    }

    public OrderedDictionary<string, object?> Describe() => new()
    {
        ["name"] = Name,
        ["label"] = Label,
        ["family"] = Family,
        ["family_label"] = MethodRegistry.Families[Family],
        ["ref"] = Ref,
        ["summary"] = Summary,
        ["params"] = Params.Select(p => (object?)p.Describe()).ToList(),
    };
}

/// <summary>
/// 방법 등록부 (docs/compare.md 4장). 새 방법은 이 폴더에 정적 클래스를 하나 만들고 <see cref="Builtins"/> 에 한 줄을 더합니다.
/// </summary>
public static class MethodRegistry
{
    /// <summary>계열 이름 → 한국어 이름 (화면 순서).</summary>
    public static readonly OrderedDictionary<string, string> Families = new()
    {
        ["procedural"] = "절차적 생성",
        ["simulation"] = "시뮬레이션",
        ["example"] = "예제 기반",
        ["ours"] = "B-PCG",
    };

    private static readonly Lazy<OrderedDictionary<string, CompareMethod>> All = new(Build);

    /// <summary>등록된 방법 (계열 순서, 등록 순서).</summary>
    private static IEnumerable<CompareMethod> Builtins() =>
    [
        NoiseMethods.Fbm,
        NoiseMethods.Ridged,
        NoiseMethods.Multifractal,
        NoiseMethods.Warped,
        SubdivisionMethod.DiamondSquare,
        FaultingMethod.Faulting,
        ErosionMethods.Thermal,
        ErosionMethods.Hydraulic,
        StreamPowerMethod.StreamPower,
        QuiltingMethod.Quilting,
        BpcgMethod.Bpcg,
    ];

    private static OrderedDictionary<string, CompareMethod> Build()
    {
        var d = new OrderedDictionary<string, CompareMethod>();
        foreach (CompareMethod m in Builtins())
        {
            if (!Families.ContainsKey(m.Family))
            {
                throw new InvalidOperationException($"방법 '{m.Name}' 의 계열이 올바르지 않습니다: {m.Family}");
            }
            if (m.Params.Select(p => p.Name).Distinct().Count() != m.Params.Length)
            {
                throw new InvalidOperationException($"방법 '{m.Name}' 의 매개변수 이름이 겹칩니다");
            }
            m.Resolve(null, m.Name); // 기본값이 모두 검사를 통과하는지
            d[m.Name] = m;
        }
        return d;
    }

    public static IReadOnlyList<CompareMethod> AllMethods()
    {
        List<string> order = Families.Keys.ToList();
        return All.Value.Values.OrderBy(m => order.IndexOf(m.Family)).ToList();
    }

    public static CompareMethod Get(string name) =>
        All.Value.TryGetValue(name, out CompareMethod? m)
            ? m
            : throw new ArgumentException($"등록되지 않은 방법입니다: '{name}' (있는 것: {string.Join(", ", All.Value.Keys.Order(StringComparer.Ordinal))})");
}
