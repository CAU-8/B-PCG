using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Bpcg.IO;

/// <summary>.npy 에서 읽은 배열: dtype 문자열, 모양, C 순서 평평한 원소 배열.</summary>
public sealed class NpyArray
{
    public NpyArray(string descr, long[] shape, Array data)
    {
        Descr = descr;
        Shape = shape;
        Data = data;
    }

    /// <summary>numpy descr (예: '&lt;f8', '|b1').</summary>
    public string Descr { get; }

    /// <summary>모양. 0차원이면 빈 배열입니다.</summary>
    public long[] Shape { get; }

    /// <summary>C 순서 원소 (double[], float[], long[], int[], byte[], sbyte[], bool[], ulong[] …).</summary>
    public Array Data { get; }

    /// <summary>원소 수 (모양의 곱).</summary>
    public long Count => Data.LongLength;

    public double[] AsDouble() => (double[])Data;

    public float[] AsFloat() => (float[])Data;

    public long[] AsLong() => (long[])Data;

    public int[] AsInt() => (int[])Data;

    public byte[] AsByte() => (byte[])Data;

    public sbyte[] AsSByte() => (sbyte[])Data;

    public bool[] AsBool() => (bool[])Data;

    public ulong[] AsULong() => (ulong[])Data;

    /// <summary>0차원 또는 원소 하나인 배열의 값을 double 로.</summary>
    public double ScalarDouble() => Convert.ToDouble(Data.GetValue(0), CultureInfo.InvariantCulture);

    /// <summary>0차원 또는 원소 하나인 정수 배열의 값을 long 으로.</summary>
    public long ScalarLong() => Data switch
    {
        ulong[] u => unchecked((long)u[0]),
        bool[] b => b[0] ? 1L : 0L,
        _ => Convert.ToInt64(Data.GetValue(0), CultureInfo.InvariantCulture),
    };

    /// <summary>0차원 bool 의 값.</summary>
    public bool ScalarBool() => Data switch
    {
        bool[] b => b[0],
        _ => ScalarLong() != 0,
    };
}

/// <summary>
/// numpy .npy 형식 읽기·쓰기 (numpy 2.5.3 <c>np.save</c> 와 바이트까지 같게, docs/csharp_port.md 4장).
/// </summary>
public static class Npy
{
    private static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    /// <summary>원소 형식 → numpy descr.</summary>
    public static string DescrOf(Type t)
    {
        if (t == typeof(double)) { return "<f8"; }
        if (t == typeof(float)) { return "<f4"; }
        if (t == typeof(long)) { return "<i8"; }
        if (t == typeof(int)) { return "<i4"; }
        if (t == typeof(short)) { return "<i2"; }
        if (t == typeof(sbyte)) { return "|i1"; }
        if (t == typeof(byte)) { return "|u1"; }
        if (t == typeof(ushort)) { return "<u2"; }
        if (t == typeof(uint)) { return "<u4"; }
        if (t == typeof(ulong)) { return "<u8"; }
        if (t == typeof(bool)) { return "|b1"; }
        throw new ArgumentException($"npy 로 쓸 수 없는 원소 형식입니다: {t}");
    }

    /// <summary>np.save 와 같은 바이트로 씁니다. shape 의 곱은 data 길이와 같아야 합니다.</summary>
    public static void Write(Stream stream, Array data, IReadOnlyList<long> shape)
    {
        long count = 1;
        foreach (long s in shape)
        {
            count *= s;
        }
        if (count != data.LongLength)
        {
            throw new ArgumentException($"모양 {ShapeRepr(shape)} 과 원소 수 {data.LongLength} 가 다릅니다");
        }
        Type t = data.GetType().GetElementType()!;
        string descr = DescrOf(t);
        byte[] header = Header(descr, shape);
        stream.Write(header);
        stream.Write(DataBytes(data, t));
    }

    /// <summary>1차원 배열을 (n,) 모양으로 씁니다.</summary>
    public static void Write(Stream stream, Array data) => Write(stream, data, [data.LongLength]);

    /// <summary>파일로 씁니다 (np.save 처럼 임시 파일 없이 바로).</summary>
    public static void WriteFile(string path, Array data, IReadOnlyList<long> shape)
    {
        using FileStream fs = File.Create(path);
        Write(fs, data, shape);
    }

    /// <summary>np.save 머리글: magic, 버전 1.0, 길이, 딕셔너리 글, 64바이트 정렬 공백, 줄바꿈.</summary>
    public static byte[] Header(string descr, IReadOnlyList<long> shape)
    {
        string h = "{'descr': '" + descr + "', 'fortran_order': False, 'shape': " + ShapeRepr(shape) + ", }";
        if (shape.Count > 0)
        {
            // numpy GROWTH_AXIS_MAX_DIGITS: 첫 축이 자라도 머리글을 다시 쓰지 않게 남겨 두는 공백
            int digits = shape[0].ToString(CultureInfo.InvariantCulture).Length;
            h += new string(' ', Math.Max(0, 21 - digits));
        }
        int pad = 64 - ((10 + h.Length + 1) % 64);
        string full = h + new string(' ', pad) + "\n";
        if (full.Length > ushort.MaxValue)
        {
            throw new NotSupportedException("npy 머리글이 너무 깁니다 (버전 2.0 은 지원하지 않습니다)");
        }
        byte[] text = Encoding.Latin1.GetBytes(full);
        byte[] outBytes = new byte[10 + text.Length];
        Magic.CopyTo(outBytes, 0);
        outBytes[6] = 1;
        outBytes[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(outBytes.AsSpan(8), (ushort)text.Length);
        text.CopyTo(outBytes, 10);
        return outBytes;
    }

    /// <summary>Python repr(tuple): (), (6144,), (6144, 6).</summary>
    public static string ShapeRepr(IReadOnlyList<long> shape)
    {
        if (shape.Count == 0)
        {
            return "()";
        }
        if (shape.Count == 1)
        {
            return "(" + shape[0].ToString(CultureInfo.InvariantCulture) + ",)";
        }
        return "(" + string.Join(", ", shape.Select(s => s.ToString(CultureInfo.InvariantCulture))) + ")";
    }

    private static byte[] DataBytes(Array data, Type t)
    {
        if (t == typeof(bool))
        {
            bool[] b = (bool[])data;
            byte[] o = new byte[b.Length];
            for (int i = 0; i < b.Length; i++)
            {
                o[i] = b[i] ? (byte)1 : (byte)0;
            }
            return o;
        }
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("big-endian 기계는 지원하지 않습니다");
        }
        if (t == typeof(double))
        {
            return CanonicalNaN((double[])data);
        }
        if (t == typeof(float))
        {
            return CanonicalNaN((float[])data);
        }
        return RawBytes(data, t);
    }

    // .NET double.NaN 은 부호 비트가 서 있어(0xFFF8…) numpy np.nan(0x7FF8…)과 바이트가 다릅니다.
    // 묶음의 NaN 은 모두 양의 quiet NaN 이므로 쓸 때 맞춥니다(docs/csharp_port.md 4장 .npy).
    private static byte[] CanonicalNaN(double[] a)
    {
        byte[] o = new byte[a.Length * 8];
        Span<byte> span = o;
        for (int i = 0; i < a.Length; i++)
        {
            double v = a[i];
            ulong bits = double.IsNaN(v) ? 0x7FF8000000000000UL : BitConverter.DoubleToUInt64Bits(v);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(i * 8)..], bits);
        }
        return o;
    }

    private static byte[] CanonicalNaN(float[] a)
    {
        byte[] o = new byte[a.Length * 4];
        Span<byte> span = o;
        for (int i = 0; i < a.Length; i++)
        {
            float v = a[i];
            uint bits = float.IsNaN(v) ? 0x7FC00000U : BitConverter.SingleToUInt32Bits(v);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(i * 4)..], bits);
        }
        return o;
    }

    private static byte[] RawBytes(Array data, Type t)
    {
        return data switch
        {
            long[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            int[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            short[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            sbyte[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            byte[] a => (byte[])a.Clone(),
            ushort[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            uint[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            ulong[] a => MemoryMarshal.AsBytes(a.AsSpan()).ToArray(),
            _ => throw new ArgumentException($"npy 로 쓸 수 없는 원소 형식입니다: {t}"),
        };
    }

    /// <summary>.npy 를 읽습니다. Fortran 순서면 C 순서로 바꿉니다.</summary>
    public static NpyArray Read(Stream stream)
    {
        byte[] head = ReadExactly(stream, 8);
        if (!head.AsSpan(0, 6).SequenceEqual(Magic))
        {
            throw new InvalidDataException("npy 파일이 아닙니다 (magic 이 다름)");
        }
        int major = head[6];
        int headerLen;
        if (major == 1)
        {
            headerLen = BinaryPrimitives.ReadUInt16LittleEndian(ReadExactly(stream, 2));
        }
        else if (major is 2 or 3)
        {
            headerLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(stream, 4)));
        }
        else
        {
            throw new InvalidDataException($"지원하지 않는 npy 버전입니다: {major}.{head[7]}");
        }
        byte[] hb = ReadExactly(stream, headerLen);
        string header = major == 3 ? Encoding.UTF8.GetString(hb) : Encoding.Latin1.GetString(hb);
        (string descr, bool fortran, long[] shape) = ParseHeader(header);
        long count = 1;
        foreach (long s in shape)
        {
            count *= s;
        }
        (Type t, int size) = TypeOf(descr);
        byte[] raw = ReadExactly(stream, checked((int)(count * size)));
        Array data = FromBytes(raw, t, descr, checked((int)count));
        if (fortran && shape.Length > 1)
        {
            data = FortranToC(data, shape);
        }
        return new NpyArray(Canonical(descr), shape, data);
    }

    /// <summary>파일에서 읽습니다.</summary>
    public static NpyArray ReadFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Read(fs);
    }

    private static string Canonical(string descr) => descr switch
    {
        "<f8" or "=f8" => "<f8",
        "<f4" or "=f4" => "<f4",
        "<i8" or "=i8" => "<i8",
        "<i4" or "=i4" => "<i4",
        "<i2" or "=i2" => "<i2",
        "<u2" or "=u2" => "<u2",
        "<u4" or "=u4" => "<u4",
        "<u8" or "=u8" => "<u8",
        _ => descr,
    };

    private static (Type, int) TypeOf(string descr) => Canonical(descr) switch
    {
        "<f8" => (typeof(double), 8),
        "<f4" => (typeof(float), 4),
        "<i8" => (typeof(long), 8),
        "<i4" => (typeof(int), 4),
        "<i2" => (typeof(short), 2),
        "|i1" => (typeof(sbyte), 1),
        "|u1" => (typeof(byte), 1),
        "<u2" => (typeof(ushort), 2),
        "<u4" => (typeof(uint), 4),
        "<u8" => (typeof(ulong), 8),
        "|b1" => (typeof(bool), 1),
        _ => throw new InvalidDataException($"지원하지 않는 npy dtype 입니다: {descr}"),
    };

    private static Array FromBytes(byte[] raw, Type t, string descr, int count)
    {
        if (t == typeof(bool))
        {
            bool[] b = new bool[count];
            for (int i = 0; i < count; i++)
            {
                b[i] = raw[i] != 0;
            }
            return b;
        }
        if (t == typeof(byte))
        {
            return raw;
        }
        Array data = Array.CreateInstance(t, count);
        Span<byte> dst = data switch
        {
            double[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            float[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            long[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            int[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            short[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            sbyte[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            ushort[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            uint[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            ulong[] a => MemoryMarshal.AsBytes(a.AsSpan()),
            _ => throw new InvalidDataException($"지원하지 않는 npy dtype 입니다: {descr}"),
        };
        raw.AsSpan().CopyTo(dst);
        return data;
    }

    private static Array FortranToC(Array data, long[] shape)
    {
        int n = data.Length;
        Array outArr = Array.CreateInstance(data.GetType().GetElementType()!, n);
        int nd = shape.Length;
        long[] idx = new long[nd];
        for (int c = 0; c < n; c++)
        {
            long rem = c;
            for (int d = nd - 1; d >= 0; d--)
            {
                idx[d] = rem % shape[d];
                rem /= shape[d];
            }
            long f = 0;
            long mul = 1;
            for (int d = 0; d < nd; d++)
            {
                f += idx[d] * mul;
                mul *= shape[d];
            }
            outArr.SetValue(data.GetValue(f), c);
        }
        return outArr;
    }

    // numpy 는 ast.literal_eval 로 읽습니다. 여기서는 세 키만 꺼냅니다(순서·공백 무관).
    private static (string Descr, bool Fortran, long[] Shape) ParseHeader(string h)
    {
        string descr = ValueAfter(h, "'descr'").Trim().Trim('\'', '"');
        string fortranText = ValueAfter(h, "'fortran_order'").Trim();
        bool fortran = fortranText.StartsWith("True", StringComparison.Ordinal);
        int s0 = h.IndexOf("'shape'", StringComparison.Ordinal);
        int open = h.IndexOf('(', s0);
        int close = h.IndexOf(')', open);
        string inner = h[(open + 1)..close];
        long[] shape = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.Parse(s.TrimEnd('L'), CultureInfo.InvariantCulture)).ToArray();
        return (descr, fortran, shape);
    }

    private static string ValueAfter(string h, string key)
    {
        int k = h.IndexOf(key, StringComparison.Ordinal);
        if (k < 0)
        {
            throw new InvalidDataException($"npy 머리글에 {key} 가 없습니다: {h}");
        }
        int colon = h.IndexOf(':', k + key.Length);
        int end = h.IndexOf(',', colon + 1);
        return h[(colon + 1)..end];
    }

    private static byte[] ReadExactly(Stream s, int n)
    {
        byte[] b = new byte[n];
        s.ReadExactly(b);
        return b;
    }
}
