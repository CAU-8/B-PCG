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
        Array.Sort(_idx, start, end - start, Comparer<int>.Create((x, y) =>
        {
            int c = _pts[(x * _dim) + axis].CompareTo(_pts[(y * _dim) + axis]);
            return c != 0 ? c : x.CompareTo(y);
        }));
        double split = _pts[(_idx[mid] * _dim) + axis];
        int left = Build(start, mid);
        int right = Build(mid, end);
        _nodes[id] = new Node(start, end, axis, split, left, right);
        return id;
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
                if (d2 > ub2)
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
