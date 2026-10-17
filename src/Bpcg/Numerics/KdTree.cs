using System;
using System.Collections.Generic;

namespace Bpcg.Numerics;

/// <summary>
/// scipy.spatial.cKDTree 대체 (유클리드 거리, 점은 (N, dim) 행 우선).
/// </summary>
/// <remarks>
/// 반경 검색은 제곱 거리 ≤ r² 인 점을 돌려줍니다. 가장 가까운 점이 여럿이면 번호가 작은 점을 고릅니다.
/// TODO(port): cKDTree 의 동률 처리와 경계(거리 = r) 판정은 대조하지 않았습니다.
/// </remarks>
public sealed class KdTree
{
    private const int LeafSize = 16;
    private readonly double[] _pts;
    private readonly int _dim;
    private readonly int[] _idx;
    private readonly List<Node> _nodes = [];

    private readonly record struct Node(int Start, int End, int Axis, double Split, int Left, int Right);

    public KdTree(double[] points, int dim = 3)
    {
        if (dim < 1 || points.Length % dim != 0)
        {
            throw new ArgumentException($"points 는 (N, {dim}) 이어야 합니다");
        }
        _pts = points;
        _dim = dim;
        int n = points.Length / dim;
        _idx = new int[n];
        for (int i = 0; i < n; i++)
        {
            _idx[i] = i;
        }
        if (n > 0)
        {
            Build(0, n);
        }
    }

    public int Count => _idx.Length;

    private int Build(int start, int end)
    {
        int id = _nodes.Count;
        _nodes.Add(default);
        if (end - start <= LeafSize)
        {
            _nodes[id] = new Node(start, end, -1, 0.0, -1, -1);
            return id;
        }
        // 가장 넓게 퍼진 축에서 가운데 값으로 나눕니다.
        int axis = 0;
        double best = -1.0;
        for (int a = 0; a < _dim; a++)
        {
            double lo = double.PositiveInfinity;
            double hi = double.NegativeInfinity;
            for (int k = start; k < end; k++)
            {
                double v = _pts[(_idx[k] * _dim) + a];
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            if (hi - lo > best)
            {
                best = hi - lo;
                axis = a;
            }
        }
        int mid = (start + end) / 2;
        // (축 값, 번호) 순서로 mid 번째만 제자리에 놓고 그 앞은 작은 쪽, 뒤는 큰 쪽으로 나눕니다. 구간 전체를 정렬하던 것과
        // 가운데 원소·양쪽 집합이 같아 나무 모양이 같고, 검색 결과는 번호·거리 순으로 정하므로 잎 안의 순서와 상관없습니다.
        SelectNth(start, end, mid, axis);
        double split = _pts[(_idx[mid] * _dim) + axis];
        int left = Build(start, mid);
        int right = Build(mid, end);
        _nodes[id] = new Node(start, end, axis, split, left, right);
        return id;
    }

    // (축 값, 번호) 순서: 값은 double.CompareTo 로 비교하고(정렬하던 때와 같은 규칙), 같으면 번호가 작은 쪽이 앞.
    private bool Before(int x, int y, int axis)
    {
        int c = _pts[(x * _dim) + axis].CompareTo(_pts[(y * _dim) + axis]);
        return c < 0 || (c == 0 && x < y);
    }

    // _idx[start..end) 에서 (축 값, 번호) 순서로 k 번째를 제자리에 놓습니다 (quickselect, 세 값의 가운데를 기준으로).
    // 끝나면 [start, k) 는 k 번째보다 앞, (k, end) 는 뒤입니다.
    private void SelectNth(int start, int end, int k, int axis)
    {
        int lo = start;
        int hi = end - 1;
        while (hi > lo)
        {
            int m = lo + ((hi - lo) >> 1);
            if (Before(_idx[m], _idx[lo], axis))
            {
                (_idx[m], _idx[lo]) = (_idx[lo], _idx[m]);
            }
            if (Before(_idx[hi], _idx[lo], axis))
            {
                (_idx[hi], _idx[lo]) = (_idx[lo], _idx[hi]);
            }
            if (Before(_idx[hi], _idx[m], axis))
            {
                (_idx[hi], _idx[m]) = (_idx[m], _idx[hi]);
            }
            int pivot = _idx[m];
            int i = lo;
            int j = hi;
            while (i <= j)
            {
                while (Before(_idx[i], pivot, axis))
                {
                    i++;
                }
                while (Before(pivot, _idx[j], axis))
                {
                    j--;
                }
                if (i <= j)
                {
                    (_idx[i], _idx[j]) = (_idx[j], _idx[i]);
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
                return;
            }
        }
    }

    private double Dist2(int i, ReadOnlySpan<double> q)
    {
        double s = 0.0;
        for (int a = 0; a < _dim; a++)
        {
            double d = _pts[(i * _dim) + a] - q[a];
            s += d * d;
        }
        return s;
    }

    /// <summary>q 에서 거리 r 안의 점 번호 (query_ball_point). 번호 오름차순으로 돌려줍니다.</summary>
    public List<int> QueryBallPoint(ReadOnlySpan<double> q, double r)
    {
        var output = new List<int>();
        if (_idx.Length == 0)
        {
            return output;
        }
        double r2 = r * r;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            Node nd = _nodes[stack.Pop()];
            if (nd.Axis < 0)
            {
                for (int k = nd.Start; k < nd.End; k++)
                {
                    if (Dist2(_idx[k], q) <= r2)
                    {
                        output.Add(_idx[k]);
                    }
                }
                continue;
            }
            double diff = q[nd.Axis] - nd.Split;
            if (diff - r <= 0.0)
            {
                stack.Push(nd.Left);
            }
            if (diff + r >= 0.0)
            {
                stack.Push(nd.Right);
            }
        }
        output.Sort();
        return output;
    }

    /// <summary>q 에서 거리 r 안의 점 수 (query_ball_point(return_length=True)).</summary>
    public int CountBallPoint(ReadOnlySpan<double> q, double r) => QueryBallPoint(q, r).Count;

    /// <summary>
    /// 가장 가까운 k 개 점 (query). upperBound 보다 먼 자리는 거리 inf, 번호 Count (scipy 와 같음).
    /// 결과는 가까운 순.
    /// </summary>
    public (double[] Dist, int[] Index) Query(ReadOnlySpan<double> q, int k = 1, double upperBound = double.PositiveInfinity)
    {
        double[] bd = new double[k];
        int[] bi = new int[k];
        Array.Fill(bd, double.PositiveInfinity);
        Array.Fill(bi, Count);
        double ub2 = double.IsPositiveInfinity(upperBound) ? double.PositiveInfinity : upperBound * upperBound;
        if (_idx.Length > 0)
        {
            Search(0, q, k, bd, bi, ub2);
        }
        for (int j = 0; j < k; j++)
        {
            bd[j] = bi[j] == Count ? double.PositiveInfinity : Math.Sqrt(bd[j]);
        }
        return (bd, bi);
    }

    private void Search(int node, ReadOnlySpan<double> q, int k, double[] bd, int[] bi, double ub2)
    {
        Node nd = _nodes[node];
        if (nd.Axis < 0)
        {
            for (int t = nd.Start; t < nd.End; t++)
            {
                int i = _idx[t];
                double d2 = Dist2(i, q);
                if (d2 >= ub2)
                {
                    continue;
                }
                // 삽입 정렬: 거리, 같으면 번호 순
                int pos = k;
                while (pos > 0 && (d2 < bd[pos - 1] || (d2 == bd[pos - 1] && i < bi[pos - 1])))
                {
                    pos--;
                }
                if (pos < k)
                {
                    for (int s = k - 1; s > pos; s--)
                    {
                        bd[s] = bd[s - 1];
                        bi[s] = bi[s - 1];
                    }
                    bd[pos] = d2;
                    bi[pos] = i;
                }
            }
            return;
        }
        double diff = q[nd.Axis] - nd.Split;
        int first = diff <= 0.0 ? nd.Left : nd.Right;
        int second = diff <= 0.0 ? nd.Right : nd.Left;
        Search(first, q, k, bd, bi, ub2);
        double worst = Math.Min(bd[k - 1], ub2);
        if (diff * diff <= worst)
        {
            Search(second, q, k, bd, bi, ub2);
        }
    }

    /// <summary>거리 r 안의 모든 점 쌍 (i &lt; j) (query_pairs). (i, j) 사전순.</summary>
    public List<(int I, int J)> QueryPairs(double r)
    {
        var output = new List<(int, int)>();
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            foreach (int j in QueryBallPoint(_pts.AsSpan(i * _dim, _dim), r))
            {
                if (j > i)
                {
                    output.Add((i, j));
                }
            }
        }
        return output;
    }
}
