using System;
using System.Linq;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>층별 병렬(ForLevels)과 큰 일 나눔(ForCoarse)이 실행 수와 상관없이 같은 결과를 내는지.</summary>
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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ForCoarseCoversEveryIndexOnceAndRunsInnerLoopsInline(int threads)
    {
        const int n = 37;
        int[] hits = new int[n];
        double[] sums = new double[n];
        Parallelism.Configure(threads);
        try
        {
            Parallelism.ForCoarse(0, n, i =>
            {
                hits[i]++;
                double[] inner = new double[5000];
                Parallelism.For(0, inner.Length, k => inner[k] = Hashing.HashUnit(13, i, k));
                double s = 0.0;
                foreach (double v in inner)
                {
                    s += v;
                }
                sums[i] = s;
            });
        }
        finally
        {
            Parallelism.Configure(null);
        }
        Assert.All(hits, h => Assert.Equal(1, h));
        for (int i = 0; i < n; i++)
        {
            double expected = 0.0;
            for (int k = 0; k < 5000; k++)
            {
                expected += Hashing.HashUnit(13, i, k);
            }
            Compare.Bits(expected, sums[i], $"조각 {i}");
        }
    }

    // .NET 의 Parallel 은 몸체의 예외를 AggregateException 으로 모아 던집니다. 그대로 두면 한 줄로 돌 때와
    // 여러 줄로 돌 때의 예외 형이 달라, 부르는 쪽의 catch (ArgumentException) 이 스레드 수에 따라 다르게 동작합니다.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void HelpersRethrowTheBodyExceptionWhateverTheThreadCount(int threads)
    {
        static void Fail(int i) => throw new ArgumentException($"시험용 오류 {i}");
        Parallelism.Configure(threads);
        try
        {
            // 5,000 은 병렬 문턱(1,024)보다 커서, 실행 수가 2 이상이면 여러 범위로 나눠 돕니다.
            ArgumentException e = Assert.Throws<ArgumentException>(() => Parallelism.For(0, 5000, i =>
            {
                if (i == 3210)
                {
                    Fail(i);
                }
            }));
            Assert.Equal("시험용 오류 3210", e.Message);
            e = Assert.Throws<ArgumentException>(() => Parallelism.ForChunks(0, 5000, 100, (a, _) =>
            {
                if (a == 3200)
                {
                    Fail(a);
                }
            }));
            Assert.Equal("시험용 오류 3200", e.Message);
            e = Assert.Throws<ArgumentException>(() => Parallelism.ForTasks(8, i =>
            {
                if (i == 5)
                {
                    Fail(i);
                }
            }));
            Assert.Equal("시험용 오류 5", e.Message);
            e = Assert.Throws<ArgumentException>(() => Parallelism.ForCoarse(0, 8, i =>
            {
                if (i == 6)
                {
                    Fail(i);
                }
            }));
            Assert.Equal("시험용 오류 6", e.Message);
            e = Assert.Throws<ArgumentException>(() => Parallelism.ForEach(Enumerable.Range(0, 5000).ToList(), i =>
            {
                if (i == 4321)
                {
                    Fail(i);
                }
            }));
            Assert.Equal("시험용 오류 4321", e.Message);
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    // 여러 항목이 실패해도 모은 예외(AggregateException)가 아니라 몸체가 던진 예외 하나가 나갑니다.
    // 어느 항목의 것인지는 정하지 않습니다(다른 범위가 먼저 실패하면 앞 범위는 돌지 않고 멈출 수 있음).
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void WhenManyItemsFailOneBodyExceptionIsReported(int threads)
    {
        Parallelism.Configure(threads);
        try
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                Parallelism.For(0, 5000, i => throw new ArgumentException($"시험용 오류 {i}")));
            Assert.StartsWith("시험용 오류 ", e.Message, StringComparison.Ordinal);
            e = Assert.Throws<ArgumentException>(() =>
                Parallelism.ForTasks(8, i => throw new ArgumentException($"시험용 오류 {i}")));
            Assert.StartsWith("시험용 오류 ", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Parallelism.Configure(null);
        }
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
