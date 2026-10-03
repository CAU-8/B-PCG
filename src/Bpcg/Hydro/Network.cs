using System;
using System.Collections.Generic;

namespace Bpcg.Hydro;

/// <summary>유역과 강 구간 (src/bpcg/hydro/network.py, docs/pipeline.md 6장).</summary>
public static class Network
{
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

    /// <summary>칸마다 흘러 나가는 출구 칸 번호 (outlet_of, 유역 번호). 출구는 자기 자신.</summary>
    public static long[] OutletOf(long[] rcv, long[] order)
    {
        Routing.CheckRcv(rcv);
        CheckOrder(order, rcv.Length);
        long[] output = new long[rcv.Length];
        foreach (long c in order)
        {
            long r = rcv[c];
            output[c] = r == c ? c : output[r];
        }
        return output;
    }

    /// <summary>강 구간을 평평한 배열(cells)과 구간 시작 위치(offsets)로 (_segments_kernel).</summary>
    internal static (long[] Cells, long[] Offsets) SegmentsKernel(long[] rcv, long[] order, bool[] isRiver)
    {
        int n = rcv.Length;
        long[] nRdon = new long[n]; // 강 기여 셀 수
        int nRiver = 0;
        for (int c = 0; c < n; c++)
        {
            if (isRiver[c])
            {
                nRiver++;
                long r = rcv[c];
                if (r != c)
                {
                    nRdon[r] += 1;
                }
            }
        }
        long[] cells = new long[nRiver];
        long[] offsets = new long[nRiver + 1];
        int m = 0;
        int nSeg = 0;
        // 구간 머리 = 강 기여 셀 수가 1 이 아닌 강 칸 (0 이면 발원점, 2 이상이면 합류점). 하류부터 적습니다.
        foreach (long h in order)
        {
            if (!isRiver[h] || nRdon[h] == 1)
            {
                continue;
            }
            offsets[nSeg] = m;
            nSeg++;
            long c = h;
            while (true)
            {
                cells[m] = c;
                m++;
                long r = rcv[c];
                if (r == c || !isRiver[r] || nRdon[r] >= 2)
                {
                    break;
                }
                c = r;
            }
        }
        offsets[nSeg] = m;
        return (cells[..m], offsets[..(nSeg + 1)]);
    }

    /// <summary>
    /// 강 칸을 하류 방향으로 이은 구간 목록 (river_segments). 각 구간은 상류 → 하류 칸 번호이고,
    /// 순서는 머리 칸이 order 에 나오는 순서(하류 쪽 구간 먼저)입니다.
    /// </summary>
    public static List<long[]> RiverSegments(long[] rcv, long[] order, bool[] isRiver)
    {
        Routing.CheckRcv(rcv);
        int n = rcv.Length;
        CheckOrder(order, n);
        if (isRiver.Length != n)
        {
            throw new ArgumentException($"is_river 는 (N,) 이어야 합니다: ({isRiver.Length},)");
        }
        (long[] cells, long[] offsets) = SegmentsKernel(rcv, order, isRiver);
        int nRiver = 0;
        foreach (bool b in isRiver)
        {
            if (b)
            {
                nRiver++;
            }
        }
        if (cells.Length != nRiver)
        {
            throw new ArgumentException("강 칸 일부가 구간에 들어가지 않았습니다 (rcv 에 순환이 있는지 확인하세요)");
        }
        var segs = new List<long[]>();
        for (int i = 0; i + 1 < offsets.Length; i++)
        {
            segs.Add(cells[(int)offsets[i]..(int)offsets[i + 1]]);
        }
        return segs;
    }

    /// <summary>강 구간을 CSR (cells, offsets) 로 (bake/bundle 의 rivers.npz 와 같은 꼴).</summary>
    public static (long[] Cells, long[] Offsets) RiverSegmentsCsr(List<long[]> segments)
    {
        int total = 0;
        foreach (long[] s in segments)
        {
            total += s.Length;
        }
        long[] cells = new long[total];
        long[] offsets = new long[segments.Count + 1];
        int m = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            offsets[i] = m;
            Array.Copy(segments[i], 0, cells, m, segments[i].Length);
            m += segments[i].Length;
        }
        offsets[segments.Count] = m;
        return (cells, offsets);
    }
}
