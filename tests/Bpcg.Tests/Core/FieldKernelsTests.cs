using System;
using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Core;

public sealed class FieldKernelsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(257)]
    [InlineData(16_387)]
    public void SimdFieldKernelMatchesScalar(int count)
    {
        Random random = new(6226);
        double[] primary = new double[count];
        double[] secondary = new double[count];
        double[] scalar = new double[count];
        double[] simd = new double[count];
        for (int i = 0; i < count; i++)
        {
            primary[i] = random.NextDouble() * 5.0;
            secondary[i] = random.NextDouble() * 20.0;
        }

        FieldKernels.MaxProductFloor(primary, secondary, 0.1, 0.01, scalar, useSimd: false);
        FieldKernels.MaxProductFloor(primary, secondary, 0.1, 0.01, simd, useSimd: true);

        Assert.Equal(scalar, simd);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ChunkedKernelMatchesScalarWithConfiguredParallelism(int threads)
    {
        const int count = 32_771;
        double[] primary = new double[count];
        double[] secondary = new double[count];
        double[] expected = new double[count];
        double[] actual = new double[count];
        for (int i = 0; i < count; i++)
        {
            primary[i] = i * 0.125;
            secondary[i] = (count - i) * 0.25;
        }

        FieldKernels.MaxProductFloor(primary, secondary, 0.5, 2.0, expected, useSimd: false);
        Parallelism.Configure(threads);
        try
        {
            FieldKernels.MaxProductFloorParallel(primary, secondary, 0.5, 2.0, actual, chunkSize: 3_000);
        }
        finally
        {
            Parallelism.Configure(null);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParallelismUsesDeterministicDisjointChunks()
    {
        const int count = 10_003;
        int[] actual = new int[count];
        Parallelism.Configure(4);
        try
        {
            Parallelism.ForChunks(0, count, 137, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    actual[i]++;
                }
            });
        }
        finally
        {
            Parallelism.Configure(null);
        }

        Assert.All(actual, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ClimateFieldsAreBitIdenticalAcrossThreadModes()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        CellGraph graph = Graph.SphereGraph(32, cfg.F("planet.radius_m"), cfg.F("landscape.jitter"), 6226);
        FieldSet single;
        Parallelism.Configure(1);
        try
        {
            single = Climate.GenerateClimate(graph, cfg, seed: 6226);
        }
        finally
        {
            Parallelism.Configure(null);
        }

        Parallelism.Configure(4);
        try
        {
            FieldSet parallel = Climate.GenerateClimate(graph, cfg, seed: 6226);
            foreach (string name in single.Names)
            {
                Assert.Equal((double[])single[name]!, (double[])parallel[name]!);
            }
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }
}
