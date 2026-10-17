using System;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>층별 병렬(ForLevels)과 조각 나눔(ForRanges)이 실행 수와 상관없이 같은 결과를 내는지.</summary>
public sealed class ParallelismTests
{
    // 부모 번호가 늘 자기보다 작은 나무 n 칸. 층(뿌리에서 몇 칸)으로 묶은 (start, items, 층 수, 부모).
    private static (int[] Start, int[] Items, int NLevels, int[] Parent) Tree(int n)
    {
        int[] parent = new int[n];
        int[] level = new int[n];
        int nLevels = 1;
        for (int c = 1; c < n; c++)
        {
            parent[c] = (int)(Hashing.HashUnit(11, c, 0) * c);
            level[c] = level[parent[c]] + 1;
            nLevels = Math.Max(nLevels, level[c] + 1);
        }
        int[] start = new int[nLevels + 1];
        foreach (int lv in level)
        {
            start[lv + 1]++;
        }
        for (int lv = 0; lv < nLevels; lv++)
        {
            start[lv + 1] += start[lv];
        }
        int[] fill = (int[])start.Clone();
        int[] items = new int[n];
        for (int c = 0; c < n; c++)
        {
            items[fill[level[c]]++] = c;
        }
        return (start, items, nLevels, parent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void ForLevelsMatchesSerial(int threads)
    {
        const int n = 60_000;
        (int[] start, int[] items, int nLevels, int[] parent) = Tree(n);
        double[] expected = new double[n];
        for (int c = 1; c < n; c++)
        {
            expected[c] = (expected[parent[c]] * 0.5) + Hashing.HashUnit(12, c, 0);
        }
        double[] actual = new double[n];
        int[] visits = new int[n];
        Parallelism.Configure(threads);
        try
        {
            Parallelism.ForLevels(start, nLevels, items, c =>
            {
                visits[c]++;
                actual[c] = c == 0 ? 0.0 : (actual[parent[c]] * 0.5) + Hashing.HashUnit(12, c, 0);
            });
        }
        finally
        {
            Parallelism.Configure(null);
        }
        Compare.Bits(expected, actual, "층별 결과");
        Assert.All(visits, v => Assert.Equal(1, v));
    }

    [Fact]
    public void ForLevelsRethrowsWithoutHanging()
    {
        (int[] start, int[] items, int nLevels, _) = Tree(20_000);
        Parallelism.Configure(4);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                Parallelism.ForLevels(start, nLevels, items, c =>
                {
                    if (c == 12_345)
                    {
                        throw new InvalidOperationException("시험용 오류");
                    }
                }));
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    [Fact]
    public void ForRangesCoversEveryIndexOnce()
    {
        const int n = 100_003;
        int[] hits = new int[n];
        int[] perRange = new int[Parallelism.RangeCount(n)];
        Parallelism.ForRanges(n, (j, lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                hits[i]++;
            }
            perRange[j] = hi - lo;
        });
        Assert.All(hits, h => Assert.Equal(1, h));
        Assert.Equal(n, SumOf(perRange));
    }

    private static int SumOf(int[] v)
    {
        int s = 0;
        foreach (int x in v)
        {
            s += x;
        }
        return s;
    }
}
