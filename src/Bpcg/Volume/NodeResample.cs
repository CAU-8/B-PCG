using System;
using Bpcg.Core;

namespace Bpcg.Volume;

/// <summary>
/// 흔들어 둔 격자점(노드)에서 잰 높이를 칸 중심의 높이로 옮깁니다.
/// </summary>
/// <remarks>
/// 왜 필요한가: 평면 히어로는 격자 줄무늬가 드러나지 않도록 칸마다 대표점을 칸 중심에서 최대 반 칸까지 옮겨 두고
/// (landscape.jitter), 솔버는 그 옮긴 점의 높이를 풉니다. 그런데 3D 샘플 함수와 엔진 높이맵은 값이 칸 정중앙에
/// 있다고 보고 그 사이를 잇습니다. 경사 0.8 인 비탈에서 점이 10 m 옮겨져 있으면 높이가 8 m 어긋나고,
/// 이 어긋남이 칸마다 제각각이라 지표에 칸 크기의 바둑판 무늬가 생깁니다.
/// 고치는 법: 칸마다 둘레 여덟 이웃의 (옮긴 위치, 높이)로 지표의 기울기를 구하고(최소제곱 평면),
/// 그 기울기로 옮긴 거리만큼 높이를 되돌립니다: z_중심 = z_점 − 기울기 · (점 위치 − 칸 중심).
/// 예: 기울기가 동쪽으로 0.8 이고 점이 칸 중심보다 동쪽으로 10 m 가 있으면, 중심 높이는 점 높이보다 8 m 낮습니다.
/// 칸마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
/// </remarks>
public static class NodeResample
{
    /// <summary>
    /// 평면 그래프의 노드 높이 z (N,) [m] 를 칸 중심 높이로 옮깁니다. keep (N,) 이 참인 칸(강·호수·바다)은 그대로 둡니다.
    /// 수면 높이와 강바닥 높이가 그 칸의 z 를 기준으로 정해져 있기 때문입니다. 흔들기가 없는 그래프에서는 z 와 같은 값을 돌려줍니다.
    /// </summary>
    public static double[] ToCellCenters(CellGraph graph, double[] z, bool[]? keep = null)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException($"칸 중심으로 옮기기는 평면 그래프에서만 합니다. 받은 그래프: '{graph.Kind}'");
        }
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        int n = ny * nx;
        if (z.Length != n || (keep is not null && keep.Length != n))
        {
            throw new ArgumentException(
                $"z 와 keep 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: z {z.Length}, keep {keep?.Length.ToString() ?? "없음"}");
        }
        double dx = graph.Spacing;
        (double east, double north) = graph.Origin;
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        double[] output = new double[n];
        Parallelism.For(0, n, c =>
        {
            output[c] = z[c];
            if (keep is not null && keep[c])
            {
                return;
            }
            double xc = pos[c * 3];
            double yc = pos[(c * 3) + 1];
            // 옮긴 거리 (점 위치 − 칸 중심). 흔들기가 없으면 0 이라 아래 계산을 건너뜁니다.
            double ox = xc - (east + (((c % nx) + 0.5) * dx));
            double oy = yc - (north - (((c / nx) + 0.5) * dx));
            if (ox == 0.0 && oy == 0.0)
            {
                return;
            }
            // 둘레 이웃으로 최소제곱 평면의 기울기 (gx, gy) 를 구합니다: Σ d dᵀ · g = Σ d · Δz.
            double sxx = 0.0;
            double sxy = 0.0;
            double syy = 0.0;
            double bx = 0.0;
            double by = 0.0;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int k = nbr[(c * CellGraph.NSlots) + s];
                if (k < 0)
                {
                    continue;
                }
                double ddx = pos[k * 3] - xc;
                double ddy = pos[(k * 3) + 1] - yc;
                double dz = z[k] - z[c];
                sxx += ddx * ddx;
                sxy += ddx * ddy;
                syy += ddy * ddy;
                bx += ddx * dz;
                by += ddy * dz;
            }
            double det = (sxx * syy) - (sxy * sxy);
            // 이웃이 한 줄로 늘어서 있으면(모서리 칸) 기울기를 정할 수 없어 그대로 둡니다.
            if (!(det > 1e-9 * sxx * syy))
            {
                return;
            }
            double gx = ((syy * bx) - (sxy * by)) / det;
            double gy = ((sxx * by) - (sxy * bx)) / det;
            output[c] = z[c] - ((gx * ox) + (gy * oy));
        });
        return output;
    }
}
