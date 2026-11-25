using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Hydro;

/// <summary>호수 하나가 어떻게 끝났는지 (LakeBudget.State).</summary>
public enum LakeState : byte
{
    /// <summary>물이 남아 수면이 턱까지 차고, 남는 물을 턱 너머로 보냅니다 (넘치는 호수).</summary>
    Open = 0,

    /// <summary>들어오는 물이 모자라 수면이 턱 아래에서 멈춥니다 (넘치지 못하는 호수).</summary>
    Closed = 1,

    /// <summary>물이 가장 낮은 칸 하나도 잠그지 못하거나, 가장 깊은 곳의 물 깊이가 minDepthM 에 못 미칩니다 (마른 분지).</summary>
    Dry = 2,
}

/// <summary>호수 하나(합친 뒤)의 물수지. 들어온 물 = 증발 + 넘친 물 이 맞습니다.</summary>
/// <param name="Pit">이 호수가 시작한 웅덩이 바닥 칸 (호수를 합쳤으면 번호가 가장 작은 바닥).</param>
/// <param name="Cells">물 깊이가 PondedM 보다 깊은 칸 수. 마른 분지는 0.</param>
/// <param name="Level">수면 [m]. 마른 분지는 물길이 끝나는 칸의 바닥 높이.</param>
/// <param name="InflowM3PerYr">들어온 물 [m³/yr]: 이 호수로 흘러드는 땅의 유출과, 다른 호수가 넘겨 준 물.</param>
/// <param name="EvaporationM3PerYr">잠긴 땅이 물에 잠기지 않았을 때보다 더 증발하는 물 [m³/yr]. 마른 분지는 들어온 물을 모두 바닥에서 잃습니다.</param>
/// <param name="OverflowM3PerYr">턱을 넘어 나간 물 [m³/yr]. 넘치지 못하는 호수와 마른 분지는 0.</param>
/// <param name="Target">넘친 물을 받은 호수의 번호 (LakeBalanceResult.Lakes 의 자리). 바다나 출구면 −1, 넘치지 않으면 −2.</param>
/// <param name="State">넘치는 호수, 넘치지 못하는 호수, 마른 분지.</param>
public sealed record LakeBudget(
    int Pit, int Cells, double Level, double InflowM3PerYr, double EvaporationM3PerYr, double OverflowM3PerYr, int Target, LakeState State);

/// <summary>호수 물수지의 결과 (LakeBalance.Solve).</summary>
/// <param name="Level">(N,) 넘치지 못하는 호수의 수면 [m]. 그런 호수 칸이 아니면 NaN.</param>
/// <param name="Closed">(N,) 넘치지 못하는 호수 칸 (닫힌 호수).</param>
/// <param name="Sink">(N,) 물이 더 흘러가지 않고 끝나는 칸: 닫힌 호수 칸과, 마른 분지의 가장 낮은 칸.</param>
/// <param name="NOpen">넘쳐서 흘러 나가는 호수 수.</param>
/// <param name="NClosed">넘치지 못하는 호수 수.</param>
/// <param name="NDry">마른 분지 수 (NShallow 포함).</param>
/// <param name="NShallow">넘치지 못하는데 가장 깊은 곳의 물 깊이가 minDepthM 에 못 미쳐 마른 분지로 센 호수 수.</param>
/// <param name="NPits">처음에 호수를 하나씩 둔 웅덩이 바닥 수.</param>
/// <param name="NSpills">호수가 차서 남는 물을 턱 너머로 넘긴 횟수.</param>
/// <param name="NMerges">두 호수의 수면이 턱에서 만나 하나로 합친 횟수.</param>
/// <param name="Lakes">호수마다의 물수지 (합친 뒤의 호수 하나에 한 줄, 웅덩이 바닥 번호 순).</param>
public sealed record LakeBalanceResult(
    double[] Level, bool[] Closed, bool[] Sink, int NOpen, int NClosed, int NDry, int NShallow, int NPits, int NSpills, int NMerges,
    IReadOnlyList<LakeBudget> Lakes);

/// <summary>
/// 호수 물수지 (docs/pipeline.md 8.1). 우묵한 자리마다 물이 넘칠 때까지 차는지, 차다가 마는지를 정합니다.
/// </summary>
/// <remarks>
/// 왜 필요한가: 우묵한 자리를 넘칠 높이까지 채우기만 하면 사막 한가운데에도 물이 가득 찬 호수가 생깁니다. 지구의 마른 땅에서는
/// 들어오는 강물보다 수면에서 증발하는 물이 많아, 호수가 넘치지 못하고 강이 거기서 끝납니다 (사해, 그레이트솔트호).
/// 규칙: 호수 수면은 한 해에 잠재 증발산 PET 만큼 증발하고, 같은 땅이 물에 잠기지 않았다면 실제 증발산 AET = P − R
/// (강수 − 유출)만큼만 증발합니다. 그래서 땅 한 칸이 물에 잠길 때마다 (PET − AET) × 칸 넓이만큼 물이 더 듭니다.
/// 닫힌 분지 호수의 넓이를 정하는 물수지 식과 같습니다 (Huybers, Rupper &amp; Roe 2015 의 식 1·4; Garcia-Castellanos 2006 의 식 2):
/// 호수 넓이 × (증발 − 강수) = 나머지 유역 넓이 × 유출.
/// 예: 한 해 유출 0.05 m 인 유역 10,000 km² 에서 5억 m³ 가 들어오고, 호수 수면이 땅보다 한 해 1.0 m 더 증발하면
/// 호수는 500 km² 에서 멈춥니다.
/// 푸는 법 (채우고 넘치고 합치기, Barnes, Callaghan &amp; Wickert 2021 의 Fill–Spill–Merge 에 증발을 넣음):
/// 1. 웅덩이 바닥(둘레에 더 낮은 칸이 없는 칸)마다 호수를 하나 둡니다. 땅의 유출은 물길을 따라 바닥 하나로 모입니다.
/// 2. 호수마다 낮은 칸부터 잠급니다. 칸 하나를 잠글 때마다 그 칸의 증발만큼 물을 씁니다. 잠근 칸의 이웃은 '그 칸 높이와 지금 수면
///    가운데 높은 쪽' 순서로 기다립니다. 그래서 호수는 늘 한 덩어리로 자랍니다.
/// 3. 물이 모자라면 수면이 거기서 멈춥니다 (넘치지 못하는 호수).
/// 4. 다음 칸이 지금 수면보다 낮고 다른 바닥이나 바다로 흘러가면, 수면이 턱에 닿은 것입니다. 남은 물을 그쪽 호수로 넘깁니다 (넘치는 호수).
///    턱 높이에 걸친 칸(물 깊이 PondedM 이하)은 호수가 아니므로 그 칸에 쓴 물도 함께 넘깁니다.
/// 5. 넘친 물을 받은 호수가 자라 턱에서 넘긴 호수의 수면과 만나면, 두 호수를 하나로 합쳐 함께 올립니다.
/// 그래서 호수마다 들어온 물 = 증발 + 넘친 물 이 맞고, 한 분지 안의 떨어진 바닥은 각자 자기 물로 따로 잠깁니다. 되풀이 상한은 없습니다.
/// 한계: 넘치는 호수의 증발은 생성 단계가 뒤에서 다시 모으는 하류 유량(discharge)에서 빼지 않습니다. 이 함수 안에서만 뺍니다.
/// </remarks>
public static class LakeBalance
{
    /// <summary>물이 고였다고 보는 깊이의 아래 한계 [m] (Water.LakeMinDepthM 과 같은 값).</summary>
    public const double PondedM = 1e-3;

    private const byte Active = 0;
    private const byte Stopped = 1;
    private const byte Spilled = 2;

    /// <summary>
    /// 물수지를 풉니다. z: (N,) 호수 바닥까지 낮춘 지표 [m]. isOutlet: (N,) 바다나 유역 출구.
    /// runoff: (N,) 실제 유출 [m/yr] (바닥값을 주지 않은 값). precip, pet: (N,) 강수와 잠재 증발산 [m/yr].
    /// inflow: (N,) 바깥에서 그 칸으로 들어오는 물 [m³/yr] (없으면 null). routeEps: 물길을 정할 때 쓰는 채움 기울기 [m].
    /// minDepthM: 넘치지 못하는 호수의 가장 깊은 곳 물 깊이가 이보다 얕으면 마른 분지로 셉니다 [m] (lakes.min_depth_m).
    /// 물길은 채운 지표에서 가장 가파른 이웃으로 정합니다.
    /// </summary>
    public static LakeBalanceResult Solve(
        CellGraph graph, double[] z, bool[] isOutlet, double[] runoff, double[] precip, double[] pet, double[]? inflow,
        double routeEps, double minDepthM = 0.0) =>
        Solve(graph, z, isOutlet, runoff, precip, pet, inflow, outlet =>
        {
            double[] zt = Depressions.FillEpsilon(z, graph.Nbr, outlet, routeEps);
            (long[] rcv, _, _) = Routing.D8Receivers(zt, graph.Nbr, graph.Dist, outlet);
            return (rcv, Routing.TopoOrder(rcv));
        }, minDepthM);

    /// <summary>
    /// 위와 같되 물길을 route 로 정합니다. route 는 '물이 끝나는 칸'(출구와 웅덩이 바닥)을 받아 (receiver, 하류가 먼저 오는 순서)를
    /// 돌려줍니다. 생성 단계가 뒤에서 쓸 물길과 같은 규칙을 넘겨야, 여기서 센 '들어오는 물'이 결과의 강과 맞습니다.
    /// </summary>
    public static LakeBalanceResult Solve(
        CellGraph graph, double[] z, bool[] isOutlet, double[] runoff, double[] precip, double[] pet, double[]? inflow,
        Func<bool[], (long[] Receiver, long[] Order)> route, double minDepthM = 0.0)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n || runoff.Length != n || precip.Length != n || pet.Length != n
            || (inflow is not null && inflow.Length != n))
        {
            throw new ArgumentException(
                $"z, isOutlet, runoff, precip, pet, inflow 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, "
                + $"{isOutlet.Length}, {runoff.Length}, {precip.Length}, {pet.Length}, {inflow?.Length.ToString() ?? "없음"}");
        }
        if (!(double.IsFinite(minDepthM) && minDepthM >= 0.0))
        {
            throw new ArgumentException($"minDepthM 은 0 이상의 유한한 값이어야 합니다. 받은 값: {minDepthM}. lakes.min_depth_m 을 확인하세요");
        }
        double[] area = graph.Area;
        int[] nbr = graph.Nbr;
        // 칸이 물에 잠기면 한 해에 더 드는 물 [m³/yr] = (PET − AET) × 넓이, AET = P − R. 0 보다 작아지지 않게 자릅니다.
        double[] cost = new double[n];
        double[] source = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!(double.IsFinite(runoff[c]) && double.IsFinite(precip[c]) && double.IsFinite(pet[c])))
            {
                throw new ArgumentException($"칸 {c} 의 runoff, precip, pet 에 NaN 이나 inf 가 있습니다: {runoff[c]}, {precip[c]}, {pet[c]}");
            }
            double aet = Math.Max(precip[c] - Math.Max(runoff[c], 0.0), 0.0);
            cost[c] = Math.Max(pet[c] - aet, 0.0) * area[c];
            source[c] = (Math.Max(runoff[c], 0.0) * area[c]) + (inflow is null ? 0.0 : Math.Max(inflow[c], 0.0));
        }

        // --- 1. 웅덩이 바닥과, 칸마다 물이 모이는 바닥
        double[] zHat = Depressions.FillDepressions(z, nbr, isOutlet);
        bool[] pit = Pits(graph, z, zHat, isOutlet);
        bool[] ends = new bool[n];
        for (int c = 0; c < n; c++)
        {
            ends[c] = isOutlet[c] || pit[c];
        }
        (long[] rcv, long[] order) = route(ends);
        if (rcv.Length != n || order.Length != n)
        {
            throw new ArgumentException($"route 가 돌려준 receiver, order 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {rcv.Length}, {order.Length}");
        }
        int[] lakeOfPit = new int[n];
        Array.Fill(lakeOfPit, -1);
        var pits = new List<int>();
        for (int c = 0; c < n; c++)
        {
            // route 가 바닥이 아닌 칸에서 물을 멈추면 그 칸도 바닥으로 봅니다.
            if (pit[c] || (!isOutlet[c] && rcv[c] == c))
            {
                lakeOfPit[c] = pits.Count;
                pits.Add(c);
            }
        }
        int[] term = new int[n];
        for (int k = 0; k < n; k++)
        {
            int c = (int)order[k];
            int r = (int)rcv[c];
            term[c] = r == c ? c : term[r];
        }
        int nl = pits.Count;
        double[] direct = new double[nl];
        for (int c = 0; c < n; c++)
        {
            int t = term[c];
            if (!isOutlet[t] && lakeOfPit[t] >= 0)
            {
                direct[lakeOfPit[t]] += source[c];
            }
        }

        // --- 2. 채우고 넘치고 합치기
        var s = new State(graph, z, isOutlet, cost, term, lakeOfPit, pits, direct);
        s.Run();

        // --- 3. 결과
        return s.Finish(minDepthM);
    }

    /// <summary>
    /// 물수지가 맞지 않는 정도: 호수마다 |들어온 물 − 증발 − 넘친 물| ÷ 들어온 물 의 가장 큰 값 (들어온 물이 1 m³/yr 보다 적으면
    /// 1 m³/yr 로 나눔). 계산의 반올림 오차만 남으므로 10⁻¹² 쯤입니다. 호수가 없으면 0.
    /// </summary>
    public static double Residual(LakeBalanceResult result)
    {
        double worst = 0.0;
        foreach (LakeBudget b in result.Lakes)
        {
            double miss = Math.Abs(b.InflowM3PerYr - b.EvaporationM3PerYr - b.OverflowM3PerYr);
            worst = Math.Max(worst, miss / Math.Max(b.InflowM3PerYr, 1.0));
        }
        return worst;
    }

    /// <summary>
    /// 웅덩이 바닥: 고인 칸(넘칠 높이가 바닥보다 PondedM 넘게 높은 칸) 가운데 더 낮은 이웃이 없는 칸. 높이가 똑같은 칸이 이웃해
    /// 평평한 바닥을 이루면, 그 바닥 둘레에 더 낮은 칸이 없을 때만 번호가 가장 작은 칸 하나를 바닥으로 둡니다.
    /// </summary>
    private static bool[] Pits(CellGraph graph, double[] z, double[] zHat, bool[] isOutlet)
    {
        int n = graph.NCells;
        int[] nbr = graph.Nbr;
        bool[] pit = new bool[n];
        bool[] seen = new bool[n];
        var stack = new Stack<int>();
        for (int c0 = 0; c0 < n; c0++)
        {
            if (seen[c0] || isOutlet[c0] || !(zHat[c0] - z[c0] > PondedM))
            {
                continue;
            }
            bool lower = false;
            for (int s = 0; s < CellGraph.NSlots && !lower; s++)
            {
                int v = nbr[(c0 * CellGraph.NSlots) + s];
                lower = v >= 0 && z[v] < z[c0];
            }
            if (lower)
            {
                continue;
            }
            // 같은 높이로 이어진 평평한 바닥 전체를 봅니다.
            bool escape = false;
            int first = c0;
            seen[c0] = true;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                first = Math.Min(first, c);
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(c * CellGraph.NSlots) + s];
                    if (v < 0)
                    {
                        continue;
                    }
                    if (z[v] < z[c] || (z[v] == z[c] && isOutlet[v]))
                    {
                        escape = true;
                    }
                    else if (z[v] == z[c] && !seen[v])
                    {
                        seen[v] = true;
                        stack.Push(v);
                    }
                }
            }
            if (!escape)
            {
                pit[first] = true;
            }
        }
        return pit;
    }

    /// <summary>(열쇠, 칸 번호) 를 작은 쪽부터: 높이가 같으면 번호가 작은 칸이 먼저입니다.</summary>
    private sealed class KeyOrder : IComparer<(double Key, int Cell)>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare((double Key, int Cell) a, (double Key, int Cell) b)
        {
            int k = a.Key.CompareTo(b.Key);
            return k != 0 ? k : a.Cell.CompareTo(b.Cell);
        }
    }

    /// <summary>채우고 넘치고 합치기의 상태. 호수 번호는 웅덩이 바닥 번호이고, 합친 호수는 합집합 찾기(union–find)의 뿌리로 셉니다.</summary>
    private sealed class State
    {
        private readonly CellGraph _graph;
        private readonly double[] _z;
        private readonly bool[] _isOutlet;
        private readonly double[] _cost;
        private readonly int[] _term;
        private readonly int[] _lakeOfPit;
        private readonly List<int> _pits;
        private readonly double[] _direct;
        private readonly int[] _parent;
        private readonly PriorityQueue<int, (double Key, int Cell)>[] _heap;
        private readonly List<int>[] _flooded;
        private readonly double[] _level;
        private readonly double[] _budget;
        private readonly double[] _paid;
        private readonly byte[] _state;
        private readonly int[] _target;
        private readonly int[] _firstPit;
        private readonly int[] _owner;
        private readonly int[] _queued;
        private readonly List<(int From, int To, double Amount)> _edges = [];
        private readonly Queue<int> _work = new();
        private int _nSpills;
        private int _nMerges;

        public State(
            CellGraph graph, double[] z, bool[] isOutlet, double[] cost, int[] term, int[] lakeOfPit, List<int> pits, double[] direct)
        {
            _graph = graph;
            _z = z;
            _isOutlet = isOutlet;
            _cost = cost;
            _term = term;
            _lakeOfPit = lakeOfPit;
            _pits = pits;
            _direct = direct;
            int nl = pits.Count;
            int n = graph.NCells;
            _parent = new int[nl];
            _heap = new PriorityQueue<int, (double, int)>[nl];
            _flooded = new List<int>[nl];
            _level = new double[nl];
            _budget = (double[])direct.Clone();
            _paid = new double[nl];
            _state = new byte[nl];
            _target = new int[nl];
            _firstPit = new int[nl];
            _owner = new int[n];
            _queued = new int[n];
            Array.Fill(_owner, -1);
            Array.Fill(_queued, -1);
            for (int l = 0; l < nl; l++)
            {
                int p = pits[l];
                _parent[l] = l;
                _heap[l] = new PriorityQueue<int, (double, int)>(KeyOrder.Instance);
                _heap[l].Enqueue(p, (z[p], p));
                _queued[p] = l;
                _flooded[l] = [];
                _level[l] = z[p];
                _target[l] = -2;
                _firstPit[l] = p;
                _work.Enqueue(l);
            }
        }

        private int Find(int l)
        {
            while (_parent[l] != l)
            {
                _parent[l] = _parent[_parent[l]];
                l = _parent[l];
            }
            return l;
        }

        public void Run()
        {
            while (_work.Count > 0)
            {
                int r = Find(_work.Dequeue());
                if (_state[r] == Active)
                {
                    Process(r);
                }
            }
        }

        /// <summary>호수 r 을 물이 떨어지거나 턱을 넘을 때까지 잠급니다.</summary>
        private void Process(int r)
        {
            double[] z = _z;
            while (true)
            {
                PriorityQueue<int, (double Key, int Cell)> heap = _heap[r];
                if (!heap.TryPeek(out int x, out (double Key, int Cell) top))
                {
                    _state[r] = Stopped;
                    return;
                }
                double key = top.Key;
                int ox = _owner[x] >= 0 ? Find(_owner[x]) : -1;
                if (ox == r)
                {
                    heap.Dequeue();
                    continue;
                }
                if (_isOutlet[x])
                {
                    Spill(r, -1, key);
                    return;
                }
                // 이 칸이 다른 호수의 물에 속하는지 봅니다: 다른 호수가 잠근 칸, 아직 물이 없는 다른 웅덩이의 바닥,
                // 또는 지금 수면보다 낮아(턱을 넘어 닿은 칸) 물이 다른 바닥으로 흘러가는 칸.
                int other = ox;
                if (other < 0 && _lakeOfPit[x] >= 0 && Find(_lakeOfPit[x]) != r)
                {
                    other = Find(_lakeOfPit[x]);
                }
                if (other < 0 && z[x] < key)
                {
                    int t = _term[x];
                    if (_isOutlet[t])
                    {
                        Spill(r, -1, key);
                        return;
                    }
                    int tl = Find(_lakeOfPit[t]);
                    other = tl != r ? tl : -1;
                }
                if (other >= 0)
                {
                    // 그 호수의 수면이 우리 수면보다 낮으면 넘쳐 들어가고, 같거나 높으면 두 수면이 만난 것이라 하나로 합칩니다.
                    // (높은 경우는 그 호수가 넘친 뒤 물가로 돌려놓은 칸(물 깊이 PondedM 이하)에 닿았을 때입니다.)
                    if (_level[other] < key)
                    {
                        Spill(r, other, key);
                        return;
                    }
                    r = Merge(r, other, key);
                    continue;
                }
                if (_budget[r] < _cost[x])
                {
                    _state[r] = Stopped;
                    return;
                }
                _budget[r] -= _cost[x];
                _paid[r] += _cost[x];
                heap.Dequeue();
                _owner[x] = r;
                _flooded[r].Add(x);
                _level[r] = Math.Max(_level[r], key);
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = _graph.Nbr[(x * CellGraph.NSlots) + s];
                    if (v < 0 || (_owner[v] >= 0 && Find(_owner[v]) == r) || (_queued[v] >= 0 && Find(_queued[v]) == r))
                    {
                        continue;
                    }
                    _queued[v] = r;
                    heap.Enqueue(v, (Math.Max(z[v], _level[r]), v));
                }
            }
        }

        /// <summary>
        /// 호수 r 이 높이 key 의 턱에 닿았습니다. 턱 높이에 걸친 칸(물 깊이 PondedM 이하)은 호수가 아니라서 그 칸에 쓴 물을 돌려받고,
        /// 남은 물을 모두 target 호수(−1 이면 바다)로 넘깁니다. 바다·출구 칸이 지금 수면보다 높으면 수면은 그 높이까지 올라 넘칩니다.
        /// </summary>
        private void Spill(int r, int target, double key)
        {
            _state[r] = Spilled;
            _level[r] = Math.Max(_level[r], key);
            _target[r] = target;
            double refund = 0.0;
            List<int> cells = _flooded[r];
            var keep = new List<int>(cells.Count);
            foreach (int c in cells)
            {
                if (_level[r] - _z[c] > PondedM)
                {
                    keep.Add(c);
                    continue;
                }
                // 물가의 칸: 다시 잠길 수 있게 기다리는 줄로 돌려놓습니다 (뒤에 다른 호수와 합치면 그 호수가 잠급니다).
                _owner[c] = -1;
                _queued[c] = r;
                _heap[r].Enqueue(c, (Math.Max(_z[c], _level[r]), c));
                refund += _cost[c];
            }
            _flooded[r] = keep;
            _paid[r] -= refund;
            double amount = _budget[r] + refund;
            _budget[r] = 0.0;
            _nSpills++;
            Give(r, target, amount);
        }

        /// <summary>호수 from 이 넘긴 물을 target 에 줍니다. 이미 넘치는 호수가 받은 물은 그 호수의 턱 너머로 그대로 보냅니다.</summary>
        private void Give(int from, int target, double amount)
        {
            _edges.Add((from, target, amount));
            if (target < 0)
            {
                return;
            }
            int t = Find(target);
            int guard = 0;
            while (_state[t] == Spilled)
            {
                int next = _target[t];
                _edges.Add((t, next, amount));
                if (next < 0)
                {
                    return;
                }
                t = Find(next);
                if (++guard > _parent.Length)
                {
                    throw new InvalidOperationException($"호수 물수지: 넘친 물이 호수 {from} 에서 돌고 돕니다 (호수 {_parent.Length}개). 코드 결함입니다");
                }
            }
            _budget[t] += amount;
            if (_state[t] == Stopped)
            {
                _state[t] = Active;
                _work.Enqueue(t);
            }
        }

        /// <summary>두 호수를 하나로 합칩니다. 기다리는 칸이 많은 쪽에 다른 쪽을 붙이고, 수면은 만난 높이로 둡니다.</summary>
        private int Merge(int r, int m, double key)
        {
            _nMerges++;
            int big = _heap[r].Count >= _heap[m].Count ? r : m;
            int small = big == r ? m : r;
            _parent[small] = big;
            while (_heap[small].TryDequeue(out int c, out (double Key, int Cell) p))
            {
                _heap[big].Enqueue(c, p);
            }
            _flooded[big].AddRange(_flooded[small]);
            _flooded[small] = [];
            _level[big] = Math.Max(Math.Max(_level[r], _level[m]), key);
            _budget[big] = _budget[r] + _budget[m];
            _paid[big] = _paid[r] + _paid[m];
            _budget[small] = 0.0;
            _paid[small] = 0.0;
            _firstPit[big] = Math.Min(_firstPit[r], _firstPit[m]);
            _state[big] = Active;
            _target[big] = -2;
            return big;
        }

        public LakeBalanceResult Finish(double minDepthM)
        {
            int n = _graph.NCells;
            int nl = _pits.Count;
            double[] z = _z;
            double[] levelOut = new double[n];
            Array.Fill(levelOut, double.NaN);
            bool[] closed = new bool[n];
            bool[] sink = new bool[n];
            // 합친 호수 사이에 오간 물은 안에서 돈 물이라 빼고, 밖과 주고받은 물만 셉니다.
            double[] inExt = new double[nl];
            double[] outExt = new double[nl];
            foreach ((int from, int to, double amount) in _edges)
            {
                int f = Find(from);
                int t = to < 0 ? -1 : Find(to);
                if (f == t)
                {
                    continue;
                }
                outExt[f] += amount;
                if (t >= 0)
                {
                    inExt[t] += amount;
                }
            }
            double[] directRoot = new double[nl];
            for (int l = 0; l < nl; l++)
            {
                directRoot[Find(l)] += _direct[l];
            }
            var roots = new List<int>();
            for (int l = 0; l < nl; l++)
            {
                if (Find(l) == l)
                {
                    roots.Add(l);
                }
            }
            roots.Sort((a, b) => _firstPit[a].CompareTo(_firstPit[b]));
            int[] index = new int[nl];
            Array.Fill(index, -1);
            for (int i = 0; i < roots.Count; i++)
            {
                index[roots[i]] = i;
            }
            var lakes = new List<LakeBudget>(roots.Count);
            int nOpen = 0;
            int nClosed = 0;
            int nDry = 0;
            int nShallow = 0;
            foreach (int r in roots)
            {
                double inflow = directRoot[r] + inExt[r];
                List<int> cells = _flooded[r];
                if (_state[r] == Spilled)
                {
                    int tgt = _target[r] < 0 ? -1 : index[Find(_target[r])];
                    nOpen += cells.Count > 0 ? 1 : 0;
                    lakes.Add(new LakeBudget(_firstPit[r], cells.Count, _level[r], inflow, _paid[r], outExt[r], tgt, LakeState.Open));
                    continue;
                }
                // 멈춘 호수: 다음 칸을 잠그다 만 몫만큼 수면을 올립니다.
                double h = _level[r];
                if (cells.Count > 0 && _heap[r].TryPeek(out int next, out (double Key, int Cell) nk) && _cost[next] > 0.0)
                {
                    double share = Math.Min(Math.Max(_budget[r] / _cost[next], 0.0), 1.0);
                    h = _level[r] + (share * Math.Max(nk.Key - _level[r], 0.0));
                }
                int lowest = -1;
                int nWet = 0;
                foreach (int c in cells)
                {
                    if (lowest < 0 || z[c] < z[lowest] || (z[c] == z[lowest] && c < lowest))
                    {
                        lowest = c;
                    }
                    nWet += h - z[c] > PondedM ? 1 : 0;
                }
                double evaporation = _paid[r] + _budget[r];
                double deepest = lowest < 0 ? 0.0 : h - z[lowest];
                if (nWet == 0 || deepest < minDepthM)
                {
                    // 마른 분지: 물길은 가장 낮은 칸에서 끝나고, 들어온 물은 모두 그 바닥에서 증발합니다.
                    int end = lowest >= 0 ? lowest : _firstPit[r];
                    sink[end] = true;
                    nDry++;
                    nShallow += nWet > 0 ? 1 : 0;
                    lakes.Add(new LakeBudget(_firstPit[r], 0, z[end], inflow, evaporation, outExt[r], -2, LakeState.Dry));
                    continue;
                }
                foreach (int c in cells)
                {
                    if (h - z[c] > PondedM)
                    {
                        closed[c] = true;
                        sink[c] = true;
                        levelOut[c] = h;
                    }
                }
                nClosed++;
                lakes.Add(new LakeBudget(_firstPit[r], nWet, h, inflow, evaporation, outExt[r], -2, LakeState.Closed));
            }
            return new LakeBalanceResult(levelOut, closed, sink, nOpen, nClosed, nDry, nShallow, nl, _nSpills, _nMerges, lakes);
        }
    }
}
