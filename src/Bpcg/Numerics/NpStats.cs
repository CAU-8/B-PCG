using System;
using System.Collections.Generic;

namespace Bpcg.Numerics;

/// <summary>numpy 통계 함수 (np.median, np.percentile linear, 마스크 합).</summary>
public static class NpStats
{
    /// <summary>np.median: 가운데 값(짝수면 가운데 두 값의 np.mean). 빈 배열·NaN 이 있으면 NaN.</summary>
    public static double Median(IEnumerable<double> values)
    {
        double[]? a = MaterializeOrNull(values);
        if (a is null)
        {
            return double.NaN;
        }
        int n = a.Length;
        if (n == 0)
        {
            return double.NaN;
        }
        int mid = n / 2;
        if (n % 2 == 1)
        {
            double one = OrderStatistic(a, mid, mid).Lo;
            return NpReduce.Mean([one]);
        }
        (double lo, double hi) = OrderStatistic(a, mid - 1, mid);
        return NpReduce.Mean([lo, hi]);
    }

    /// <summary>np.percentile(values, p) (method="linear"). 빈 배열이면 예외, NaN 이 있으면 NaN.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        double[]? a = MaterializeOrNull(values);
        if (a is null)
        {
            return double.NaN;
        }
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
        (double lo, double hi) = OrderStatistic(a, ip, inx);
        double diff = hi - lo;
        return gamma >= 0.5 ? hi - (diff * (1 - gamma)) : lo + (diff * gamma);
    }

    // 값을 한 번만 읽어 배열로 만듭니다. NaN 이 있으면 null.
    private static double[]? MaterializeOrNull(IEnumerable<double> values)
    {
        double[] a = values is double[] arr ? (double[])arr.Clone() : [.. values];
        foreach (double v in a)
        {
            if (double.IsNaN(v))
            {
                return null;
            }
        }
        return a;
    }

    // 오름차순 정렬했을 때 i 번째와 j(= i 또는 i+1) 번째 값. 정렬 대신 고르기(quickselect)로 찾습니다.
    // −0 과 +0 이 함께 있으면 정렬 결과에서 어느 쪽이 그 자리에 오는지가 정렬 방식에 달려 있으므로, 그때만 Array.Sort 로
    // 정렬해 예전과 같은 비트를 돌려줍니다. 그 밖의 같은 값은 비트도 같습니다. a 는 바꿉니다.
    private static (double Lo, double Hi) OrderStatistic(double[] a, int i, int j)
    {
        bool negZero = false;
        bool posZero = false;
        foreach (double v in a)
        {
            if (v == 0.0)
            {
                if (double.IsNegative(v))
                {
                    negZero = true;
                }
                else
                {
                    posZero = true;
                }
            }
        }
        if (negZero && posZero)
        {
            Array.Sort(a);
            return (a[i], a[j]);
        }
        double lo = Select(a, 0, a.Length - 1, i);
        if (j == i)
        {
            return (lo, lo);
        }
        // i 번째를 고른 뒤 [i+1, n) 에는 그보다 크거나 같은 값만 남으므로, 그 구간의 가장 작은 값이 i+1 번째입니다.
        double hi = double.PositiveInfinity;
        for (int k = i + 1; k < a.Length; k++)
        {
            if (a[k] < hi)
            {
                hi = a[k];
            }
        }
        return (lo, hi);
    }

    // a[lo..hi] 에서 오름차순 k 번째 값 (Hoare 나눔, 세 값의 가운데를 기준으로). 끝나면 k 앞은 작거나 같고 뒤는 크거나 같습니다.
    private static double Select(double[] a, int lo, int hi, int k)
    {
        while (hi > lo)
        {
            int m = lo + ((hi - lo) >> 1);
            if (a[m] < a[lo])
            {
                (a[m], a[lo]) = (a[lo], a[m]);
            }
            if (a[hi] < a[lo])
            {
                (a[hi], a[lo]) = (a[lo], a[hi]);
            }
            if (a[hi] < a[m])
            {
                (a[hi], a[m]) = (a[m], a[hi]);
            }
            double pivot = a[m];
            int i = lo;
            int j = hi;
            while (i <= j)
            {
                while (a[i] < pivot)
                {
                    i++;
                }
                while (pivot < a[j])
                {
                    j--;
                }
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j)
            {
                hi = j;
            }
            else if (k >= i)
            {
                lo = i;
            }
            else
            {
                return a[k];
            }
        }
        return a[k];
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
