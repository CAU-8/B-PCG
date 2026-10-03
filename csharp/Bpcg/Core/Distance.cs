using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Bpcg.Core;

/// <summary>
/// 가장 가까운 출발 칸까지의 거리: 여러 출발점 다익스트라 (src/bpcg/core/distance.py, docs/pipeline.md 3장).
/// </summary>
/// <remarks>
/// 힙의 키는 출발 칸까지의 현 길이 제곱이고, 같은 키면 셀 번호로 순서를 정합니다. 꺼내는 순서가 결과를
/// 정하므로 numba 의 이진 힙을 그대로 옮겼습니다(<c>PriorityQueue</c> 를 쓰지 않음, docs/csharp_port.md 6장 core ②).
/// </remarks>
public static class Distance
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HeapLess(double[] hkey, long[] hcell, int a, int b)
    {
        if (hkey[a] < hkey[b])
        {
            return true;
        }
        if (hkey[a] > hkey[b])
        {
            return false;
        }
        return hcell[a] < hcell[b];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapSwap(double[] hkey, long[] hcell, long[] hsrc, int a, int b)
    {
        (hkey[a], hkey[b]) = (hkey[b], hkey[a]);
        (hcell[a], hcell[b]) = (hcell[b], hcell[a]);
        (hsrc[a], hsrc[b]) = (hsrc[b], hsrc[a]);
    }

    private static void SiftUp(double[] hkey, long[] hcell, long[] hsrc, int k)
    {
        while (k > 0)
        {
            int parent = (k - 1) >> 1;
            if (HeapLess(hkey, hcell, k, parent))
            {
                HeapSwap(hkey, hcell, hsrc, k, parent);
                k = parent;
            }
            else
            {
                break;
            }
        }
    }

    private static void SiftDown(double[] hkey, long[] hcell, long[] hsrc, int size)
    {
        int k = 0;
        while (true)
        {
            int left = (2 * k) + 1;
            if (left >= size)
            {
                break;
            }
            int best = left;
            int right = left + 1;
            if (right < size && HeapLess(hkey, hcell, right, left))
            {
                best = right;
            }
            if (HeapLess(hkey, hcell, best, k))
            {
                HeapSwap(hkey, hcell, hsrc, k, best);
                k = best;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// 벡터 전파 다익스트라 (_propagate_sources). pos (N,3) [m], nbr (N,K), isSource (N,).
    /// 반환: key (N,) 출발 칸까지 현 길이 제곱 [m²] (닿지 않으면 +∞), src (N,) (없으면 −1).
    /// maxKey 보다 먼 칸에는 값을 주지 않고 거기서 전파를 멈춥니다.
    /// </summary>
    internal static (double[] Key, long[] Src) PropagateSources(
        double[] pos, int[] nbr, int nSlots, bool[] isSource, double maxKey)
    {
        int nCells = pos.Length / 3;
        double[] key = new double[nCells];
        long[] src = new long[nCells];
        Array.Fill(key, double.PositiveInfinity);
        Array.Fill(src, -1L);

        int cap = Math.Max(16, nCells + (nCells / 2));
        double[] hkey = new double[cap];
        long[] hcell = new long[cap];
        long[] hsrc = new long[cap];
        int size = 0;
        // 출발 칸은 키 0, 셀 번호 순으로 넣으므로 그대로 힙 순서입니다.
        for (int c = 0; c < nCells; c++)
        {
            if (isSource[c])
            {
                key[c] = 0.0;
                src[c] = c;
                hkey[size] = 0.0;
                hcell[size] = c;
                hsrc[size] = c;
                size++;
            }
        }

        while (size > 0)
        {
            double k0 = hkey[0];
            long c = hcell[0];
            long s = hsrc[0];
            size--;
            if (size > 0)
            {
                hkey[0] = hkey[size];
                hcell[0] = hcell[size];
                hsrc[0] = hsrc[size];
                SiftDown(hkey, hcell, hsrc, size);
            }
            // 그 사이 더 가까운 출발 칸으로 바뀐 칸이면 낡은 원소이므로 건너뜁니다.
            if (k0 != key[c] || s != src[c])
            {
                continue;
            }
            double sx = pos[s * 3];
            double sy = pos[(s * 3) + 1];
            double sz = pos[(s * 3) + 2];
            for (int slot = 0; slot < nSlots; slot++)
            {
                int v = nbr[(c * nSlots) + slot];
                if (v < 0)
                {
                    continue;
                }
                double dx = pos[v * 3] - sx;
                double dy = pos[(v * 3) + 1] - sy;
                double dz = pos[(v * 3) + 2] - sz;
                double d2 = (dx * dx) + (dy * dy) + (dz * dz);
                if (d2 > maxKey)
                {
                    continue;
                }
                if (d2 < key[v] || (d2 == key[v] && s < src[v]))
                {
                    key[v] = d2;
                    src[v] = s;
                    if (size == cap)
                    {
                        cap *= 2;
                        Array.Resize(ref hkey, cap);
                        Array.Resize(ref hcell, cap);
                        Array.Resize(ref hsrc, cap);
                    }
                    hkey[size] = d2;
                    hcell[size] = v;
                    hsrc[size] = s;
                    SiftUp(hkey, hcell, hsrc, size);
                    size++;
                }
            }
        }
        return (key, src);
    }

    /// <summary>
    /// 정해진 출발 칸까지 정확한 거리 [m] (_final_distance): 구면 R·atan2(|a×b|, a·b), 평면 유클리드.
    /// maxDist 를 넘으면 +∞. numba prange 처럼 칸마다 나눕니다.
    /// </summary>
    internal static double[] FinalDistance(double[] pos, long[] src, double radius, bool isSphere, double maxDist)
    {
        int nCells = pos.Length / 3;
        double[] output = new double[nCells];
        Parallel.For(0, nCells, c =>
        {
            long s = src[c];
            if (s < 0)
            {
                output[c] = double.PositiveInfinity;
                return;
            }
            double ax = pos[c * 3];
            double ay = pos[(c * 3) + 1];
            double az = pos[(c * 3) + 2];
            double bx = pos[s * 3];
            double by = pos[(s * 3) + 1];
            double bz = pos[(s * 3) + 2];
            double d;
            if (isSphere)
            {
                double cx = (ay * bz) - (az * by);
                double cy = (az * bx) - (ax * bz);
                double cz = (ax * by) - (ay * bx);
                double cross = Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz));
                double dot = (ax * bx) + (ay * by) + (az * bz);
                d = radius * Math.Atan2(cross, dot);
            }
            else
            {
                double dx = ax - bx;
                double dy = ay - by;
                double dz = az - bz;
                d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }
            output[c] = d <= maxDist ? d : double.PositiveInfinity;
        });
        return output;
    }

    private static void CheckInputs(CellGraph graph, bool[] isSource, double maxDist)
    {
        if (graph.Kind == "sphere" && !(graph.R is double r && r > 0))
        {
            throw new ArgumentException("구면 그래프에는 반지름 R 이 있어야 합니다");
        }
        if (isSource.Length != graph.NCells)
        {
            throw new ArgumentException(
                $"is_source 모양이 그래프와 다릅니다: ({isSource.Length},), 기대 ({graph.NCells},)");
        }
        if (double.IsNaN(maxDist) || maxDist < 0)
        {
            throw new ArgumentException($"max_dist 는 0 이상이어야 합니다: {maxDist}");
        }
    }

    /// <summary>거리 상한 [m] → 힙 키(현 길이 제곱) 상한. 반올림으로 경계 칸을 잃지 않게 조금 넉넉히 둡니다.</summary>
    internal static double MaxKey(CellGraph graph, double maxDist)
    {
        if (!double.IsFinite(maxDist))
        {
            return double.PositiveInfinity;
        }
        if (graph.Kind == "sphere")
        {
            double radius = graph.R!.Value;
            if (maxDist >= Math.PI * radius)
            {
                return double.PositiveInfinity;
            }
            double chord = 2.0 * radius * Math.Sin(0.5 * maxDist / radius);
            return chord * chord * (1.0 + 1e-9);
        }
        return maxDist * maxDist * (1.0 + 1e-9);
    }

    /// <summary>
    /// 칸마다 가장 가까운 출발 칸과 그 거리 (nearest_source; 구면 대원 거리, 평면 유클리드 거리).
    /// 출발 칸이 없거나 닿지 않거나 maxDist 를 넘으면 dist = +∞, src = −1. 거리가 같으면 번호가 작은 출발 칸.
    /// </summary>
    public static (double[] Dist, long[] Src) NearestSource(
        CellGraph graph, bool[] isSource, double maxDist = double.PositiveInfinity)
    {
        CheckInputs(graph, isSource, maxDist);
        bool isSphere = graph.Kind == "sphere";
        double radius = isSphere ? graph.R!.Value : 0.0;
        double maxKey = MaxKey(graph, maxDist);
        (_, long[] src) = PropagateSources(graph.Pos, graph.Nbr, CellGraph.NSlots, isSource, maxKey);
        double[] dist = FinalDistance(graph.Pos, src, radius, isSphere, maxDist);
        for (int c = 0; c < src.Length; c++)
        {
            if (!double.IsFinite(dist[c]))
            {
                src[c] = -1;
            }
        }
        return (dist, src);
    }

    /// <summary>
    /// nearest_source 에 더해 가장 가까운 출발 칸의 값을 넘겨받습니다 (nearest_source_values).
    /// values 는 (N, width) 행 우선. 출발 칸이 없는 칸의 값은 실수면 NaN, 부호 있는 정수면 −1, 그 밖은 0(false).
    /// </summary>
    public static (double[] Dist, long[] Src, T[] Values) NearestSourceValues<T>(
        CellGraph graph, bool[] isSource, T[] values, int width = 1, double maxDist = double.PositiveInfinity)
    {
        if (width < 1 || values.Length != graph.NCells * width)
        {
            throw new ArgumentException(
                $"values 의 첫 축 길이가 칸 수와 같아야 합니다: {values.Length / Math.Max(width, 1)}, 칸 수 {graph.NCells}");
        }
        (double[] dist, long[] src) = NearestSource(graph, isSource, maxDist);
        T fill = FillValue<T>();
        T[] output = new T[values.Length];
        for (int c = 0; c < src.Length; c++)
        {
            long s = src[c];
            for (int k = 0; k < width; k++)
            {
                output[(c * width) + k] = s >= 0 ? values[(s * width) + k] : fill;
            }
        }
        return (dist, src, output);
    }

    private static T FillValue<T>()
    {
        object v = typeof(T) switch
        {
            Type t when t == typeof(double) => double.NaN,
            Type t when t == typeof(float) => float.NaN,
            Type t when t == typeof(long) => -1L,
            Type t when t == typeof(int) => -1,
            Type t when t == typeof(short) => (short)-1,
            Type t when t == typeof(sbyte) => (sbyte)-1,
            _ => default(T)!,
        };
        return (T)v;
    }
}
