using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bpcg.Core;

/// <summary>설정된 병렬 실행 수를 각 독립 셀 커널에 전달합니다.</summary>
public static class Parallelism
{
    // Parallel.For is expensive for tiny fields, and nested calls can create a
    // second scheduler layer. Keep those cases on the current worker.
    public const int ParallelThreshold = 1024;

    private static readonly AsyncLocal<int?> Degree = new();
    private static readonly AsyncLocal<int> Nesting = new();

    /// <summary>현재 실행 흐름의 최대 병렬 실행 수. null이면 .NET 자동값을 사용합니다.</summary>
    public static int? MaxDegreeOfParallelism => Degree.Value;

    /// <summary>병렬 커널의 최대 실행 수를 지정합니다.</summary>
    public static void Configure(int? maxDegree)
    {
        if (maxDegree is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegree), "병렬 실행 수는 1 이상이어야 합니다");
        }
        Degree.Value = maxDegree;
    }

    /// <summary>독립 인덱스를 결정적인 쓰기 구역으로 나눠 처리합니다.</summary>
    public static void For(int fromInclusive, int toExclusive, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int count = toExclusive - fromInclusive;
        int? degree = Degree.Value;
        if (degree == 1 || Nesting.Value > 0 || count <= ParallelThreshold)
        {
            for (int i = fromInclusive; i < toExclusive; i++)
            {
                body(i);
            }
            return;
        }
        var options = new ParallelOptions();
        if (degree is int max)
        {
            options.MaxDegreeOfParallelism = max;
        }
        Parallel.For(fromInclusive, toExclusive, options, i =>
        {
            int previous = Nesting.Value;
            Nesting.Value = previous + 1;
            try
            {
                body(i);
            }
            finally
            {
                Nesting.Value = previous;
            }
        });
    }

    /// <summary>큰 필드를 덩어리 단위로 나눠 처리합니다. 각 callback은 [start, end) 범위를 받습니다.</summary>
    public static void ForChunks(int fromInclusive, int toExclusive, int chunkSize, Action<int, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "덩어리 크기는 1 이상이어야 합니다");
        }
        int count = toExclusive - fromInclusive;
        if (count <= 0)
        {
            return;
        }
        int chunks = (count + chunkSize - 1) / chunkSize;
        int? degree = Degree.Value;
        void RunChunk(int chunk)
        {
            int start = fromInclusive + (chunk * chunkSize);
            int previous = Nesting.Value;
            Nesting.Value = previous + 1;
            try
            {
                body(start, Math.Min(toExclusive, start + chunkSize));
            }
            finally
            {
                Nesting.Value = previous;
            }
        }
        if (degree == 1 || Nesting.Value > 0 || count <= ParallelThreshold || chunks == 1)
        {
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                RunChunk(chunk);
            }
            return;
        }

        var options = new ParallelOptions();
        if (degree is int max)
        {
            options.MaxDegreeOfParallelism = max;
        }
        Parallel.For(0, chunks, options, RunChunk);
    }

    /// <summary>독립 항목을 결정적인 쓰기 구역으로 나눠 처리합니다.</summary>
    public static void ForEach<T>(System.Collections.Generic.IEnumerable<T> source, Action<T> body)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        int? degree = Degree.Value;
        if (degree == 1 || Nesting.Value > 0
            || source is System.Collections.Generic.ICollection<T> collection && collection.Count <= ParallelThreshold)
        {
            foreach (T item in source)
            {
                body(item);
            }
            return;
        }
        var options = new ParallelOptions();
        if (degree is int max)
        {
            options.MaxDegreeOfParallelism = max;
        }
        Parallel.ForEach(source, options, item =>
        {
            int previous = Nesting.Value;
            Nesting.Value = previous + 1;
            try
            {
                body(item);
            }
            finally
            {
                Nesting.Value = previous;
            }
        });
    }
}
