using System;
using System.Text.Json;
using System.Threading.Tasks;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>core/noise.py 대조: FMA 없는 같은 식이라 비트 단위로 같아야 합니다.</summary>
public sealed class NoiseTests
{
    [Fact]
    public void GradientNoise3()
    {
        GoldenCase g = Golden.Load("core/noise", "gradient_noise3");
        double[] x = g.In("x").AsDouble();
        double[] y = g.In("y").AsDouble();
        double[] z = g.In("z").AsDouble();
        long[] s = g.In("seed").AsLong();
        double[] got = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            got[i] = Noise.GradientNoise3(x[i], y[i], z[i], s[i]);
        }
        Compare.Bits(g.Out("v").AsDouble(), got, "gradient_noise3");
    }

    [Theory]
    [InlineData("octave_tables_0")]
    [InlineData("octave_tables_1")]
    [InlineData("octave_tables_2")]
    [InlineData("octave_tables_3")]
    public void OctaveTables(string name)
    {
        GoldenCase g = Golden.Load("core/noise", name);
        (ulong[] seeds, double[] offsets, double[] freqs, double[] amps, double invNorm) = Noise.OctaveTables(
            g.In("seed").ScalarLong(), (int)g.In("n_comp").ScalarLong(), (int)g.In("octaves").ScalarLong(),
            g.In("gain").ScalarDouble(), g.In("lacunarity").ScalarDouble(), g.In("frequency").ScalarDouble());
        Compare.Equal(g.Out("seeds").AsULong(), seeds, "seeds");
        Compare.Bits(g.Out("offsets").AsDouble(), offsets, "offsets");
        Compare.Bits(g.Out("freqs").AsDouble(), freqs, "freqs");
        Compare.Bits(g.Out("amps").AsDouble(), amps, "amps");
        Compare.Bits(g.Out("inv_norm").ScalarDouble(), invNorm, "inv_norm");
    }

    [Theory]
    [InlineData("fbm3_test_p", false)]
    [InlineData("fbm3_test_p_123", false)]
    [InlineData("fbm3_random", false)]
    [InlineData("fbm3_random_params", false)]
    [InlineData("vector_fbm3_test_p", true)]
    [InlineData("vector_fbm3_random", true)]
    public void Fbm(string name, bool vector)
    {
        GoldenCase g = Golden.Load("core/noise", name);
        double[] pts = g.In("points").AsDouble();
        long seed = g.In("seed").ScalarLong();
        JsonElement kw = g.Meta.GetProperty("kwargs");
        int octaves = kw.TryGetProperty("octaves", out JsonElement o) ? o.GetInt32() : 5;
        double gain = kw.TryGetProperty("gain", out JsonElement ga) ? ga.GetDouble() : 0.5;
        double lac = kw.TryGetProperty("lacunarity", out JsonElement la) ? la.GetDouble() : 2.0;
        double freq = kw.TryGetProperty("frequency", out JsonElement fr) ? fr.GetDouble() : 1.0;
        double[] got = vector
            ? Noise.VectorFbm3(pts, seed, octaves, gain, lac, freq)
            : Noise.Fbm3(pts, seed, octaves, gain, lac, freq);
        Compare.Bits(g.Out("v").AsDouble(), got, name);
    }

    [Fact]
    public void FbmIsThreadCountIndependent()
    {
        // numba prange 와 같이 점마다 나누므로 병렬도 1 과 기본값의 결과가 같아야 합니다.
        GoldenCase g = Golden.Load("core/noise", "fbm3_random");
        double[] pts = g.In("points").AsDouble();
        double[] a = Noise.VectorFbm3(pts, 7);
        double[] b = new double[pts.Length];
        Parallel.For(0, 1, new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ => b = Noise.VectorFbm3(pts, 7));
        Compare.Bits(a, b, "병렬도");
    }

    [Fact]
    public void Errors()
    {
        GoldenCase g = Golden.Load("core/noise", "fbm3_errors");
        double[] p = [0.6, -0.48, 0.64, 0.0, 0.0, 1.0, -0.36, 0.48, -0.8];
        Assert.Throws<ArgumentException>(() => Noise.Fbm3(p, 0, octaves: 0));
        Assert.Throws<ArgumentException>(() => Noise.Fbm3(p, 0, gain: 0.0));
        Assert.Throws<ArgumentException>(() => Noise.Fbm3(p, 0, frequency: -1.0));
        Assert.Throws<ArgumentException>(() => Noise.Fbm3([double.NaN, 0.0, 0.0], 0));
        Assert.Throws<ArgumentException>(() => Noise.Fbm3(new double[8], 0));
        Assert.Empty(Noise.Fbm3([], 0));
        Assert.Empty(g.Out("empty").AsDouble());
    }
}
