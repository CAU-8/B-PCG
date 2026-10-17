using System;
using System.Collections.Concurrent;
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
        // 인덱스마다 AsyncLocal 을 쓰면 쓸 때마다 실행 문맥을 새로 만들어, 호출 한 번에 수백 MB 를 할당하고
        // 몸체보다 비싸집니다. 범위로 나눠 범위마다 한 번만 씁니다. 범위 수는 실행 수의 8배로 잡아
        // 성능·효율 코어가 섞인 CPU 에서도 늦게 끝나는 범위가 전체를 붙잡지 않게 합니다.
        int workers = degree ?? Environment.ProcessorCount;
        int rangeSize = Math.Max(1, (count + (workers * 8) - 1) / (workers * 8));
        Parallel.ForEach(Partitioner.Create(fromInclusive, toExclusive, rangeSize), options, range =>
        {
            int previous = Nesting.Value;
            Nesting.Value = previous + 1;
            try
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    body(i);
                }
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

    /// <summary>
    /// 무거운 독립 작업 count 개를 병렬로 돌립니다 (작업 번호 0..count−1). <see cref="For"/> 와 달리 작업 수 문턱이 없어
    /// 수백 개의 큰 작업(굽기 조각 등)도 나눠 돌고, 작업 안에서 부르는 병렬 커널은 중첩으로 보아 직렬로 돕니다.
    /// 작업마다 결과를 따로 두고 번호 순서대로 합치면 결과가 실행 수와 상관없이 같습니다.
    /// </summary>
    public static void ForTasks(int count, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int? degree = Degree.Value;
        if (degree == 1 || Nesting.Value > 0 || count <= 1)
        {
            for (int i = 0; i < count; i++)
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
        Parallel.For(0, count, options, i =>
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
