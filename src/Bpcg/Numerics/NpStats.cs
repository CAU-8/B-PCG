using System;
using System.Collections.Generic;

namespace Bpcg.Numerics;

/// <summary>numpy 통계 함수 (np.median, np.percentile linear, 마스크 합).</summary>
public static class NpStats
{
    private static double[] SortedOrNull(IEnumerable<double> values)
    {
        var list = new List<double>(values);
        foreach (double v in list)
        {
            if (double.IsNaN(v))
            {
                return [];
            }
        }
        double[] a = list.ToArray();
        Array.Sort(a);
        return a;
    }

    private static bool HasNaN(IEnumerable<double> values)
    {
        foreach (double v in values)
        {
            if (double.IsNaN(v))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>np.median: 가운데 값(짝수면 가운데 두 값의 np.mean). 빈 배열·NaN 이 있으면 NaN.</summary>
    public static double Median(IEnumerable<double> values)
    {
        if (HasNaN(values))
        {
            return double.NaN;
        }
        double[] a = SortedOrNull(values);
        int n = a.Length;
        if (n == 0)
        {
            return double.NaN;
        }
        int mid = n / 2;
        return n % 2 == 1 ? NpReduce.Mean(a.AsSpan(mid, 1)) : NpReduce.Mean(a.AsSpan(mid - 1, 2));
    }

    /// <summary>np.percentile(values, p) (method="linear"). 빈 배열이면 예외, NaN 이 있으면 NaN.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        if (HasNaN(values))
        {
            return double.NaN;
        }
        double[] a = SortedOrNull(values);
        int n = a.Length;
        if (n == 0)
        {
            throw new ArgumentException("빈 배열의 백분위수는 정의되지 않습니다");
        }
        double q = p / 100.0;
        double vi = (n - 1) * q;
        double prev = Math.Floor(vi);
        int ip = (int)prev;
        int inx = ip + 1;
        if (vi >= n - 1)
        {
            ip = inx = n - 1;
            prev = -1;
        }
        if (vi < 0)
        {
            ip = inx = 0;
            prev = 0;
        }
        double gamma = vi - prev;
        double lo = a[ip];
        double hi = a[inx];
        double diff = hi - lo;
        return gamma >= 0.5 ? hi - (diff * (1 - gamma)) : lo + (diff * gamma);
    }

    /// <summary>a[mask].sum() (numpy pairwise 합).</summary>
    public static double SumWhere(double[] a, bool[] mask)
    {
        var sel = new List<double>();
        for (int i = 0; i < a.Length; i++)
        {
            if (mask[i])
            {
                sel.Add(a[i]);
            }
        }
        return NpReduce.Sum(sel.ToArray());
    }

    /// <summary>a[mask] 를 모읍니다.</summary>
    public static double[] Select(double[] a, bool[] mask)
    {
        var sel = new List<double>();
        for (int i = 0; i < a.Length; i++)
        {
            if (mask[i])
            {
                sel.Add(a[i]);
            }
        }
        return sel.ToArray();
    }
}
