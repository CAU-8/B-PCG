using System;

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
    /// <summary>D8 커널 (_d8_kernel). prev 와 다른 칸 수를 돌려줍니다(prev 가 없으면 N).</summary>
    internal static int D8Kernel(
        double[] zt, int[] nbr, int nSlot, double[] dist, bool[] isOutlet, long[]? prev, double eta,
        long[] rcv, double[] slope)
    {
        int n = zt.Length;
        bool hasPrev = prev is not null;
        int nChanged = 0;
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c])
            {
                rcv[c] = c;
                slope[c] = 0.0;
            }
            else
            {
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
                    rcv[c] = c;
                    slope[c] = 0.0;
                }
                else
                {
                    if (hasPrev)
                    {
                        long p = prev![c];
                        if (p != best && p != c)
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
                    }
                    rcv[c] = best;
                    slope[c] = bestS;
                }
            }
            if (hasPrev && rcv[c] != prev![c])
            {
                nChanged++;
            }
        }
        if (!hasPrev)
        {
            nChanged = n;
        }
        return nChanged;
    }

    /// <summary>기여 셀 CSR (_donor_csr_kernel): donors[start[c]..start[c+1]) 가 c 로 물을 보내는 칸(번호 오름차순).</summary>
    internal static (long[] Start, long[] Donors) DonorCsrKernel(long[] rcv)
    {
        int n = rcv.Length;
        long[] start = new long[n + 1];
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r != c)
            {
                start[r + 1] += 1;
            }
        }
        for (int c = 0; c < n; c++)
        {
            start[c + 1] += start[c];
        }
        long[] donors = new long[start[n]];
        long[] fill = new long[n];
        Array.Copy(start, fill, n);
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r != c)
            {
                donors[fill[r]] = c;
                fill[r] += 1;
            }
        }
        return (start, donors);
    }

    /// <summary>
    /// 출구(자기 자신을 가리키는 칸)를 먼저 모두 적고, 출구마다 기여 셀을 깊이 우선으로 적습니다 (_topo_kernel).
    /// 채운 칸 수를 돌려줍니다.
    /// </summary>
    internal static int TopoKernel(long[] rcv, long[] start, long[] donors, long[] order)
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
        int nRoot = m;
        long[] stack = new long[n];
        for (int q = 0; q < nRoot; q++)
        {
            long root = order[q];
            int sp = 0;
            for (long t = start[root + 1] - 1; t > start[root] - 1; t--)
            {
                stack[sp] = donors[t];
                sp++;
            }
            while (sp > 0)
            {
                sp--;
                long c = stack[sp];
                order[m] = c;
                m++;
                // 번호가 작은 기여 셀을 먼저 꺼내도록 거꾸로 넣습니다.
                for (long t = start[c + 1] - 1; t > start[c] - 1; t--)
                {
                    stack[sp] = donors[t];
                    sp++;
                }
            }
        }
        return m;
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
        (long[] start, long[] donors) = DonorCsrKernel(rcv);
        long[] order = new long[rcv.Length];
        int m = TopoKernel(rcv, start, donors, order);
        if (m != rcv.Length)
        {
            throw new ArgumentException($"수신 셀에 순환이 있어 출구에 닿지 않는 칸이 {rcv.Length - m}개 있습니다");
        }
        return order;
    }
}
