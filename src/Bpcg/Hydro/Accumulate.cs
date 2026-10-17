using System;

namespace Bpcg.Hydro;

/// <summary>
/// 유량 누적: Q_c = w_c + Σ_{c'∈D(c)} Q_c' (src/bpcg/hydro/accumulate.py, 가이드 2장 ④).
/// </summary>
/// <remarks>
/// 모듈 함수 accumulate 가 모듈 이름과 같아 정적 클래스에 Module 을 붙였습니다(docs/csharp_port.md 0장 쟁점 6).
/// order 를 거꾸로 돌며 차례로 흩어 더하므로, 순서는 반드시 <see cref="Routing.TopoOrder"/> 의 출력을 씁니다.
/// </remarks>
public static class AccumulateModule
{
    /// <summary>acc 에 가중치를 넣어 두고 부르면 그 자리에서 누적합니다 (_accumulate_kernel).</summary>
    internal static void AccumulateKernel(long[] rcv, long[] order, double[] acc)
    {
        for (int q = order.Length - 1; q >= 0; q--)
        {
            long c = order[q];
            long r = rcv[c];
            if (r != c)
            {
                acc[r] += acc[c];
            }
        }
    }

    /// <summary><see cref="AccumulateKernel(long[], long[], double[])"/> 의 int 색인 판 (solver 내부 배열용).</summary>
    internal static void AccumulateKernel(int[] rcv, int[] order, double[] acc)
    {
        for (int q = order.Length - 1; q >= 0; q--)
        {
            int c = order[q];
            int r = rcv[c];
            if (r != c)
            {
                acc[r] += acc[c];
            }
        }
    }

    /// <summary>
    /// Solver 전용 누적: 가중치 세 개를 기존 버퍼에 복사해 한 번의 역순 순회로 함께 누적합니다.
    /// 필드마다 더하는 순서는 <see cref="AccumulateKernel(int[], int[], double[])"/> 를 세 번 부른 것과 같아 결과가
    /// 비트까지 같고, 순서·수신 셀은 한 번만 읽으며 서로 독립인 덧셈 사슬 셋이 함께 돕니다.
    /// </summary>
    internal static void Accumulate3IntoTrusted(int[] rcv, int[] order,
        double[] w1, double[] a1, double[] w2, double[] a2, double[] w3, double[] a3)
    {
        Array.Copy(w1, a1, w1.Length);
        Array.Copy(w2, a2, w2.Length);
        Array.Copy(w3, a3, w3.Length);
        for (int q = order.Length - 1; q >= 0; q--)
        {
            int c = order[q];
            int r = rcv[c];
            if (r != c)
            {
                a1[r] += a1[c];
                a2[r] += a2[c];
                a3[r] += a3[c];
            }
        }
    }

    private static void CheckOrder(long[] order, int n)
    {
        if (order.Length != n)
        {
            throw new ArgumentException($"order 는 (N,) 정수 배열이어야 합니다: ({order.Length},)");
        }
        foreach (long o in order)
        {
            if (o < 0 || o >= n)
            {
                throw new ArgumentException("order 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)");
            }
        }
    }

    /// <summary>
    /// 상류부터 가중치를 모읍니다 (accumulate): acc_c = weight_c + Σ_{기여 셀} acc. weight 는 (N,) (복사해서 씀).
    /// </summary>
    public static double[] Accumulate(long[] rcv, long[] order, double[] weight)
    {
        Routing.CheckRcv(rcv);
        CheckOrder(order, rcv.Length);
        if (weight.Length != rcv.Length)
        {
            throw new ArgumentException($"weight 는 (N,) 또는 스칼라여야 합니다: ({weight.Length},)");
        }
        double[] acc = (double[])weight.Clone();
        AccumulateKernel(rcv, order, acc);
        return acc;
    }

    /// <summary>accumulate (스칼라 가중치).</summary>
    public static double[] Accumulate(long[] rcv, long[] order, double weight)
    {
        Routing.CheckRcv(rcv);
        CheckOrder(order, rcv.Length);
        double[] acc = new double[rcv.Length];
        Array.Fill(acc, weight);
        AccumulateKernel(rcv, order, acc);
        return acc;
    }

    /// <summary>칸마다 물을 보내 오는 기여 셀 수 (donors_count, 자기 자신 제외). 반환: int32.</summary>
    public static int[] DonorsCount(long[] rcv)
    {
        Routing.CheckRcv(rcv);
        int[] cnt = new int[rcv.Length];
        for (int c = 0; c < rcv.Length; c++)
        {
            long r = rcv[c];
            if (r != c)
            {
                cnt[r] += 1;
            }
        }
        return cnt;
    }
}
