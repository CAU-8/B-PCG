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
        foreach (double value in zt)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException("zt 에 NaN 이나 무한대가 있습니다");
            }
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
