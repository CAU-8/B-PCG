using System;
using System.IO;
using Bpcg.Bake;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>
/// 저장 정밀도(float32)로 둔 필드를 float[] 그대로 읽는 길이, double[] 로 넓혀 읽던 길과 같은 값·바이트를 내는지.
/// </summary>
public sealed class Float32StorageTests
{
    // 음수·0·−0·큰 값·작은 값이 섞인 float 값.
    private static float[] Values(int n)
    {
        float[] f = new float[n];
        for (int i = 0; i < n; i++)
        {
            f[i] = (float)((Hashing.HashUnit(41, i, 0) - 0.5) * Math.Pow(10.0, (i % 9) - 3));
        }
        f[0] = -0.0f;
        f[1] = 0.0f;
        return f;
    }

    private static double[] Widened(float[] f) => Array.ConvertAll(f, v => (double)v);

    [Fact]
    public void RealViewReadsFloatAndDoubleTheSame()
    {
        float[] f = Values(1000);
        double[] d = Widened(f);
        var vf = new RealView(f);
        var vd = new RealView(d);
        Assert.Equal(d.Length, vf.Length);
        for (int i = 0; i < d.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(d[i]), BitConverter.DoubleToInt64Bits(vf[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(d[i]), BitConverter.DoubleToInt64Bits(vd[i]));
        }
        Compare.Bits(d, FieldSet.ToFloat64(f), "ToFloat64");
        Assert.Throws<InvalidCastException>(() => new RealView(new int[3]));
    }

    [Fact]
    public void GhostLayersAndSamplingFromFloatMatchWidened()
    {
        const int n = 16;
        float[] f = Values(6 * n * n);
        double[] d = Widened(f);
        Compare.Bits(Resample.AddGhostLayers(d, n, 2), Resample.AddGhostLayers(f, n, 2), "고스트 줄");
        CellGraph g = Graph.SphereGraph(24, 6_371_000.0, 0.4, 3);
        double[] unit = g.Unit();
        Compare.Bits(Resample.SampleSphere(d, n, unit), Resample.SampleSphereReal(f, n, unit), "구면 표본");
    }

    [Fact]
    public void HeightmapFromFloatWritesSameBytes()
    {
        float[] f = Values(12 * 7);
        string dir = Directory.CreateTempSubdirectory("bpcg-hm-").FullName;
        try
        {
            string a = Path.Combine(dir, "a");
            string b = Path.Combine(dir, "b");
            Heightmap.WriteHeightmap(a, Widened(f), 12, 7, 2.5, [1.0, 2.0, 3.0]);
            Heightmap.WriteHeightmap(b, f, 12, 7, 2.5, [1.0, 2.0, 3.0]);
            Assert.Equal(File.ReadAllBytes(a + ".bin"), File.ReadAllBytes(b + ".bin"));
            Assert.Equal(File.ReadAllText(a + ".json"), File.ReadAllText(b + ".json"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
