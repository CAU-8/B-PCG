using System;
using System.Collections;
using System.Collections.Generic;

namespace Bpcg.Core;

/// <summary>
/// 필드 묶음: 이름 → 칸마다의 배열 (Python <c>dict[str, np.ndarray]</c>, docs/pipeline.md 2장).
/// </summary>
/// <remarks>
/// 삽입 순서를 지킵니다(이미 있는 이름에 다시 넣으면 자리는 그대로, Python dict 와 같음).
/// 배열은 원소 형 그대로(double[], float[], int[], byte[], sbyte[], bool[], long[]) 둡니다. FIELDS 가 float32 인
/// 필드도 계산 중에는 Python 처럼 double[] 일 수 있고, 묶음에 쓸 때만 FIELDS dtype 으로 바꿉니다. 단계가 끝나 저장
/// 정밀도로 반올림한 뒤(Runs.UseSavedPrecision)와 묶음에서 읽은 뒤에는 float[] 로 둡니다. 그런 필드는
/// <see cref="GetReal"/> 로 복사 없이 읽습니다.
/// (N, L) 필드는 행 우선 평평한 배열이고 열 수를 <see cref="Columns"/> 에 둡니다.
/// </remarks>
public sealed class FieldSet : IEnumerable<KeyValuePair<string, Array>>
{
    private readonly OrderedDictionary<string, Array> _data = [];
    private readonly Dictionary<string, int> _columns = [];

    /// <summary>배열 읽기·쓰기.</summary>
    public Array this[string name]
    {
        get => _data.TryGetValue(name, out Array? a)
            ? a
            : throw new KeyNotFoundException($"필드 '{name}' 이 없습니다");
        set => _data[name] = value;
    }

    /// <summary>원소 형을 정해 읽습니다.</summary>
    public T[] Get<T>(string name) => this[name] as T[]
        ?? throw new InvalidCastException($"필드 '{name}' 의 원소 형이 {typeof(T).Name} 이 아닙니다: {this[name].GetType().Name}");

    /// <summary>np.asarray(fields[name], dtype=np.float64): double[] 는 그대로, float[] 는 정확히 넓힌 새 배열.</summary>
    public double[] GetFloat64(string name) => ToFloat64(this[name]);

    /// <summary>실수 필드(double[] 또는 float[])를 복사하지 않고 읽습니다. float 은 읽을 때 정확히 넓힙니다.</summary>
    public RealView GetReal(string name) => new(this[name]);

    /// <summary>원소 형에 상관없이 np.asarray(fields[name], dtype=np.float64).</summary>
    public double[] GetFloat64Any(string name) => this[name] is double[] d ? d : (double[])CastTo(this[name], typeof(double));

    /// <summary>실수 배열을 double[] 로 (np.asarray(a, dtype=np.float64)).</summary>
    /// <remarks>float[] 는 새 배열로 넓힙니다(칸마다 제 자리만 쓰므로 나눠 돌림). 한 번 읽고 놓는 곳에서만 씁니다.</remarks>
    public static double[] ToFloat64(Array a) => a switch
    {
        double[] d => d,
        float[] f => Widen(f),
        _ => throw new InvalidCastException($"실수 배열이어야 합니다: {a.GetType().Name}"),
    };

    private static double[] Widen(float[] f)
    {
        double[] o = new double[f.Length];
        Parallelism.For(0, f.Length, i => o[i] = f[i]);
        return o;
    }

    /// <summary>
    /// a.astype(t) (numpy 규칙): 실수 → float32 는 가장 가까운 값, 실수 → 정수는 0 쪽으로 자름, 정수끼리는 비트를 잘라 감쌈,
    /// 불리언은 0/1. 형이 같으면 그대로 돌려줍니다.
    /// </summary>
    public static Array CastTo(Array a, Type t)
    {
        Type s = a.GetType().GetElementType()!;
        if (s == t)
        {
            return a;
        }
        Array? fast = CastFast(a, t);
        if (fast is not null)
        {
            return fast;
        }
        int n = a.Length;
        Array o = Array.CreateInstance(t, n);
        for (int i = 0; i < n; i++)
        {
            object v = a.GetValue(i)!;
            o.SetValue(ConvertScalar(v, t), i);
        }
        return o;
    }

    // 기본 수 형끼리는 원소마다 상자(GetValue·SetValue)를 만들지 않고 바꿉니다. 값은 ConvertScalar 와 같습니다:
    // 실수 → 정수는 0 쪽으로 자르고(유한하지 않으면 long.MinValue) 감싸고, 불리언은 0/1, 정수는 long 을 거쳐 감쌉니다.
    private static Array? CastFast(Array a, Type t) => a switch
    {
        double[] d => FromReal(d.Length, i => d[i], t, i => d[i] != 0.0),
        float[] f => FromReal(f.Length, i => f[i], t, i => f[i] != 0.0f),
        bool[] b => FromInteger(b.Length, i => b[i] ? 1L : 0L, t),
        byte[] x => FromInteger(x.Length, i => x[i], t),
        sbyte[] x => FromInteger(x.Length, i => x[i], t),
        short[] x => FromInteger(x.Length, i => x[i], t),
        ushort[] x => FromInteger(x.Length, i => x[i], t),
        int[] x => FromInteger(x.Length, i => x[i], t),
        uint[] x => FromInteger(x.Length, i => x[i], t),
        long[] x => FromInteger(x.Length, i => x[i], t),
        ulong[] x => FromInteger(x.Length, i => unchecked((long)x[i]), t),
        _ => null,
    };

    private static Array? FromReal(int n, Func<int, double> get, Type t, Func<int, bool> nonZero)
    {
        if (t == typeof(bool))
        {
            bool[] o = new bool[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = nonZero(i);
            }
            return o;
        }
        if (t == typeof(double))
        {
            double[] o = new double[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = get(i);
            }
            return o;
        }
        if (t == typeof(float))
        {
            float[] o = new float[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = (float)get(i);
            }
            return o;
        }
        return FromInteger(n, i =>
        {
            double d = get(i);
            return double.IsFinite(d) ? (long)Math.Truncate(d) : long.MinValue;
        }, t);
    }

    private static Array? FromInteger(int n, Func<int, long> get, Type t)
    {
        static T[] Fill<T>(int n, Func<int, T> f)
        {
            T[] o = new T[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = f(i);
            }
            return o;
        }
        return unchecked(t switch
        {
            Type u when u == typeof(bool) => Fill(n, i => get(i) != 0),
            Type u when u == typeof(double) => Fill(n, i => (double)get(i)),
            Type u when u == typeof(float) => Fill(n, i => (float)get(i)),
            Type u when u == typeof(long) => Fill(n, get),
            Type u when u == typeof(int) => Fill(n, i => (int)get(i)),
            Type u when u == typeof(short) => Fill(n, i => (short)get(i)),
            Type u when u == typeof(sbyte) => Fill(n, i => (sbyte)get(i)),
            Type u when u == typeof(byte) => Fill(n, i => (byte)get(i)),
            Type u when u == typeof(ushort) => Fill(n, i => (ushort)get(i)),
            Type u when u == typeof(uint) => Fill(n, i => (uint)get(i)),
            Type u when u == typeof(ulong) => Fill(n, i => (ulong)get(i)),
            _ => (Array?)null,
        });
    }

    private static object ConvertScalar(object v, Type t)
    {
        if (v is bool b)
        {
            v = b ? 1L : 0L;
        }
        if (t == typeof(bool))
        {
            return v switch
            {
                double d => d != 0.0,
                float f => f != 0.0f,
                _ => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) != 0,
            };
        }
        if (v is double or float)
        {
            double d = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(double))
            {
                return d;
            }
            if (t == typeof(float))
            {
                return (float)d;
            }
            // TODO(port): numpy 는 NaN·inf·범위 밖 실수 → 정수가 정의되지 않습니다(플랫폼 값). 여기서는 0 쪽으로 자르고 감쌉니다.
            long l = double.IsFinite(d) ? (long)Math.Truncate(d) : long.MinValue;
            return WrapInt(l, t);
        }
        long x = v switch
        {
            ulong u => unchecked((long)u),
            _ => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture),
        };
        if (t == typeof(double))
        {
            return (double)x;
        }
        if (t == typeof(float))
        {
            return (float)x;
        }
        return WrapInt(x, t);
    }

    private static object WrapInt(long x, Type t) => unchecked(t switch
    {
        Type u when u == typeof(long) => x,
        Type u when u == typeof(int) => (int)x,
        Type u when u == typeof(short) => (short)x,
        Type u when u == typeof(sbyte) => (sbyte)x,
        Type u when u == typeof(byte) => (byte)x,
        Type u when u == typeof(ushort) => (ushort)x,
        Type u when u == typeof(uint) => (uint)x,
        Type u when u == typeof(ulong) => (ulong)x,
        _ => throw new InvalidCastException($"바꿀 수 없는 형: {t.Name}"),
    });

    /// <summary>FIELDS 에 적힌 dtype 으로 바꿉니다 (v.astype(FIELDS[name].dtype)).</summary>
    public static Array CastToField(string name, Array a) => CastTo(a, Fields.ElementType(Fields.All[name].Dtype));

    /// <summary>np.asarray(a)[idx]: 1차원 배열에서 idx 칸을 모읍니다 (원소 형 유지).</summary>
    public static Array Take(Array a, long[] idx) => a switch
    {
        // 기본 수 형은 원소마다 상자(GetValue·SetValue)를 만들지 않고 바로 모읍니다.
        double[] x => TakeTyped(x, idx),
        float[] x => TakeTyped(x, idx),
        bool[] x => TakeTyped(x, idx),
        byte[] x => TakeTyped(x, idx),
        sbyte[] x => TakeTyped(x, idx),
        short[] x => TakeTyped(x, idx),
        ushort[] x => TakeTyped(x, idx),
        int[] x => TakeTyped(x, idx),
        uint[] x => TakeTyped(x, idx),
        long[] x => TakeTyped(x, idx),
        ulong[] x => TakeTyped(x, idx),
        _ => TakeBoxed(a, idx),
    };

    private static T[] TakeTyped<T>(T[] a, long[] idx)
    {
        T[] o = new T[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            o[k] = a[idx[k]];
        }
        return o;
    }

    private static Array TakeBoxed(Array a, long[] idx)
    {
        Type t = a.GetType().GetElementType()!;
        Array o = Array.CreateInstance(t, idx.Length);
        for (int k = 0; k < idx.Length; k++)
        {
            o.SetValue(a.GetValue(idx[k]), k);
        }
        return o;
    }

    /// <summary>이름이 있는지.</summary>
    public bool Contains(string name) => _data.ContainsKey(name);

    /// <summary>필드 수.</summary>
    public int Count => _data.Count;

    /// <summary>이름 (삽입 순서).</summary>
    public IEnumerable<string> Names => _data.Keys;

    /// <summary>(N, L) 필드의 열 수 L. 1차원 필드는 없음.</summary>
    public IReadOnlyDictionary<string, int> Columns => _columns;

    /// <summary>(N, L) 필드를 넣습니다.</summary>
    public void Set2D(string name, Array data, int columns)
    {
        _data[name] = data;
        _columns[name] = columns;
    }

    /// <summary>dict.update: 다른 묶음의 필드를 넣습니다(있는 이름은 자리를 지키고 값만 바뀜).</summary>
    public void Update(FieldSet other)
    {
        foreach (KeyValuePair<string, Array> kv in other)
        {
            _data[kv.Key] = kv.Value;
            if (other._columns.TryGetValue(kv.Key, out int cols))
            {
                _columns[kv.Key] = cols;
            }
        }
    }

    /// <summary>얕은 복사 (Python dict(...) / {**a}).</summary>
    public FieldSet Copy()
    {
        var o = new FieldSet();
        o.Update(this);
        return o;
    }

    /// <summary>이름을 지웁니다 (dict.pop).</summary>
    public bool Remove(string name)
    {
        _columns.Remove(name);
        return _data.Remove(name);
    }

    public IEnumerator<KeyValuePair<string, Array>> GetEnumerator() => _data.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 실수 필드를 복사하지 않고 읽는 창: double[] 이나 float[] 를 그대로 가리키고, float 은 읽을 때 double 로 넓힙니다.
/// float → double 은 값이 정확히 그대로라, double[] 로 넓혀 둔 배열을 읽는 것과 같은 값입니다.
/// </summary>
public readonly struct RealView
{
    private readonly double[]? _d;
    private readonly float[]? _f;

    public RealView(Array a)
    {
        switch (a)
        {
            case double[] d:
                _d = d;
                break;
            case float[] f:
                _f = f;
                break;
            default:
                throw new InvalidCastException($"실수 배열이어야 합니다: {a.GetType().Name}");
        }
    }

    public int Length => _d?.Length ?? _f!.Length;

    public double this[int i] => _d is not null ? _d[i] : _f![i];

    /// <summary>double[] 로 담긴 창이면 그 배열 (아니면 null).</summary>
    public double[]? AsDoubles => _d;

    /// <summary>float[] 로 담긴 창이면 그 배열 (아니면 null).</summary>
    public float[]? AsFloats => _f;
}
