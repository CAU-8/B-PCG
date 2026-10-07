using System;
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
}
