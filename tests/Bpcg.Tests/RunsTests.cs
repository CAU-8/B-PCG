using System;
using System.IO;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests;

public sealed class RunsTests
{
    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void ApplyThreadsConfiguresParallelKernels(long configured, int expected)
    {
        Config cfg = Config.LoadConfig("earth", "tiny").WithOverrides(
            [new System.Collections.Generic.KeyValuePair<string, object?>("profile.compute.numba_threads", configured)]);
        int actual = Pipeline.ApplyThreads(cfg);
        Assert.Equal(configured == 0 ? Environment.ProcessorCount : Math.Min(configured, Environment.ProcessorCount), actual);
        Assert.Equal(expected < 0 ? Environment.ProcessorCount : expected, Parallelism.MaxDegreeOfParallelism);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllDoesNotReloadSavedStates(bool flat)
    {
        string runDir = Path.Combine(Path.GetTempPath(), "bpcg-runs-" + Guid.NewGuid());
        try
        {
            Config cfg = Config.LoadConfig("earth", "tiny");
            int removed = 0;
            Runs.All(cfg, runDir, flat, null, line =>
            {
                // 저장 완료 직후 graph.npz를 없애면 디스크 재로드는 실패해야 합니다.
                string? level = line.StartsWith("[행성] 묶음을 썼습니다:", StringComparison.Ordinal)
                    ? Runs.PlanetDir
                    : line.StartsWith("[히어로] 묶음을 썼습니다:", StringComparison.Ordinal)
                        ? Runs.HeroDir : null;
                if (level is not null)
                {
                    string graph = Path.Combine(runDir, level, "graph.npz");
                    Assert.True(File.Exists(graph));
                    File.Delete(graph);
                    removed++;
                }
            });
            Assert.Equal(flat ? 1 : 2, removed);
            Assert.True(File.Exists(Path.Combine(runDir, Runs.CorridorDir, "manifest.json")));
            if (!flat)
            {
                Assert.True(File.Exists(Path.Combine(runDir, Runs.GlobeDir, "globe.json")));
            }
        }
        finally
        {
            if (Directory.Exists(runDir))
            {
                Directory.Delete(runDir, true);
            }
        }
    }
}
