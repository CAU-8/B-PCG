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

    private static int EnterNested()
    {
        int previous = Nesting.Value;
        Nesting.Value = previous + 1;
        return previous;
    }

    private static void LeaveNested(int previous) => Nesting.Value = previous;

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
        Parallel.ForEach(source, options, EnterNested, (item, _, previous) =>
        {
            body(item);
            return previous;
        }, LeaveNested);
    }

    /// <summary>
    /// 항목 하나가 큰 일(예: 동굴 조각 하나에 수십 ms)일 때 씁니다. For 와 달리 항목이 적어도 나눕니다.
    /// 항목 안에서 부르는 For 는 한 줄로 돕니다(중첩).
    /// </summary>
    public static void ForCoarse(int fromInclusive, int toExclusive, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int? degree = Degree.Value;
        if (degree == 1 || Nesting.Value > 0 || toExclusive - fromInclusive <= 1)
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
        Parallel.For(fromInclusive, toExclusive, options, EnterNested, (i, _, previous) =>
        {
            body(i);
            return previous;
        }, LeaveNested);
    }

    /// <summary>
    /// 층별 병렬 (ForLevels): items[start[L]..start[L+1]) 가 층 L 입니다. 같은 층 안의 항목은 서로 기다리지 않고,
    /// 층 L 은 층 L−1 이 모두 끝난 뒤에 시작합니다. 예를 들어 강 나무에서 출구로부터 같은 칸 수만큼 떨어진 칸들이
    /// 한 층입니다. 한 칸의 높이는 바로 아래 칸에만 달려 있어 같은 층끼리는 동시에 쌓을 수 있습니다.
    /// body 가 항목마다 자기 자리에만 쓰면 실행 수와 상관없이 결과가 같습니다.
    /// </summary>
    public static void ForLevels(int[] start, int nLevels, int[] items, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int threads = Degree.Value ?? Environment.ProcessorCount;
        if (threads <= 1 || Nesting.Value > 0 || nLevels == 0)
        {
            for (int k = start[0]; k < start[nLevels]; k++)
            {
                body(items[k]);
            }
            return;
        }
        new LevelTeam(threads, start, nLevels, items, body).Run();
    }

    /// <summary>
    /// ForLevels 의 일꾼 묶음. 층마다 32개씩 항목을 먼저 집어 가는 쪽이 처리하고(빠른 코어가 더 많이 가져감),
    /// 모두 끝나면 장벽에서 만나 다음 층으로 갑니다. 층이 수천 개라 장벽은 잠들지 않고 돌며 기다립니다.
    /// </summary>
    private sealed class LevelTeam(int threads, int[] start, int nLevels, int[] items, Action<int> body)
    {
        private const int Chunk = 32;
        private readonly int[] _next = new int[nLevels];
        private int _arrived;
        private int _sense;
        private volatile bool _abort;
        private Exception? _error;

        public void Run()
        {
            Thread[] workers = new Thread[threads - 1];
            for (int t = 1; t < threads; t++)
            {
                int id = t;
                workers[t - 1] = new Thread(() => Work(id)) { IsBackground = true };
                workers[t - 1].Start();
            }
            Work(0);
            foreach (Thread w in workers)
            {
                w.Join();
            }
            if (_error is not null)
            {
                ExceptionDispatchInfo.Capture(_error).Throw();
            }
        }

        private void Work(int id)
        {
            int previous = EnterNested();
            try
            {
                int sense = 0;
                for (int level = 0; level < nLevels; level++)
                {
                    if (!_abort)
                    {
                        try
                        {
                            Level(id, level);
                        }
                        catch (Exception e)
                        {
                            // 다른 일꾼이 장벽에서 영원히 기다리지 않게, 오류가 나도 장벽까지는 갑니다.
                            Interlocked.CompareExchange(ref _error, e, null);
                            _abort = true;
                        }
                    }
                    Wait(ref sense);
                }
            }
            finally
            {
                LeaveNested(previous);
            }
        }

        private void Level(int id, int level)
        {
            int a = start[level];
            int b = start[level + 1];
            if (b - a < 2 * Chunk)
            {
                if (id == 0)
                {
                    for (int k = a; k < b; k++)
                    {
                        body(items[k]);
                    }
                }
                return;
            }
            while (true)
            {
                int lo = a + ((Interlocked.Increment(ref _next[level]) - 1) * Chunk);
                if (lo >= b)
                {
                    return;
                }
                int hi = Math.Min(lo + Chunk, b);
                for (int k = lo; k < hi; k++)
                {
                    body(items[k]);
                }
            }
        }

        private void Wait(ref int sense)
        {
            sense ^= 1;
            if (Interlocked.Increment(ref _arrived) == threads)
            {
                _arrived = 0;
                Volatile.Write(ref _sense, sense);
                return;
            }
            var spin = new SpinWait();
            while (Volatile.Read(ref _sense) != sense)
            {
                spin.SpinOnce(-1);
            }
        }
    }
}
