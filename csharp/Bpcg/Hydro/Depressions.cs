using System;

namespace Bpcg.Hydro;

/// <summary>
/// 웅덩이 채우기: priority-flood (src/bpcg/hydro/depressions.py, 가이드 2장 ①, docs/pipeline.md 6장).
/// </summary>
/// <remarks>
/// 힙의 키는 (높이, 셀 번호)이고 numba 의 2진 힙과 웅덩이 FIFO 를 같은 제어 흐름으로 옮겼습니다.
/// 값은 순수 힙과 같지만 0 의 부호까지 같게 하려고 흐름을 바꾸지 않습니다(docs/csharp_port.md 6장 hydro).
/// 최댓값은 <c>Math.Max</c> 대신 삼항식으로 씁니다(±0 과 NaN 처리가 다름).
/// </remarks>
public static class Depressions
{
    private static bool KeyLess(double za, long ia, double zb, long ib) => za < zb || (za == zb && ia < ib);

    private static int HeapPush(double[] hz, long[] hi, int size, double z, long i)
    {
        int k = size;
        while (k > 0)
        {
            int p = (k - 1) >> 1;
            if (KeyLess(z, i, hz[p], hi[p]))
            {
                hz[k] = hz[p];
                hi[k] = hi[p];
                k = p;
            }
            else
            {
                break;
            }
        }
        hz[k] = z;
        hi[k] = i;
        return size + 1;
    }

    // 맨 위 원소를 지우고 새 크기를 돌려줍니다. 맨 위 값은 부르기 전에 읽어 둡니다.
    private static int HeapPop(double[] hz, long[] hi, int size)
    {
        size--;
        double z = hz[size];
        long i = hi[size];
        int k = 0;
        while (true)
        {
            int a = (2 * k) + 1;
            if (a >= size)
            {
                break;
            }
            int m = a;
            int b = a + 1;
            if (b < size && KeyLess(hz[b], hi[b], hz[a], hi[a]))
            {
                m = b;
            }
            if (KeyLess(hz[m], hi[m], z, i))
            {
                hz[k] = hz[m];
                hi[k] = hi[m];
                k = m;
            }
            else
            {
                break;
            }
        }
        hz[k] = z;
        hi[k] = i;
        return size;
    }

    /// <summary>priority-flood + 웅덩이 큐 (Barnes 2014, _fill_kernel). 방문한 칸 수를 돌려줍니다.</summary>
    internal static int FillKernel(double[] z, int[] nbr, int nSlot, bool[] isOutlet, double[] output)
    {
        int n = z.Length;
        bool[] visited = new bool[n];
        double[] hz = new double[n];
        long[] hi = new long[n];
        long[] pit = new long[n];
        int size = 0;
        int nVisited = 0;
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c])
            {
                output[c] = z[c];
                visited[c] = true;
                nVisited++;
                size = HeapPush(hz, hi, size, z[c], c);
            }
        }
        int head = 0;
        int tail = 0;
        while (size > 0 || head < tail)
        {
            long c;
            if (head < tail)
            {
                c = pit[head];
                head++;
                if (head == tail)
                {
                    head = 0;
                    tail = 0;
                }
            }
            else
            {
                c = hi[0];
                size = HeapPop(hz, hi, size);
            }
            double zc = output[c];
            for (int s = 0; s < nSlot; s++)
            {
                int k = nbr[(c * nSlot) + s];
                if (k < 0 || visited[k])
                {
                    continue;
                }
                visited[k] = true;
                nVisited++;
                if (z[k] <= zc)
                {
                    output[k] = zc;
                    pit[tail] = k;
                    tail++;
                }
                else
                {
                    output[k] = z[k];
                    size = HeapPush(hz, hi, size, z[k], k);
                }
            }
        }
        return nVisited;
    }

    /// <summary>ε 기울기를 준 priority-flood (순수 힙, _fill_epsilon_kernel). 방문한 칸 수를 돌려줍니다.</summary>
    internal static int FillEpsilonKernel(double[] z, int[] nbr, int nSlot, bool[] isOutlet, double eps, double[] output)
    {
        int n = z.Length;
        bool[] visited = new bool[n];
        double[] hz = new double[n];
        long[] hi = new long[n];
        int size = 0;
        int nVisited = 0;
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c])
            {
                output[c] = z[c];
                visited[c] = true;
                nVisited++;
                size = HeapPush(hz, hi, size, z[c], c);
            }
        }
        while (size > 0)
        {
            long c = hi[0];
            size = HeapPop(hz, hi, size);
            double zmin = output[c] + eps;
            for (int s = 0; s < nSlot; s++)
            {
                int k = nbr[(c * nSlot) + s];
                if (k < 0 || visited[k])
                {
                    continue;
                }
                visited[k] = true;
                nVisited++;
                double zk = z[k] > zmin ? z[k] : zmin;
                output[k] = zk;
                size = HeapPush(hz, hi, size, zk, k);
            }
        }
        return nVisited;
    }

    /// <summary>입력 검사 (_check_inputs). 반환: 이웃 슬롯 수 K.</summary>
    internal static int CheckInputs(double[] z, int[] nbr, bool[] isOutlet)
    {
        int n = z.Length;
        if (n == 0 ? nbr.Length != 0 : nbr.Length % n != 0)
        {
            throw new ArgumentException($"nbr 는 (N, K) 이어야 합니다: z ({n},), nbr 원소 {nbr.Length}");
        }
        int nSlot = n == 0 ? 0 : nbr.Length / n;
        foreach (int k in nbr)
        {
            if (k < -1 || k >= n)
            {
                throw new ArgumentException("nbr 에 범위를 벗어난 셀 번호가 있습니다 (-1 또는 0..N-1)");
            }
        }
        if (isOutlet.Length != n)
        {
            throw new ArgumentException($"is_outlet 은 (N,) 이어야 합니다: ({isOutlet.Length},)");
        }
        foreach (double v in z)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("z 에 NaN 이나 무한대가 있습니다");
            }
        }
        if (Array.IndexOf(isOutlet, true) < 0)
        {
            throw new ArgumentException("출구(바다) 칸이 하나도 없습니다. 물이 빠져나갈 곳이 필요합니다");
        }
        return nSlot;
    }

    /// <summary>
    /// 웅덩이를 넘침 높이까지 채웁니다 (fill_depressions). z: (N,) 고도 [m]. nbr: (N, K) 행 우선, 없으면 −1.
    /// 반환: 채운 고도 ẑ [m] (ẑ ≥ z, 출구는 ẑ = z). 출구와 이어지지 않은 칸이 있으면 ArgumentException.
    /// </summary>
    public static double[] FillDepressions(double[] z, int[] nbr, bool[] isOutlet)
    {
        int nSlot = CheckInputs(z, nbr, isOutlet);
        double[] output = new double[z.Length];
        int nVisited = FillKernel(z, nbr, nSlot, isOutlet, output);
        if (nVisited != z.Length)
        {
            throw new ArgumentException($"출구와 이어지지 않은 칸이 {z.Length - nVisited}개 있습니다");
        }
        return output;
    }

    /// <summary>
    /// 라우팅용 ε 채움 (fill_epsilon): z̃_c' = max(z_c', z̃_c + ε). eps [m] 은 0 보다 커야 합니다.
    /// </summary>
    public static double[] FillEpsilon(double[] z, int[] nbr, bool[] isOutlet, double eps)
    {
        int nSlot = CheckInputs(z, nbr, isOutlet);
        if (!(double.IsFinite(eps) && eps > 0.0))
        {
            throw new ArgumentException($"eps 는 0 보다 큰 유한한 값이어야 합니다: {eps}");
        }
        double[] output = new double[z.Length];
        int nVisited = FillEpsilonKernel(z, nbr, nSlot, isOutlet, eps, output);
        if (nVisited != z.Length)
        {
            throw new ArgumentException($"출구와 이어지지 않은 칸이 {z.Length - nVisited}개 있습니다");
        }
        return output;
    }
}
