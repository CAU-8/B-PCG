using System;
using System.IO;
using Bpcg.Bake;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests;

public sealed class RunsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ApplyThreadsConfiguresParallelKernels(long configured)
    {
        Config cfg = Config.LoadConfig("earth", "tiny").WithOverrides(
            [
                new System.Collections.Generic.KeyValuePair<string, object?>(
                    "profile.compute.numba_threads", configured
                ),
            ]
        );
        int actual = Pipeline.ApplyThreads(cfg);
        int expected = configured == 0
            ? Environment.ProcessorCount
            : (int)Math.Min(configured, Environment.ProcessorCount);
        Assert.Equal(expected, actual);
        Assert.Equal(expected, Parallelism.MaxDegreeOfParallelism);
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

    // 같은 폴더에 묶음을 다시 쓰다가 멈추면, 옛 목차(시드·설정)와 새 배열이 섞인 묶음이 남아서는 안 됩니다.
    [Fact]
    public void RewritingABundleRemovesTheOldManifestBeforeTheArrays()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bpcg-bundle-" + Guid.NewGuid());
        try
        {
            CellGraph g = Graph.FlatGraph(4, 4, 100.0);
            string manifest = Path.Combine(dir, Bundle.ManifestFile);
            var first = new FieldSet { ["z_m"] = new double[16] };
            Bundle.WriteBundle(dir, g, first);
            Assert.True(File.Exists(manifest));

            // 둘째 쓰기는 slope(이름순으로 먼저) 를 쓴 뒤 z_m 의 길이가 틀려 멈춥니다.
            var second = new FieldSet { ["slope"] = new double[16], ["z_m"] = new double[3] };
            Assert.Throws<ArgumentException>(() => Bundle.WriteBundle(dir, g, second));
            Assert.True(File.Exists(Path.Combine(dir, "surface", "slope.npy")));
            Assert.False(File.Exists(manifest));
            Assert.Contains("manifest", Assert.ThrowsAny<Exception>(() => Bundle.ReadManifest(dir)).Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
