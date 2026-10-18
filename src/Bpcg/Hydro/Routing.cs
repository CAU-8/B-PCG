using System;
using Bpcg.Core;

namespace Bpcg.Hydro;

/// <summary>
/// 수신 셀(D8 + 히스테리시스)과 하류부터의 순서 (src/bpcg/hydro/routing.py, 가이드 2장 ②③, 4장).
/// </summary>
/// <remarks>
/// 같은 경사면 셀 번호가 작은 이웃을 고르고(슬롯 순서가 아님), 히스테리시스 비교는
/// <c>!(best_s &gt; (1 + η)·sp)</c> 모양을 지킵니다(docs/csharp_port.md 6장 hydro).
/// numba 의 기본 error_model='python' 처럼 낮은 이웃까지 거리가 0 이면 DivideByZeroException 을 냅니다.
/// </remarks>
public static class Routing
{
    /// <summary>
    /// 한 칸의 D8 수신 셀 (_d8_kernel 의 칸 몸체). hasPrev 면 p 는 지난 반복의 수신 셀이고, 히스테리시스를 적용합니다.
    /// slope 는 고른 수신 셀까지의 경사 [m/m] (출구·낮은 이웃이 없는 칸은 0).
    /// </summary>
    private static long CellReceiver(
        int c, double[] zt, int[] nbr, int nSlot, double[] dist, bool[] isOutlet, bool hasPrev, long p, double eta,
        out double slope)
    {
        slope = 0.0;
        if (isOutlet[c])
        {
            return c;
        }
        double zc = zt[c];
        long best = -1;
        double bestS = 0.0;
        for (int s = 0; s < nSlot; s++)
        {
            int k = nbr[(c * nSlot) + s];
            if (k < 0)
            {
                continue;
            }
            double zk = zt[k];
            if (zk < zc)
            {
                double d = dist[(c * nSlot) + s];
                if (d == 0.0)
                {
                    throw new DivideByZeroException("float division by zero");
                }
                double sl = (zc - zk) / d;
                // 같은 경사면 셀 번호가 작은 이웃 (결정성)
                if (best < 0 || sl > bestS || (sl == bestS && k < best))
                {
                    best = k;
                    bestS = sl;
                }
            }
        }
        if (best < 0)
        {
            // 낮은 이웃이 없는 칸(채우지 않은 고도의 웅덩이 바닥)은 자기 자신을 가리킵니다.
            return c;
        }
        if (hasPrev && p != best && p != c)
        {
            // 가이드 4장 히스테리시스: 옛 수신 셀이 아직 확실히 낮은 이웃이면,
            // 새 경사가 (1+η)배보다 클 때만 바꿉니다.
            for (int s = 0; s < nSlot; s++)
            {
                if (nbr[(c * nSlot) + s] == p)
                {
                    double zp = zt[p];
                    if (zp < zc)
                    {
                        double sp = (zc - zp) / dist[(c * nSlot) + s];
                        if (!(bestS > (1.0 + eta) * sp))
                        {
                            best = p;
                            bestS = sp;
                        }
                    }
                    break;
                }
            }
        }
        slope = bestS;
        return best;
    }

    /// <summary>D8 커널 (_d8_kernel). prev 와 다른 칸 수를 돌려줍니다(prev 가 없으면 N).</summary>
    internal static int D8Kernel(
        double[] zt, int[] nbr, int nSlot, double[] dist, bool[] isOutlet, long[]? prev, double eta,
        long[] rcv, double[]? slope)
    {
        int n = zt.Length;
        bool hasPrev = prev is not null;
        for (int c = 0; c < n; c++)
        {
            rcv[c] = CellReceiver(c, zt, nbr, nSlot, dist, isOutlet, hasPrev, hasPrev ? prev![c] : -1, eta, out double s);
            if (slope is not null)
            {
                slope[c] = s;
            }
        }
        int nChanged = n;
        if (hasPrev)
        {
            nChanged = 0;
            for (int c = 0; c < n; c++)
            {
                if (rcv[c] != prev![c])
                {
                    nChanged++;
                }
            }
        }
        return nChanged;
    }

    /// <summary>
    /// solver 전용 D8: 검증된 그래프에서 칸 덩어리마다 병렬로 rcv 버퍼에 씁니다(int 색인). 칸마다 결과가 독립이라
    /// 덩어리 나눔과 상관없이 <see cref="D8Kernel"/> 과 같은 수신 셀이 나옵니다. 바뀐 칸 수는 세지 않습니다.
    /// </summary>
    internal static void D8IntoTrusted(double[] zt, int[] nbr, double[] dist, bool[] isOutlet, int[]? prev, double eta, int[] rcv)
    {
        int n = zt.Length;
        // 유한한지는 덩어리마다 나눠 보고, 수신 셀을 하나라도 쓰기 전에 오류를 냅니다.
        int notFinite = 0;
        Parallelism.ForChunks(0, n, D8ChunkCells, (start, end) =>
        {
            for (int c = start; c < end; c++)
            {
                if (!double.IsFinite(zt[c]))
                {
                    System.Threading.Volatile.Write(ref notFinite, 1);
                    return;
                }
            }
        });
        if (notFinite != 0)
        {
            throw new ArgumentException("zt 에 NaN 이나 무한대가 있습니다");
        }
        int nSlot = n == 0 ? 0 : nbr.Length / n;
        bool hasPrev = prev is not null;
        Parallelism.ForChunks(0, n, D8ChunkCells, (start, end) =>
        {
            for (int c = start; c < end; c++)
            {
                rcv[c] = (int)CellReceiver(c, zt, nbr, nSlot, dist, isOutlet, hasPrev, hasPrev ? prev![c] : -1, eta, out _);
            }
        });
    }

    // 덩어리 하나의 칸 수: laptop 행성·히어로(약 160만 칸)에서 16K–64K 칸이 가장 빨랐습니다.
    private const int D8ChunkCells = 16_384;

    /// <summary>
    /// 기여 셀 CSR (_donor_csr_kernel) 을 주어진 버퍼에 만듭니다: donors[start[c]..start[c+1]) 가 c 로 물을 보내는 칸(번호 오름차순).
    /// start (N+1,), donors·fill (N,) (donors 는 앞쪽 start[N] 칸만 씀, fill 은 작업용).
    /// </summary>
    internal static void DonorCsrInto(int[] rcv, int[] start, int[] donors, int[] fill)
    {
        int n = rcv.Length;
        Array.Clear(start);
        for (int c = 0; c < n; c++)
        {
            int r = rcv[c];
            if (r != c)
            {
                start[r + 1] += 1;
            }
        }
        for (int c = 0; c < n; c++)
        {
            start[c + 1] += start[c];
        }
        Array.Copy(start, fill, n);
        for (int c = 0; c < n; c++)
        {
            int r = rcv[c];
            if (r != c)
            {
                donors[fill[r]] = c;
                fill[r] += 1;
            }
        }
    }

    /// <summary>
    /// 출구(자기 자신을 가리키는 칸)를 먼저 모두 적고, 출구마다 기여 셀을 깊이 우선으로 적습니다 (_topo_kernel).
    /// stack·order 는 (N,) 버퍼. 채운 칸 수를 돌려주고, nRoot 에 출구 수를 줍니다.
    /// </summary>
    internal static int TopoInto(int[] rcv, int[] start, int[] donors, int[] stack, int[] order, out int nRoot)
    {
        int n = rcv.Length;
        int m = 0;
        for (int c = 0; c < n; c++)
        {
            if (rcv[c] == c)
            {
                order[m] = c;
                m++;
            }
        }
        nRoot = m;
        for (int q = 0; q < nRoot; q++)
        {
            int root = order[q];
            int sp = 0;
            for (int t = start[root + 1] - 1; t > start[root] - 1; t--)
            {
                stack[sp] = donors[t];
                sp++;
            }
            while (sp > 0)
            {
                sp--;
                int c = stack[sp];
                order[m] = c;
                m++;
                // 번호가 작은 기여 셀을 먼저 꺼내도록 거꾸로 넣습니다.
                for (int t = start[c + 1] - 1; t > start[c] - 1; t--)
                {
                    stack[sp] = donors[t];
                    sp++;
                }
            }
        }
        return m;
    }

    // 출구를 모을 때 덩어리 하나의 칸 수.
    private const int RootChunkCells = 16_384;

    /// <summary><see cref="TopoIntoParallel"/> 의 작업 버퍼. 반복마다 다시 씁니다.</summary>
    internal sealed class TopoScratch
    {
        // 작업 하나가 순서를 적는 조각의 길이. 작업들은 공용 버퍼에서 조각을 하나씩 받아 씁니다.
        internal const int BlockSize = 4096;

        // 작업 수의 상한. 작업마다 마지막 조각이 덜 찰 수 있어 공용 버퍼를 이만큼 넉넉히 잡습니다.
        internal const int MaxTasks = 512;

        internal TopoScratch(int n)
        {
            Pool = new int[(((n + BlockSize - 1) / BlockSize) + MaxTasks) * BlockSize];
        }

        internal int[] Pool { get; }

        internal System.Collections.Generic.List<int>[] Blocks { get; } = new System.Collections.Generic.List<int>[MaxTasks];

        internal int[] LastFill { get; } = new int[MaxTasks];

        internal int[]?[] Stacks { get; } = new int[MaxTasks][];

        // 순서를 이루는 토막: 출구 번호 [A, B) 의 나무들(출구 자신은 빼고), 칸 A 의 부분 나무, 칸 A 하나.
        internal System.Collections.Generic.List<(byte Kind, int A, int B)> Pieces { get; } = [];

        internal System.Collections.Generic.List<int> TaskStart { get; } = [];
    }

    private const byte PieceRoots = 0;
    private const byte PieceSubtree = 1;
    private const byte PieceCell = 2;

    /// <summary>
    /// <see cref="TopoInto"/> 와 같은 순서를 병렬로 만듭니다. 그 순서는 출구를 번호 순서대로 모두 적은 뒤, 출구마다 기여 셀 나무를
    /// 깊이 우선으로(번호가 작은 기여 셀 먼저) 적은 것입니다. 나무의 앞뒤 순서는 구조로 정해지므로, 순서를 토막(출구 묶음의 나무들,
    /// 부분 나무 하나, 칸 하나)으로 잘라 토막마다 따로 적은 뒤 토막 순서대로 이어 붙이면 한 줄로 적은 것과 같습니다.
    /// weightHint(칸마다 부분 나무 크기의 짐작, 예: 지난 반복의 집수 면적)가 splitWeight 보다 큰 나무는 줄기를 따라 더 잘게 잘라
    /// 큰 나무 하나도 나눠 돕니다. 짐작은 일을 어떻게 나눌지에만 쓰고 결과에는 영향이 없습니다. 병렬 실행 수가 1 이면
    /// <see cref="TopoInto"/> 를 그대로 부릅니다.
    /// </summary>
    internal static int TopoIntoParallel(
        int[] rcv, int[] start, int[] donors, int[] stack, int[] order, TopoScratch scratch,
        double[]? weightHint, double splitWeight, out int nRoot)
    {
        int n = rcv.Length;
        int workers = Parallelism.MaxDegreeOfParallelism ?? Environment.ProcessorCount;
        if (workers <= 1 || n <= RootChunkCells)
        {
            return TopoInto(rcv, start, donors, stack, order, out nRoot);
        }
        int nChunks = (n + RootChunkCells - 1) / RootChunkCells;
        int[] rootStart = new int[nChunks + 1];
        Parallelism.ForChunks(0, n, RootChunkCells, (lo, hi) =>
        {
            int count = 0;
            for (int c = lo; c < hi; c++)
            {
                count += rcv[c] == c ? 1 : 0;
            }
            rootStart[(lo / RootChunkCells) + 1] = count;
        });
        for (int j = 0; j < nChunks; j++)
        {
            rootStart[j + 1] += rootStart[j];
        }
        nRoot = rootStart[nChunks];
        Parallelism.ForChunks(0, n, RootChunkCells, (lo, hi) =>
        {
            int m = rootStart[lo / RootChunkCells];
            for (int c = lo; c < hi; c++)
            {
                if (rcv[c] == c)
                {
                    order[m] = c;
                    m++;
                }
            }
        });
        int roots = nRoot;
        bool split = weightHint is not null && splitWeight > 0.0;

        // 토막 만들기 (한 줄로, 큰 나무의 줄기 길이만큼만 돎).
        var pieces = scratch.Pieces;
        pieces.Clear();
        int rangeSize = Math.Max(1, roots / (workers * 8));
        void AddRoots(int lo, int hi)
        {
            for (int q = lo; q < hi; q += rangeSize)
            {
                pieces.Add((PieceRoots, q, Math.Min(hi, q + rangeSize)));
            }
        }
        int cursor = 0;
        if (split)
        {
            double[] hint = weightHint!;
            // 큰 출구(짐작한 나무가 splitWeight 보다 큼)를 출구 번호 순서대로 찾습니다. 덩어리마다 찾아 덩어리 순서대로 잇습니다.
            int nRootChunks = (roots + RootChunkCells - 1) / RootChunkCells;
            var bigByChunk = new System.Collections.Generic.List<int>?[nRootChunks];
            Parallelism.ForChunks(0, roots, RootChunkCells, (lo, hi) =>
            {
                System.Collections.Generic.List<int>? found = null;
                for (int q = lo; q < hi; q++)
                {
                    int r = order[q];
                    if (hint[r] > splitWeight && start[r + 1] > start[r])
                    {
                        (found ??= []).Add(q);
                    }
                }
                bigByChunk[lo / RootChunkCells] = found;
            });
            int[] cut = stack;
            foreach (int q in EnumerateBig(bigByChunk))
            {
                int r = order[q];
                AddRoots(cursor, q);
                cursor = q + 1;
                // 큰 출구: 기여 셀 부분 나무를 차례로 토막으로 두되, 큰 부분 나무는 칸 하나 + 그 기여 셀 부분 나무들로 풉니다.
                int sp = 0;
                PushDonors(r, start, donors, ref cut, ref sp);
                while (sp > 0)
                {
                    sp--;
                    int c = cut[sp];
                    if (hint[c] > splitWeight && start[c + 1] > start[c])
                    {
                        pieces.Add((PieceCell, c, 0));
                        PushDonors(c, start, donors, ref cut, ref sp);
                    }
                    else
                    {
                        pieces.Add((PieceSubtree, c, 0));
                    }
                }
            }
        }
        AddRoots(cursor, roots);

        // 토막을 작업으로 묶습니다: 짐작한 크기가 splitWeight 의 절반쯤 모이면 작업 하나. 출구 묶음은 따로 작업 하나.
        var taskStart = scratch.TaskStart;
        taskStart.Clear();
        double acc = 0.0;
        byte prevKind = PieceRoots;
        for (int i = 0; i < pieces.Count; i++)
        {
            (byte kind, int pa, _) = pieces[i];
            bool newTask = i == 0 || kind == PieceRoots || prevKind == PieceRoots || acc >= 0.5 * splitWeight;
            if (newTask && taskStart.Count < TopoScratch.MaxTasks)
            {
                taskStart.Add(i);
                acc = 0.0;
            }
            if (kind == PieceSubtree)
            {
                acc += weightHint![pa];
            }
            prevKind = kind;
        }
        int nTasks = taskStart.Count;
        taskStart.Add(pieces.Count);
        var next = new TopoWriter.Counter();
        Parallelism.ForTasks(nTasks, t =>
        {
            System.Collections.Generic.List<int> blocks = scratch.Blocks[t] ??= [];
            blocks.Clear();
            var w = new TopoWriter(scratch.Pool, blocks, next);
            int[] st = scratch.Stacks[t] ?? new int[64];
            for (int i = taskStart[t]; i < taskStart[t + 1]; i++)
            {
                (byte kind, int pa, int pb) = pieces[i];
                if (kind == PieceRoots)
                {
                    for (int q = pa; q < pb; q++)
                    {
                        Walk(order[q], start, donors, ref st, w);
                    }
                }
                else
                {
                    w.Write(pa);
                    if (kind == PieceSubtree)
                    {
                        Walk(pa, start, donors, ref st, w);
                    }
                }
            }
            scratch.Stacks[t] = st;
            scratch.LastFill[t] = w.LastFill;
        });
        int[] offset = new int[nTasks + 1];
        offset[0] = roots;
        for (int t = 0; t < nTasks; t++)
        {
            int nb = scratch.Blocks[t]!.Count;
            offset[t + 1] = offset[t] + (nb == 0 ? 0 : ((nb - 1) * TopoScratch.BlockSize) + scratch.LastFill[t]);
        }
        Parallelism.ForTasks(nTasks, t =>
        {
            int dst = offset[t];
            int left = offset[t + 1] - offset[t];
            foreach (int b in scratch.Blocks[t]!)
            {
                int len = Math.Min(left, TopoScratch.BlockSize);
                Array.Copy(scratch.Pool, b * TopoScratch.BlockSize, order, dst, len);
                dst += len;
                left -= len;
            }
        });
        return offset[nTasks];
    }

    private static System.Collections.Generic.IEnumerable<int> EnumerateBig(System.Collections.Generic.List<int>?[] byChunk)
    {
        foreach (System.Collections.Generic.List<int>? list in byChunk)
        {
            if (list is not null)
            {
                foreach (int q in list)
                {
                    yield return q;
                }
            }
        }
    }

    // top 의 기여 셀 나무를 깊이 우선으로 적습니다 (top 자신은 적지 않음, TopoInto 의 출구 하나와 같은 순서).
    private static void Walk(int top, int[] start, int[] donors, ref int[] st, TopoWriter w)
    {
        int sp = 0;
        PushDonors(top, start, donors, ref st, ref sp);
        while (sp > 0)
        {
            sp--;
            int c = st[sp];
            w.Write(c);
            PushDonors(c, start, donors, ref st, ref sp);
        }
    }

    // 작업 하나가 순서를 공용 버퍼의 조각에 차례로 적습니다. 조각이 차면 다음 빈 조각을 받습니다.
    private sealed class TopoWriter(int[] pool, System.Collections.Generic.List<int> blocks, TopoWriter.Counter next)
    {
        private int _at;
        private int _fill = TopoScratch.BlockSize;

        internal sealed class Counter
        {
            internal int Next;
        }

        internal int LastFill => blocks.Count == 0 ? 0 : _fill;

        internal void Write(int c)
        {
            if (_fill == TopoScratch.BlockSize)
            {
                int b = System.Threading.Interlocked.Increment(ref next.Next) - 1;
                blocks.Add(b);
                _at = b * TopoScratch.BlockSize;
                _fill = 0;
            }
            pool[_at + _fill] = c;
            _fill++;
        }
    }

    // c 의 기여 셀을 번호가 작은 것이 먼저 꺼내지도록 거꾸로 쌓습니다 (TopoInto 와 같은 순서). 스택이 모자라면 늘립니다.
    private static void PushDonors(int c, int[] start, int[] donors, ref int[] st, ref int sp)
    {
        int lo = start[c];
        int hi = start[c + 1];
        if (sp + (hi - lo) > st.Length)
        {
            Array.Resize(ref st, Math.Max(st.Length * 2, sp + (hi - lo)));
        }
        for (int t = hi - 1; t > lo - 1; t--)
        {
            st[sp] = donors[t];
            sp++;
        }
    }

    /// <summary>기여 셀 CSR (_donor_csr_kernel, long 입출력). 셀 번호는 <see cref="CheckRcv"/> 를 통과해야 합니다.</summary>
    internal static (long[] Start, long[] Donors) DonorCsrKernel(long[] rcv)
    {
        int n = rcv.Length;
        int[] r = ToInt(rcv);
        int[] start = new int[n + 1];
        int[] donors = new int[n];
        DonorCsrInto(r, start, donors, new int[n]);
        return (ToLong(start, n + 1), ToLong(donors, start[n]));
    }

    private static int[] ToInt(long[] a)
    {
        int[] o = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            o[i] = (int)a[i];
        }
        return o;
    }

    private static long[] ToLong(int[] a, int length)
    {
        long[] o = new long[length];
        for (int i = 0; i < length; i++)
        {
            o[i] = a[i];
        }
        return o;
    }

    /// <summary>수신 셀 검사 (_check_rcv).</summary>
    internal static void CheckRcv(long[] rcv)
    {
        foreach (long r in rcv)
        {
            if (r < 0 || r >= rcv.Length)
            {
                throw new ArgumentException("rcv 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)");
            }
        }
    }

    /// <summary>
    /// 가장 가파른 낮은 이웃을 수신 셀로 고릅니다 (d8_receivers). zt: (N,) ε 채움 고도 [m],
    /// nbr·dist: (N, K) 행 우선, prev: 지난 반복의 수신 셀 또는 null, eta: 히스테리시스 η.
    /// 반환: (rcv, slope [m/m], n_changed).
    /// </summary>
    public static (long[] Rcv, double[] Slope, int NChanged) D8Receivers(
        double[] zt, int[] nbr, double[] dist, bool[] isOutlet, long[]? prev = null, double eta = 0.0)
    {
        int n = zt.Length;
        if (n == 0 ? nbr.Length != 0 : nbr.Length % n != 0)
        {
            throw new ArgumentException($"nbr 는 (N, K) 정수 배열이어야 합니다: 원소 {nbr.Length}");
        }
        int nSlot = n == 0 ? 0 : nbr.Length / n;
        foreach (int k in nbr)
        {
            if (k < -1 || k >= n)
            {
                throw new ArgumentException("nbr 에 범위를 벗어난 셀 번호가 있습니다 (-1 또는 0..N-1)");
            }
        }
        if (dist.Length != nbr.Length)
        {
            throw new ArgumentException($"dist 는 nbr 와 모양이 같아야 합니다: {dist.Length} != {nbr.Length}");
        }
        if (isOutlet.Length != n)
        {
            throw new ArgumentException($"is_outlet 은 (N,) 이어야 합니다: ({isOutlet.Length},)");
        }
        foreach (double v in zt)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("zt 에 NaN 이나 무한대가 있습니다");
            }
        }
        if (!(double.IsFinite(eta) && eta >= 0.0))
        {
            throw new ArgumentException($"eta 는 0 이상이어야 합니다: {eta}");
        }
        if (prev is not null)
        {
            CheckRcv(prev);
            if (prev.Length != n)
            {
                throw new ArgumentException($"prev 는 (N,) 이어야 합니다: ({prev.Length},)");
            }
        }
        long[] rcv = new long[n];
        double[] slope = new double[n];
        int nChanged = D8Kernel(zt, nbr, nSlot, dist, isOutlet, prev, eta, rcv, slope);
        return (rcv, slope, nChanged);
    }

    /// <summary>기여 셀 목록 CSR (donor_lists): (start (N+1,), donors (M,)).</summary>
    public static (long[] Start, long[] Donors) DonorLists(long[] rcv)
    {
        CheckRcv(rcv);
        return DonorCsrKernel(rcv);
    }

    /// <summary>
    /// 하류가 먼저 오는 순서 σ (topo_order): 모든 칸에서 rank(r(c)) &lt; rank(c). 앞쪽은 자기 자신을 가리키는
    /// 칸 전부(번호 오름차순), 그 뒤는 출구마다 기여 셀을 깊이 우선(번호 작은 기여 셀 먼저)으로 담습니다.
    /// 수신 셀에 순환이 있으면 ArgumentException.
    /// </summary>
    public static long[] TopoOrder(long[] rcv)
    {
        CheckRcv(rcv);
        return TopoOrderTrusted(rcv);
    }

    /// <summary>범위 검사를 통과한 receiver 의 위상 순서 (int 커널을 쓰고 long 으로 돌려줌).</summary>
    internal static long[] TopoOrderTrusted(long[] rcv)
    {
        int n = rcv.Length;
        int[] r = ToInt(rcv);
        int[] start = new int[n + 1];
        int[] donors = new int[n];
        int[] order = new int[n];
        DonorCsrInto(r, start, donors, new int[n]);
        int m = TopoInto(r, start, donors, new int[n], order, out _);
        if (m != n)
        {
            throw new ArgumentException($"수신 셀에 순환이 있어 출구에 닿지 않는 칸이 {n - m}개 있습니다");
        }
        return ToLong(order, n);
    }
}
