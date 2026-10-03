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
/// 필드도 계산 중에는 Python 처럼 double[] 일 수 있고, 묶음에 쓸 때만 FIELDS dtype 으로 바꿉니다.
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

    /// <summary>실수 배열을 double[] 로 (np.asarray(a, dtype=np.float64)).</summary>
    public static double[] ToFloat64(Array a) => a switch
    {
        double[] d => d,
        float[] f => Array.ConvertAll(f, v => (double)v),
        _ => throw new InvalidCastException($"실수 배열이어야 합니다: {a.GetType().Name}"),
    };

    /// <summary>np.asarray(a)[idx]: 1차원 배열에서 idx 칸을 모읍니다 (원소 형 유지).</summary>
    public static Array Take(Array a, long[] idx)
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
