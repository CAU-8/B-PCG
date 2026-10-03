using System.Linq;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>core/hashing.py 대조: 모든 출력이 비트 단위로 같아야 합니다(정수 해시, 정확한 변환).</summary>
public sealed class HashingTests
{
    [Fact]
    public void SplitMix64()
    {
        GoldenCase g = Golden.Load("core/hashing", "splitmix64");
        ulong[] x = g.In("x").AsULong();
        ulong[] got = x.Select(Hashing.SplitMix64).ToArray();
        Compare.Equal(g.Out("h").AsULong(), got, "splitmix64");
    }

    [Fact]
    public void Hash3AndHashUnit()
    {
        GoldenCase g = Golden.Load("core/hashing", "hash3_hash_unit");
        long[] a = g.In("a").AsLong();
        long[] b = g.In("b").AsLong();
        long[] c = g.In("c").AsLong();
        ulong[] h3 = new ulong[a.Length];
        double[] hu = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            h3[i] = Hashing.Hash3(a[i], b[i], c[i]);
            hu[i] = Hashing.HashUnit(a[i], b[i], c[i]);
        }
        Compare.Equal(g.Out("hash3").AsULong(), h3, "hash3");
        Compare.Bits(g.Out("hash_unit").AsDouble(), hu, "hash_unit");
    }

    [Theory]
    [InlineData("hash_uniform_array_s0_t101")]
    [InlineData("hash_uniform_array_s0_t102")]
    [InlineData("hash_uniform_array_sm3_t101")]
    [InlineData("hash_uniform_array_sm3_t102")]
    [InlineData("hash_uniform_array_s9223372036854775807_t101")]
    [InlineData("hash_uniform_array_s9223372036854775807_t102")]
    public void HashUniformArray(string name)
    {
        GoldenCase g = Golden.Load("core/hashing", name);
        double[] got = Hashing.HashUniformArray(g.In("ids").AsLong(), g.In("seed").ScalarLong(), g.In("stream").ScalarLong());
        Compare.Bits(g.Out("u").AsDouble(), got, name);
    }
}
