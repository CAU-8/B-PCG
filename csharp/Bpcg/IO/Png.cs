using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Bpcg.IO;

/// <summary>
/// 8비트 RGBA PNG 쓰기 (matplotlib.image.imsave 대체).
/// </summary>
/// <remarks>
/// TODO(port): matplotlib(Pillow)와 바이트까지 같지는 않습니다(압축 수준·필터·메타데이터가 다름). 픽셀 값은 같습니다.
/// </remarks>
public static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>RGBA (h, w, 4) 행 우선, 0번 행이 그림 맨 위입니다.</summary>
    public static void WriteRgba(string path, byte[] rgba, int width, int height, string software = "bpcg")
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("rgba 길이가 width·height·4 와 다릅니다");
        }
        using var ms = new MemoryStream();
        ms.Write(Signature);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; // 비트 깊이
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        byte[] text = Encoding.Latin1.GetBytes("Software\0" + software);
        WriteChunk(ms, "tEXt", text);
        byte[] phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(0), 3937); // 100 dpi
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), 3937);
        phys[8] = 1;
        WriteChunk(ms, "pHYs", phys);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgba, y * width * 4, width * 4);
                }
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0, t), data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}
