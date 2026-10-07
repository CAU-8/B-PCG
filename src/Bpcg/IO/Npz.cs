using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Bpcg.IO;

/// <summary>.npz 에 넣을 배열 하나: 이름, 원소 배열, 모양.</summary>
public sealed record NpzEntry(string Name, Array Data, long[] Shape)
{
    /// <summary>1차원 배열.</summary>
    public NpzEntry(string name, Array data) : this(name, data, [data.LongLength])
    {
    }
}

/// <summary>
/// numpy .npz 읽기·쓰기. 쓰기는 <c>np.savez</c>(ZIP_STORED, 로컬 zip64 extra, 1980-01-01 시각)와
/// POSIX 에서 바이트까지 같게 합니다 (docs/csharp_port.md 4장 .npz).
/// </summary>
public static class Npz
{
    private const ushort Version45 = 45;
    private const ushort MadeByUnix45 = 0x032D;
    private const ushort DosDate1980 = 0x0021;
    private const uint ExternalAttr0600 = 0x01800000;

    /// <summary>np.savez(path, **entries) 와 같은 바이트를 씁니다(항목 순서는 주어진 순서).</summary>
    public static void Write(Stream stream, IEnumerable<NpzEntry> entries)
    {
        var central = new MemoryStream();
        long offset = 0;
        int count = 0;
        foreach (NpzEntry e in entries)
        {
            var body = new MemoryStream();
            Npy.Write(body, e.Data, e.Shape);
            byte[] data = body.ToArray();
            uint crc = Crc32.Compute(data);
            byte[] name = Encoding.ASCII.GetBytes(e.Name + ".npy");
            if (offset > uint.MaxValue || data.LongLength > uint.MaxValue)
            {
                throw new NotSupportedException("4 GiB 를 넘는 npz 는 지원하지 않습니다");
            }

            byte[] local = new byte[30 + name.Length + 20];
            Span<byte> s = local;
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], Version45);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(s[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(s[18..], 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(s[22..], 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(s[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(s[28..], 20);
            name.CopyTo(s[30..]);
            Span<byte> extra = s[(30 + name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[4..], (ulong)data.LongLength);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[12..], (ulong)data.LongLength);
            stream.Write(local);
            stream.Write(data);

            byte[] cd = new byte[46 + name.Length];
            Span<byte> c = cd;
            BinaryPrimitives.WriteUInt32LittleEndian(c, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(c[4..], MadeByUnix45);
            BinaryPrimitives.WriteUInt16LittleEndian(c[6..], Version45);
            BinaryPrimitives.WriteUInt16LittleEndian(c[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[12..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[14..], DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(c[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(c[20..], (uint)data.LongLength);
            BinaryPrimitives.WriteUInt32LittleEndian(c[24..], (uint)data.LongLength);
            BinaryPrimitives.WriteUInt16LittleEndian(c[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(c[30..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(c[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(c[38..], ExternalAttr0600);
            BinaryPrimitives.WriteUInt32LittleEndian(c[42..], (uint)offset);
            name.CopyTo(c[46..]);
            central.Write(cd);

            offset += local.Length + data.LongLength;
            count++;
        }
        byte[] centralBytes = central.ToArray();
        stream.Write(centralBytes);
        byte[] end = new byte[22];
        Span<byte> d = end;
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], (uint)centralBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], (uint)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(d[20..], 0);
        stream.Write(end);
    }

    /// <summary>파일로 씁니다.</summary>
    public static void WriteFile(string path, IEnumerable<NpzEntry> entries)
    {
        using FileStream fs = File.Create(path);
        Write(fs, entries);
    }

    /// <summary>
    /// .npz 를 읽습니다. 이름은 '.npy' 를 뗀 키이고 순서는 zip 항목 순서입니다.
    /// keys 를 주면 그 항목만 읽습니다(나머지는 dtype 도 보지 않음).
    /// </summary>
    public static OrderedDictionary<string, NpyArray> Read(Stream stream, IReadOnlyCollection<string>? keys = null)
    {
        var result = new OrderedDictionary<string, NpyArray>();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string key = entry.FullName.EndsWith(".npy", StringComparison.Ordinal)
                ? entry.FullName[..^4]
                : entry.FullName;
            if (keys is not null && !keys.Contains(key))
            {
                continue;
            }
            using Stream s = entry.Open();
            var buf = new MemoryStream();
            s.CopyTo(buf);
            buf.Position = 0;
            result[key] = Npy.Read(buf);
        }
        return result;
    }

    /// <summary>파일에서 읽습니다.</summary>
    public static OrderedDictionary<string, NpyArray> ReadFile(string path, IReadOnlyCollection<string>? keys = null)
    {
        using FileStream fs = File.OpenRead(path);
        return Read(fs, keys);
    }
}

/// <summary>CRC-32 (IEEE 802.3, 다항식 0xEDB88320). zip·PNG 가 씁니다.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
            }
            t[n] = c;
        }
        return t;
    }

    /// <summary>바이트 전체의 CRC-32.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>앞선 CRC 에 이어서 계산합니다(zlib.crc32(data, crc) 와 같음).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc ^ 0xFFFFFFFFU;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFU;
    }
}
