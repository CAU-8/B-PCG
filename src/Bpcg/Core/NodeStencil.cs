using System;
using System.Collections.Generic;
using Bpcg.Numerics;

namespace Bpcg.Core;

/// <summary>
/// 구면 그래프의 대표점(노드 흔들기로 옮긴 점)에 있는 값을 구면 위 다른 점에서 읽는 계수입니다. 점마다 그 점을 담은 삼각형의
/// 세 대표점과 무게, 그리고 가장 가까운 대표점을 담습니다.
/// </summary>
/// <remarks>
/// 왜 필요한가: 행성 지도(L0)의 값(융기, 깎인 두께, 지질 템플릿 등)은 칸 정중앙이 아니라 대표점에서 계산한 값입니다.
/// 대표점은 노드 흔들기(강이 격자 줄을 따라 곧게 뻗지 않도록 칸 중심을 조금씩 옮기는 일)로 칸 중심에서 가로·세로로 최대 반 칸
/// 옮겨져 있습니다. laptop 프로필에서는 약 10 km 입니다. 이 값을 칸 정중앙에 있다고 보고 읽으면(<see cref="Resample.SampleSphere(double[], int, double[])"/>)
/// 값이 최대 반 칸 어긋난 자리에 놓이고, 가장 가까운 칸의 값을 쓰는 범주 값은 경계가 칸 경계를 따라 곧게 남습니다.
/// 하는 일: 읽을 점들이 놓인 칸과 그 둘레 rings 겹의 칸에서 대표점을 모아 들로네 삼각분할로 잇습니다(<see cref="SphereDelaunay"/>).
/// 연속 값은 점을 담은 삼각형의 세 대표점 값을 무게중심 좌표로 섞습니다(<see cref="Linear"/>). 범주 값은 가장 가까운 대표점의
/// 값을 씁니다(<see cref="Nearest{T}"/>). 그래서 대표점 위에서는 그 대표점의 값을 그대로 돌려줍니다.
/// 예: 대표점 A, B, C 의 융기가 1, 2, 3 mm/yr 이면, 세 점의 한가운데에서는 2 mm/yr, A 위에서는 정확히 1 mm/yr 입니다.
/// 둘레 겹이 넉넉하면 결과는 행성의 모든 대표점으로 이은 삼각분할과 같습니다. 그래서 읽을 점을 하나만 주든 여럿 주든
/// 같은 자리의 값은 같습니다. 대표점이 한 반구보다 훨씬 좁게 모여 있어야 하므로, 둘레 겹을 넣어 모은 대표점이
/// 가운데에서 <see cref="MaxCapRadiusRad"/> 를 넘으면 그 겹은 넣지 않습니다(아주 거친 격자에서만 생김).
/// 점마다 제 출력 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
/// </remarks>
public sealed class NodeStencil
{
    /// <summary>
    /// 읽을 점이 놓인 칸 둘레에서 대표점을 모으는 겹 수. 점을 담은 들로네 삼각형의 외접원은 반지름이 칸 한두 개라,
    /// 이 원 안의 대표점을 모두 모으려면 칸 네 겹이면 되고 한 겹을 더 둡니다.
    /// </summary>
    public const int DefaultRings = 5;

    /// <summary>모은 대표점이 가운데에서 떨어질 수 있는 가장 큰 각 [rad] (약 43°). 가상 꼭짓점(70.5°)과 거리를 두기 위해서입니다.</summary>
    public const double MaxCapRadiusRad = 0.75;

    private readonly int _nCells;

    private NodeStencil(int nCells, int[] cells, double[] weights, int[] nearest, int ringsUsed, int nodeCount)
    {
        _nCells = nCells;
        Cells = cells;
        Weights = weights;
        NearestCell = nearest;
        RingsUsed = ringsUsed;
        NodeCount = nodeCount;
    }

    /// <summary>(M, 3) 행 우선. 점을 담은 들로네 삼각형의 세 대표점(칸 번호).</summary>
    public int[] Cells { get; }

    /// <summary>(M, 3) 행 우선. 세 대표점의 무게(0 이상, 합 1). 점이 대표점 위에 있으면 그 대표점의 무게가 정확히 1 입니다.</summary>
    public double[] Weights { get; }

    /// <summary>(M,) 점에 가장 가까운(대원 거리) 대표점의 칸 번호. 거리가 같으면 번호가 작은 칸.</summary>
    public int[] NearestCell { get; }

    /// <summary>실제로 넣은 둘레 겹 수(<see cref="MaxCapRadiusRad"/> 때문에 요청보다 적을 수 있음).</summary>
    public int RingsUsed { get; }

    /// <summary>삼각분할에 넣은 대표점 수.</summary>
    public int NodeCount { get; }

    /// <summary>읽을 점 수 M.</summary>
    public int Count => NearestCell.Length;

    /// <summary>
    /// 구면 그래프 graph 의 대표점(graph.Pos)으로 점 unit (M, 3) 을 읽는 계수를 만듭니다. unit 은 단위 벡터이고,
    /// 한 반구보다 훨씬 좁은 곳(히어로 유역 같은 곳)에 모여 있어야 합니다. rings 는 둘레에서 대표점을 모으는 칸 겹 수입니다.
    /// </summary>
    public static NodeStencil Build(CellGraph graph, double[] unit, int rings = DefaultRings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Kind != "sphere")
        {
            throw new ArgumentException($"대표점 보간은 구면 그래프(kind 'sphere')에서만 합니다. 받은 그래프: '{graph.Kind}'");
        }
        if (rings < 1)
        {
            throw new ArgumentException($"둘레 겹 수 rings 는 1 이상이어야 합니다: {rings}");
        }
        Resample.CheckUnit(unit);
        int m = unit.Length / 3;
        int nCells = graph.NCells;
        if (m == 0)
        {
            return new NodeStencil(nCells, [], [], [], 0, 0);
        }
        long[] home = Cubesphere.CellOf(unit, (int)graph.Shape[1]);

        // 1. 읽을 점이 놓인 칸, 그다음 이웃 칸을 한 겹씩 더합니다.
        bool[] inSet = new bool[nCells];
        var members = new List<int>();
        foreach (long c in home)
        {
            if (!inSet[c])
            {
                inSet[c] = true;
                members.Add((int)c);
            }
        }
        (double[] center, double radius) = Cap(graph, members);
        if (radius > MaxCapRadiusRad)
        {
            throw new ArgumentException(
                $"읽을 점들이 너무 넓게 퍼져 있습니다: 점이 놓인 칸의 대표점이 가운데에서 {radius:F2} rad 까지 떨어져 있습니다. "
                + $"대표점 보간은 가운데에서 {MaxCapRadiusRad} rad (약 43°) 안에 모인 점에만 씁니다. 점을 지역별로 나눠 읽으세요");
        }
        int ringsUsed = 0;
        int ringStart = 0;
        for (int r = 1; r <= rings; r++)
        {
            int before = members.Count;
            for (int i = ringStart; i < before; i++)
            {
                int c = members[i];
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int nb = graph.Nbr[(c * CellGraph.NSlots) + s];
                    if (nb >= 0 && !inSet[nb])
                    {
                        inSet[nb] = true;
                        members.Add(nb);
                    }
                }
            }
            (double[] c2, double r2) = Cap(graph, members);
            if (r2 > MaxCapRadiusRad)
            {
                for (int i = before; i < members.Count; i++)
                {
                    inSet[members[i]] = false;
                }
                members.RemoveRange(before, members.Count - before);
                break;
            }
            center = c2;
            ringStart = before;
            ringsUsed = r;
        }

        // 2. 모은 대표점을 칸 번호 순으로 넣어 삼각분할을 만듭니다.
        int[] nodes = members.ToArray();
        Array.Sort(nodes);
        var localOf = new Dictionary<int, int>(nodes.Length);
        var dt = new SphereDelaunay(center, nodes.Length);
        Span<double> u = stackalloc double[3];
        for (int i = 0; i < nodes.Length; i++)
        {
            localOf[nodes[i]] = i;
            UnitOf(graph, nodes[i], u);
            dt.Insert(u);
        }
        dt.Finish();

        // 3. 점마다 담은 삼각형과 가장 가까운 대표점을 찾습니다. 걷기는 점이 놓인 칸의 대표점에서 시작합니다.
        int nv = SphereDelaunay.NVirtual;
        int[] cells = new int[m * 3];
        double[] weights = new double[m * 3];
        int[] nearest = new int[m];
        bool[] outside = new bool[m];
        Parallelism.For(0, m, k =>
        {
            ReadOnlySpan<double> q = unit.AsSpan(k * 3, 3);
            int start = nv + localOf[(int)home[k]];
            (int t, double l0, double l1, double l2) = dt.LocateFrom(start, q);
            int a = dt.Vertex(t, 0);
            int b = dt.Vertex(t, 1);
            int c = dt.Vertex(t, 2);
            if (a < nv || b < nv || c < nv)
            {
                outside[k] = true;
                return;
            }
            // 같은 삼각형이 어느 꼭짓점부터 저장됐든 같은 순서로 더하도록, 번호가 가장 작은 꼭짓점을 앞에 둡니다(반시계 순서는 그대로).
            if (b < a && b < c)
            {
                (a, b, c) = (b, c, a);
                (l0, l1, l2) = (l1, l2, l0);
            }
            else if (c < a && c < b)
            {
                (a, b, c) = (c, a, b);
                (l0, l1, l2) = (l2, l0, l1);
            }
            double sum = (l0 + l1) + l2;
            cells[k * 3] = nodes[a - nv];
            cells[(k * 3) + 1] = nodes[b - nv];
            cells[(k * 3) + 2] = nodes[c - nv];
            weights[k * 3] = l0 / sum;
            weights[(k * 3) + 1] = l1 / sum;
            weights[(k * 3) + 2] = l2 / sum;
            int v0 = Closer(dt, q, Closer(dt, q, a, b), c);
            nearest[k] = nodes[dt.Nearest(q, v0) - nv];
        });
        int bad = Array.IndexOf(outside, true);
        if (bad >= 0)
        {
            throw new InvalidOperationException(
                $"점 {bad} 번이 모은 대표점들의 바깥쪽에 놓였습니다(둘레 겹 {ringsUsed}겹, 대표점 {nodes.Length}개). "
                + "격자가 너무 거칠어 둘레 겹을 충분히 넣지 못했을 수 있습니다");
        }
        return new NodeStencil(nCells, cells, weights, nearest, ringsUsed, nodes.Length);
    }

    /// <summary>
    /// 대표점 값 field (6n²,) (double[] 또는 float[]) 를 읽을 점마다 삼각형 안에서 섞습니다: w0·f[c0] + w1·f[c1] + w2·f[c2]. 반환 (M,).
    /// 대표점 위의 점은 그 대표점의 값을 그대로 받습니다. 섞는 대표점 가운데 NaN 이나 inf 가 있으면 결과도 그렇습니다.
    /// </summary>
    public double[] Linear(Array field)
    {
        ArgumentNullException.ThrowIfNull(field);
        CheckLength(field.Length);
        int[] cells = Cells;
        double[] w = Weights;
        double[] output = new double[Count];
        switch (field)
        {
            case double[] d:
                Parallelism.For(0, Count, k =>
                    output[k] = ((w[k * 3] * d[cells[k * 3]]) + (w[(k * 3) + 1] * d[cells[(k * 3) + 1]]))
                        + (w[(k * 3) + 2] * d[cells[(k * 3) + 2]]));
                break;
            case float[] f:
                Parallelism.For(0, Count, k =>
                    output[k] = ((w[k * 3] * (double)f[cells[k * 3]]) + (w[(k * 3) + 1] * (double)f[cells[(k * 3) + 1]]))
                        + (w[(k * 3) + 2] * (double)f[cells[(k * 3) + 2]]));
                break;
            default:
                throw new InvalidCastException($"대표점 값은 실수 배열(double[] 또는 float[])이어야 합니다: {field.GetType().Name}");
        }
        return output;
    }

    /// <summary>범주 값 field (6n²,) 를 읽을 점마다 가장 가까운 대표점에서 그대로 가져옵니다. 반환 (M,), 형은 그대로.</summary>
    public T[] Nearest<T>(T[] field)
    {
        ArgumentNullException.ThrowIfNull(field);
        CheckLength(field.Length);
        T[] output = new T[Count];
        for (int k = 0; k < output.Length; k++)
        {
            output[k] = field[NearestCell[k]];
        }
        return output;
    }

    private void CheckLength(int length)
    {
        if (length != _nCells)
        {
            throw new ArgumentException($"대표점 값의 길이가 그래프 칸 수 {_nCells} 와 다릅니다: {length}");
        }
    }

    // 두 꼭짓점 가운데 q 에 더 가까운 쪽 (같으면 번호가 작은 쪽). 가장 가까운 대표점 찾기를 시작할 자리를 고릅니다.
    private static int Closer(SphereDelaunay dt, ReadOnlySpan<double> q, int a, int b)
    {
        double da = dt.Dot(a, q);
        double db = dt.Dot(b, q);
        return db > da || (db == da && b < a) ? b : a;
    }

    // 칸 c 의 대표점 단위 벡터. CellGraph.Unit() 과 같은 식이라 비트까지 같습니다.
    private static void UnitOf(CellGraph graph, int c, Span<double> u)
    {
        ReadOnlySpan<double> p = graph.Pos.AsSpan(c * 3, 3);
        double norm = LinAlg.Norm3(p);
        u[0] = p[0] / norm;
        u[1] = p[1] / norm;
        u[2] = p[2] / norm;
    }

    // 칸들의 대표점이 모인 곳: 가운데 방향(단위 벡터 합의 방향)과 가운데에서 가장 먼 대표점까지의 각 [rad].
    private static (double[] Center, double Radius) Cap(CellGraph graph, List<int> members)
    {
        Span<double> u = stackalloc double[3];
        double sx = 0.0;
        double sy = 0.0;
        double sz = 0.0;
        foreach (int c in members)
        {
            UnitOf(graph, c, u);
            sx += u[0];
            sy += u[1];
            sz += u[2];
        }
        double norm = Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
        if (!(norm > 0.0))
        {
            return ([0.0, 0.0, 1.0], Math.PI);
        }
        double[] center = [sx / norm, sy / norm, sz / norm];
        double minDot = 1.0;
        foreach (int c in members)
        {
            UnitOf(graph, c, u);
            minDot = Math.Min(minDot, ((u[0] * center[0]) + (u[1] * center[1])) + (u[2] * center[2]));
        }
        return (center, Math.Acos(Math.Clamp(minDot, -1.0, 1.0)));
    }
}
