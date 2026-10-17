using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bpcg.Core;

/// <summary>설정된 병렬 실행 수를 각 독립 셀 커널에 전달합니다.</summary>
public static class Parallelism
{
    private static readonly AsyncLocal<int?> Degree = new();

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
        int? degree = Degree.Value;
        if (degree == 1)
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
        Parallel.For(fromInclusive, toExclusive, options, body);
    }

    /// <summary>독립 항목을 결정적인 쓰기 구역으로 나눠 처리합니다.</summary>
    public static void ForEach<T>(System.Collections.Generic.IEnumerable<T> source, Action<T> body)
    {
        int? degree = Degree.Value;
        if (degree == 1)
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
        Parallel.ForEach(source, options, body);
    }

    /// <summary>ForRanges 가 0..n 을 나누는 조각 수. n 으로만 정해지고 실행 수와는 상관없습니다.</summary>
    public static int RangeCount(int n) => Math.Clamp(n / 8192, 1, 256);

    /// <summary>
    /// 0..n 을 RangeCount(n) 개 조각으로 나눠 조각마다 body(조각 번호, 시작, 끝)를 부릅니다. 조각 경계가 n 으로만
    /// 정해지므로, 조각마다 센 정수를 조각 번호 순서로 더하면 실행 수와 상관없이 같은 값이 나옵니다.
    /// </summary>
    public static void ForRanges(int n, Action<int, int, int> body)
    {
        int parts = RangeCount(n);
        For(0, parts, j => body(j, (int)((long)n * j / parts), (int)((long)n * (j + 1) / parts)));
    }

    /// <summary>
    /// 층별 병렬 (ForLevels): items[start[L]..start[L+1]) 가 층 L 입니다. 같은 층 안의 항목은 서로 기다리지 않고,
    /// 층 L 은 층 L−1 이 모두 끝난 뒤에 시작합니다. 예를 들어 강 나무에서 출구로부터 같은 칸 수만큼 떨어진 칸들이
    /// 한 층입니다. 한 칸의 높이는 바로 아래 칸에만 달려 있어 같은 층끼리는 동시에 쌓을 수 있습니다.
    /// body 가 항목마다 자기 자리에만 쓰면 실행 수와 상관없이 결과가 같습니다.
    /// </summary>
    public static void ForLevels(int[] start, int nLevels, int[] items, Action<int> body)
    {
        int threads = Degree.Value ?? Environment.ProcessorCount;
        if (threads <= 1 || nLevels == 0)
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
            var workers = new Thread[threads - 1];
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
