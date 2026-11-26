using System;
using System.Collections.Generic;
using Bpcg.Numerics;

namespace Bpcg.Core;

/// <summary>
/// 구면 위 점들을 겹치지 않는 삼각형으로 잇습니다. 어느 삼각형의 외접원 안에도 다른 점이 없게 잇는 방법(들로네 삼각분할)입니다.
/// </summary>
/// <remarks>
/// 왜 필요한가: 흔들어 둔 대표점(노드)의 값을 그 사이 자리에서 읽으려면, 자리마다 둘레의 대표점 셋을 골라야 합니다.
/// 대표점들을 겹치지 않는 삼각형으로 이어 두면, 그 자리를 담은 삼각형의 세 꼭짓점이 그 셋입니다. 들로네 방식은 가장 작은 각을
/// 가장 크게 잇는 방법이라, 가늘고 긴 삼각형이 먼 대표점끼리 섞는 일이 적습니다.
/// 하는 일: 구면 전체를 덮는 정사면체(가상 꼭짓점 넷)에서 시작합니다. 점을 하나씩 넣을 때마다, 새 점을 외접원 안에 담는
/// 삼각형들을 지우고 그 빈자리의 둘레를 새 점과 잇습니다(Bowyer–Watson 방식).
/// 판정은 모두 3D 벡터로 합니다. 면 좌표나 평면 투영을 쓰지 않으므로, 같은 점들이면 어느 자리에서 읽어도 같은 삼각형이 나옵니다.
/// - 왼쪽 판정: 점 q 가 대원 호 a → b 의 왼쪽(공 밖에서 볼 때 반시계 쪽)에 있으면 q·((a − q) × (b − q)) 가 0 보다 큽니다.
///   a 와 b 를 바꾸면 값의 부호만 정확히 바뀌고, q 가 a 나 b 와 같으면 정확히 0 입니다.
/// - 외접원 판정: 구면 위의 원은 공을 자르는 평면이 만든 원입니다. 그래서 반시계 삼각형 abc 의 외접원 안에 q 가 있으면
///   (a − q)·((b − q) × (c − q)) 가 0 보다 작습니다. q 가 그 평면의 공 바깥쪽에 있다는 뜻입니다.
/// 가상 꼭짓점은 넣는 점들의 가운데에서 70.5° 이상 떨어져 있습니다. 넣는 점들이 그보다 훨씬 좁게 모여 있으면,
/// 가운데 쪽 삼각형에는 가상 꼭짓점이 끼어들지 않습니다.
/// </remarks>
internal sealed class SphereDelaunay
{
    /// <summary>처음 구면을 덮는 정사면체의 꼭짓점 수. 0~3번 꼭짓점은 넣은 점이 아닌 가상 점입니다.</summary>
    public const int NVirtual = 4;

    private double[] _p;
    private int _nv;
    private int[] _tv;
    private int[] _tn;
    private bool[] _dead;
    private int[] _mark;
    private int _nt;
    private int _last;
    private int _stamp;
    private int[] _vertexTri = [];
    private int[] _adjStart = [];
    private int[] _adj = [];
    private readonly List<int> _cavity = [];
    private readonly Stack<int> _stack = new();
    private readonly List<(int A, int B, int Outside, int From)> _boundary = [];
    private readonly Dictionary<int, int> _startAt = [];
    private readonly Dictionary<int, int> _endAt = [];

    /// <summary>
    /// 가운데 방향 center (3,) 를 둘러싸는 정사면체로 시작합니다. 가상 꼭짓점 하나는 center 의 반대편에, 셋은 center 에서
    /// 70.5° 떨어진 자리에 둡니다. capacity 는 넣을 점 수의 짐작값입니다(넘어도 늘어납니다).
    /// </summary>
    public SphereDelaunay(ReadOnlySpan<double> center, int capacity)
    {
        int cap = Math.Max(capacity, 0) + NVirtual;
        _p = new double[cap * 3];
        _tv = new int[(cap * 2 + 8) * 3];
        _tn = new int[_tv.Length];
        _dead = new bool[_tv.Length / 3];
        _mark = new int[_tv.Length / 3];

        double cn = LinAlg.Norm3(center);
        if (!(cn > 0.0 && double.IsFinite(cn)))
        {
            throw new ArgumentException("삼각분할의 가운데 방향은 길이가 0 이 아닌 유한한 벡터여야 합니다");
        }
        double[] c = [center[0] / cn, center[1] / cn, center[2] / cn];
        // c 에 수직인 두 방향 e1, e2. c 의 성분 가운데 절댓값이 가장 작은 축과 c 의 외적에서 시작합니다.
        double[] axis = new double[3];
        axis[ArgMinAbs(c)] = 1.0;
        double[] e1 = new double[3];
        LinAlg.Cross3(axis, c, e1);
        double n1 = LinAlg.Norm3(e1);
        e1 = [e1[0] / n1, e1[1] / n1, e1[2] / n1];
        double[] e2 = new double[3];
        LinAlg.Cross3(c, e1, e2);
        // 정사면체: 꼭짓점끼리의 내적이 모두 −1/3 입니다. c 반대편 꼭짓점과 나머지 셋의 내적이 −1/3 이 되려면,
        // 나머지 셋은 c 와의 내적이 1/3(약 70.5°)인 자리에 120° 간격으로 놓입니다.
        double cosT = 1.0 / 3.0;
        double sinT = Math.Sqrt(8.0) / 3.0;
        double s3 = Math.Sqrt(3.0) / 2.0;
        (double Cos, double Sin)[] dirs = [(1.0, 0.0), (-0.5, s3), (-0.5, -s3)];
        Span<double> v = stackalloc double[3];
        foreach ((double ca, double sa) in dirs)
        {
            for (int k = 0; k < 3; k++)
            {
                v[k] = (cosT * c[k]) + (sinT * ((ca * e1[k]) + (sa * e2[k])));
            }
            AddPoint(v);
        }
        v[0] = -c[0];
        v[1] = -c[1];
        v[2] = -c[2];
        AddPoint(v);

        int[][] faces = [[0, 1, 2], [0, 1, 3], [0, 2, 3], [1, 2, 3]];
        foreach (int[] f in faces)
        {
            int a = f[0], b = f[1], d = f[2];
            if (Det3(a, b, d) < 0.0)
            {
                (b, d) = (d, b);
            }
            NewTriangle(a, b, d);
        }
        // 네 면의 이웃: k 번 꼭짓점 맞은편 변을 함께 가진 다른 면
        for (int t = 0; t < 4; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = _tv[(t * 3) + ((k + 1) % 3)];
                int b = _tv[(t * 3) + ((k + 2) % 3)];
                for (int u = 0; u < 4; u++)
                {
                    if (u != t && HasVertex(u, a) && HasVertex(u, b))
                    {
                        _tn[(t * 3) + k] = u;
                    }
                }
            }
        }
        _last = 0;
    }

    /// <summary>꼭짓점 수(가상 꼭짓점 넷 포함).</summary>
    public int VertexCount => _nv;

    /// <summary>
    /// 점 q (3,) 를 넣고 그 꼭짓점 번호를 돌려줍니다. 같은 자리에 이미 점이 있거나 빈자리가 새 점에서 다 보이지 않으면
    /// InvalidOperationException 을 냅니다.
    /// </summary>
    public int Insert(ReadOnlySpan<double> q)
    {
        if (_vertexTri.Length > 0)
        {
            throw new InvalidOperationException("Finish 를 부른 뒤에는 점을 더 넣을 수 없습니다");
        }
        int v = AddPoint(q);
        q = _p.AsSpan(v * 3, 3);
        (int t, _, _, _) = Locate(q, _last);

        // 1. 빈자리: 새 점을 외접원 안에 담는 삼각형들. 새 점을 담은 삼각형에서 시작해 이웃으로 넓힙니다.
        _stamp++;
        _cavity.Clear();
        _mark[t] = _stamp;
        _stack.Push(t);
        while (_stack.Count > 0)
        {
            int s = _stack.Pop();
            _cavity.Add(s);
            for (int k = 0; k < 3; k++)
            {
                int u = _tn[(s * 3) + k];
                if (_mark[u] != _stamp && InCircumcircle(u, q))
                {
                    _mark[u] = _stamp;
                    _stack.Push(u);
                }
            }
        }

        // 2. 빈자리의 둘레 변. 새 점이 모든 둘레 변의 왼쪽에 있어야 새 삼각형이 뒤집히지 않습니다.
        _boundary.Clear();
        foreach (int s in _cavity)
        {
            for (int k = 0; k < 3; k++)
            {
                int u = _tn[(s * 3) + k];
                if (_mark[u] == _stamp)
                {
                    continue;
                }
                int a = _tv[(s * 3) + ((k + 1) % 3)];
                int b = _tv[(s * 3) + ((k + 2) % 3)];
                if (!(Side(a, b, q) > 0.0))
                {
                    throw new InvalidOperationException(
                        $"들로네 삼각분할에 점 {v - NVirtual} 번을 넣지 못했습니다: 지울 삼각형들의 둘레 변 하나가 새 점에서 보이지 않습니다. "
                        + "같은 자리에 대표점이 둘 있는지 확인하세요");
                }
                _boundary.Add((a, b, u, s));
            }
        }

        // 3. 둘레 변마다 새 점과 잇는 삼각형 (a, b, v) 를 만들고 이웃을 다시 겁니다.
        _startAt.Clear();
        _endAt.Clear();
        foreach ((int a, int b, int outside, int from) in _boundary)
        {
            int t2 = NewTriangle(a, b, v);
            _tn[(t2 * 3) + 2] = outside;
            for (int k = 0; k < 3; k++)
            {
                if (_tn[(outside * 3) + k] == from)
                {
                    _tn[(outside * 3) + k] = t2;
                }
            }
            _startAt[a] = t2;
            _endAt[b] = t2;
        }
        foreach ((int a, int b, _, _) in _boundary)
        {
            int t2 = _startAt[a];
            _tn[t2 * 3] = _startAt[b];
            _tn[(t2 * 3) + 1] = _endAt[a];
        }
        foreach (int s in _cavity)
        {
            _dead[s] = true;
        }
        _last = _nt - 1;
        return v;
    }

    /// <summary>점을 다 넣은 뒤 부릅니다. 꼭짓점마다 닿은 삼각형 하나와 이웃 꼭짓점 목록(번호 순)을 만듭니다.</summary>
    public void Finish()
    {
        int[] vertexTri = new int[_nv];
        Array.Fill(vertexTri, -1);
        var adj = new List<int>[_nv];
        for (int i = 0; i < _nv; i++)
        {
            adj[i] = [];
        }
        for (int t = 0; t < _nt; t++)
        {
            if (_dead[t])
            {
                continue;
            }
            for (int k = 0; k < 3; k++)
            {
                int a = _tv[(t * 3) + k];
                if (vertexTri[a] < 0)
                {
                    vertexTri[a] = t;
                }
                // 반시계 삼각형 (a, b, c) 의 a → b 변: 꼭짓점 a 둘레의 삼각형마다 다음 꼭짓점이 한 번씩 나옵니다.
                adj[a].Add(_tv[(t * 3) + ((k + 1) % 3)]);
            }
        }
        _adjStart = new int[_nv + 1];
        for (int i = 0; i < _nv; i++)
        {
            adj[i].Sort();
            _adjStart[i + 1] = _adjStart[i] + adj[i].Count;
        }
        _adj = new int[_adjStart[_nv]];
        for (int i = 0; i < _nv; i++)
        {
            adj[i].CopyTo(_adj, _adjStart[i]);
        }
        _vertexTri = vertexTri;
    }

    /// <summary>
    /// 꼭짓점 start 에 닿은 삼각형에서 걸어가 점 q (3,) 를 담은 삼각형 T 를 찾습니다. L0·L1·L2 는 T 의 0·1·2번 꼭짓점의
    /// 무게중심 좌표에 비례하는 값(모두 0 이상, 합으로 나누면 무게)입니다. Finish 뒤에 부르며, 여러 스레드에서 함께 불러도 됩니다.
    /// </summary>
    public (int T, double L0, double L1, double L2) LocateFrom(int start, ReadOnlySpan<double> q)
    {
        if (_vertexTri.Length == 0)
        {
            throw new InvalidOperationException("Finish 를 부르기 전에는 점을 찾을 수 없습니다");
        }
        return Locate(q, _vertexTri[start]);
    }

    /// <summary>삼각형 t 의 k 번 꼭짓점 (k = 0, 1, 2, 공 밖에서 볼 때 반시계 순서).</summary>
    public int Vertex(int t, int k) => _tv[(t * 3) + k];

    /// <summary>꼭짓점 v 의 단위 벡터 (3,).</summary>
    public ReadOnlySpan<double> Point(int v) => _p.AsSpan(v * 3, 3);

    /// <summary>
    /// 꼭짓점 start 에서 시작해 점 q 에 더 가까운 이웃으로 옮겨 가며, q 에 가장 가까운 꼭짓점을 찾습니다(가상 꼭짓점은 건너뜀).
    /// 들로네 삼각분할에서는 가장 가까운 점이 아닌 꼭짓점에 늘 더 가까운 이웃이 있어서, 멈춘 곳이 가장 가까운 점입니다.
    /// 거리가 같으면 번호가 작은 꼭짓점을 고릅니다. Finish 뒤에 부르며, 여러 스레드에서 함께 불러도 됩니다.
    /// </summary>
    public int Nearest(ReadOnlySpan<double> q, int start)
    {
        if (_vertexTri.Length == 0)
        {
            throw new InvalidOperationException("Finish 를 부르기 전에는 점을 찾을 수 없습니다");
        }
        int best = start;
        double bestDot = Dot(start, q);
        while (true)
        {
            int next = best;
            double nextDot = bestDot;
            for (int i = _adjStart[best]; i < _adjStart[best + 1]; i++)
            {
                int w = _adj[i];
                if (w < NVirtual)
                {
                    continue;
                }
                double d = Dot(w, q);
                if (d > nextDot || (d == nextDot && w < next))
                {
                    next = w;
                    nextDot = d;
                }
            }
            if (next == best)
            {
                return best;
            }
            best = next;
            bestDot = nextDot;
        }
    }

    /// <summary>꼭짓점 v 와 점 q 의 내적. 클수록 가깝습니다.</summary>
    public double Dot(int v, ReadOnlySpan<double> q) =>
        ((_p[v * 3] * q[0]) + (_p[(v * 3) + 1] * q[1])) + (_p[(v * 3) + 2] * q[2]);

    private (int T, double L0, double L1, double L2) Locate(ReadOnlySpan<double> q, int start)
    {
        // 가장 크게 바깥쪽에 있는 변을 건너 걸어갑니다. 들로네 삼각분할에서는 이 걸음이 되돌아 돌지 않습니다.
        int t = start;
        int limit = (4 * _nt) + 16;
        for (int step = 0; step < limit; step++)
        {
            int a = _tv[t * 3];
            int b = _tv[(t * 3) + 1];
            int c = _tv[(t * 3) + 2];
            double l0 = Side(b, c, q);
            double l1 = Side(c, a, q);
            double l2 = Side(a, b, q);
            int k = -1;
            double worst = 0.0;
            if (l0 < worst)
            {
                worst = l0;
                k = 0;
            }
            if (l1 < worst)
            {
                worst = l1;
                k = 1;
            }
            if (l2 < worst)
            {
                k = 2;
            }
            if (k < 0)
            {
                return (t, l0, l1, l2);
            }
            t = _tn[(t * 3) + k];
        }
        throw new InvalidOperationException($"들로네 삼각분할에서 점을 담은 삼각형을 {limit}걸음 안에 찾지 못했습니다");
    }

    // q·((a − q) × (b − q)): q 가 대원 호 a → b 의 왼쪽이면 양수. a, b 를 바꾸면 부호만 정확히 바뀝니다.
    private double Side(int a, int b, ReadOnlySpan<double> q)
    {
        double q0 = q[0];
        double q1 = q[1];
        double q2 = q[2];
        double ax = _p[a * 3] - q0;
        double ay = _p[(a * 3) + 1] - q1;
        double az = _p[(a * 3) + 2] - q2;
        double bx = _p[b * 3] - q0;
        double by = _p[(b * 3) + 1] - q1;
        double bz = _p[(b * 3) + 2] - q2;
        return ((q0 * ((ay * bz) - (az * by))) + (q1 * ((az * bx) - (ax * bz)))) + (q2 * ((ax * by) - (ay * bx)));
    }

    // (a − q)·((b − q) × (c − q)) < 0 이면 q 가 반시계 삼각형 abc 의 외접원 안에 있습니다.
    private bool InCircumcircle(int t, ReadOnlySpan<double> q)
    {
        int a = _tv[t * 3];
        int b = _tv[(t * 3) + 1];
        int c = _tv[(t * 3) + 2];
        double ax = _p[a * 3] - q[0];
        double ay = _p[(a * 3) + 1] - q[1];
        double az = _p[(a * 3) + 2] - q[2];
        double bx = _p[b * 3] - q[0];
        double by = _p[(b * 3) + 1] - q[1];
        double bz = _p[(b * 3) + 2] - q[2];
        double cx = _p[c * 3] - q[0];
        double cy = _p[(c * 3) + 1] - q[1];
        double cz = _p[(c * 3) + 2] - q[2];
        double det = ((ax * ((by * cz) - (bz * cy))) + (ay * ((bz * cx) - (bx * cz)))) + (az * ((bx * cy) - (by * cx)));
        return det < 0.0;
    }

    private double Det3(int a, int b, int c)
    {
        Span<double> bc = stackalloc double[3];
        LinAlg.Cross3(_p.AsSpan(b * 3, 3), _p.AsSpan(c * 3, 3), bc);
        return LinAlg.Dot3Numba(_p.AsSpan(a * 3, 3), bc);
    }

    private bool HasVertex(int t, int v) => _tv[t * 3] == v || _tv[(t * 3) + 1] == v || _tv[(t * 3) + 2] == v;

    private int AddPoint(ReadOnlySpan<double> q)
    {
        if ((_nv + 1) * 3 > _p.Length)
        {
            Array.Resize(ref _p, Math.Max(_p.Length * 2, 12));
        }
        q[..3].CopyTo(_p.AsSpan(_nv * 3, 3));
        return _nv++;
    }

    private int NewTriangle(int a, int b, int c)
    {
        if ((_nt + 1) * 3 > _tv.Length)
        {
            int size = Math.Max(_tv.Length * 2, 24);
            Array.Resize(ref _tv, size);
            Array.Resize(ref _tn, size);
            Array.Resize(ref _dead, size / 3);
            Array.Resize(ref _mark, size / 3);
        }
        int t = _nt++;
        _tv[t * 3] = a;
        _tv[(t * 3) + 1] = b;
        _tv[(t * 3) + 2] = c;
        _tn[t * 3] = -1;
        _tn[(t * 3) + 1] = -1;
        _tn[(t * 3) + 2] = -1;
        _dead[t] = false;
        _mark[t] = 0;
        return t;
    }

    private static int ArgMinAbs(double[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (Math.Abs(v[i]) < Math.Abs(v[best]))
            {
                best = i;
            }
        }
        return best;
    }
}
