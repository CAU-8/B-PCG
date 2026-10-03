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
