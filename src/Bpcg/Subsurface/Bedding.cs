using System;
using Bpcg.Core;

namespace Bpcg.Subsurface;

/// <summary>
/// 동굴 층이 놓인 지층의 층리면(지층이 쌓인 면) 높이와, 그 면이 기운 정도.
/// </summary>
/// <remarks>
/// 왜 필요한가: 아래층 동굴 통로는 층리면을 따라 물 높이 아래로 오르내립니다. 오르내리는 폭은 층리면이 기운 정도로 정합니다
/// (CaveNetwork.LoopDepth). 그래서 기운 정도가 칸마다 흔들리면 폭도 칸마다 흔들려, 통로가 칸마다 위아래로 꺾입니다.
/// 지층 기둥(층마다 바닥 높이)은 칸 정중앙이 아니라 칸의 대표점에 있습니다. 대표점은 강이 격자 줄을 따라 곧게 뻗지 않도록
/// 칸 중심에서 최대 반 칸 옮긴 점입니다(노드 흔들기). 그래서 기울기는 둘레 여덟 이웃의 대표점 위치와 바닥 높이에 가장 가깝게
/// 맞춘 평면(최소제곱 평면)으로 잽니다. NodeResample.ToCellCenters 가 지표 기울기를 구하는 방법과 같습니다.
/// 높이 차를 칸 간격으로 나누면 틀립니다. 예를 들어 25 m 칸에서 서쪽 이웃의 대표점이 칸 중심보다 서쪽으로 10 m, 동쪽 이웃의
/// 대표점이 동쪽으로 10 m 옮겨져 있으면 두 점은 50 m 가 아니라 70 m 떨어져 있습니다. 높이 차를 50 m 로 나누면 기울기가
/// 1.4배로 나옵니다.
/// 칸마다 제 자리와 이웃만 읽으므로 나눠 돌려도 결과가 같습니다.
/// </remarks>
public static class Bedding
{
    /// <summary>
    /// 칸마다 높이 level 이 든 지층의 바닥면을 층리면으로 보고, 그 높이 [m] 와 기운 각의 sin 을 돌려줍니다.
    /// level 이 맨 아래 기반암 안이면 기반암의 윗면(마지막 층의 바닥)을 씁니다.
    /// strataBottom: (N·nLayers) 칸 c 의 층 i 바닥 높이 [m] (index c·nLayers + i, 위층부터). level: (N,) 동굴 층 높이 [m], 없는 칸은 NaN.
    /// 반환: BedZ (N,) 층리면 높이 [m] (level 이나 바닥이 없는 칸은 NaN), DipSin (N,) 기운 각의 sin (기울기를 정할 수 없으면 0).
    /// 기울기 g 는 이웃 대표점들의 (위치 차, 같은 층 바닥의 높이 차) 에 가장 가깝게 맞춘 평면의 기울기이고, sin = |g| ÷ sqrt(1 + |g|²) 입니다.
    /// 예: 동쪽으로 100 m 에 20 m 씩 내려가는 면은 |g| = 0.2, sin ≈ 0.196 (약 11.3°).
    /// </summary>
    public static (double[] BedZ, double[] DipSin) AtLevel(CellGraph graph, double[] strataBottom, int nLayers, double[] level)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException($"층리면 기울기는 평면 그래프에서만 잽니다. 받은 그래프: '{graph.Kind}'");
        }
        int n = graph.NCells;
        if (nLayers < 0 || level.Length != n || strataBottom.Length != n * nLayers)
        {
            throw new ArgumentException(
                $"level 은 칸 수 ({n},), strataBottom 은 (칸 수 × 층 수) = ({n} × {nLayers},) 길이여야 합니다. "
                + $"받은 길이: level {level.Length}, strataBottom {strataBottom.Length}");
        }
        double[] bed = new double[n];
        double[] dip = new double[n];
        Array.Fill(bed, double.NaN);
        if (nLayers == 0)
        {
            return (bed, dip);
        }
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        Parallelism.For(0, n, c =>
        {
            double lv = level[c];
            if (!double.IsFinite(lv))
            {
                return;
            }
            int li = nLayers - 1;
            for (int i = 0; i < nLayers; i++)
            {
                if (strataBottom[(c * nLayers) + i] < lv)
                {
                    li = i;
                    break;
                }
            }
            double b = strataBottom[(c * nLayers) + li];
            if (!double.IsFinite(b))
            {
                return;
            }
            bed[c] = b;
            // 같은 층(li)의 바닥을 이웃 대표점에서 읽어 최소제곱 평면의 기울기 (gx, gy) 를 구합니다: Σ d dᵀ · g = Σ d · Δz.
            double xc = pos[c * 3];
            double yc = pos[(c * 3) + 1];
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
                double bk = strataBottom[(k * nLayers) + li];
                if (!double.IsFinite(bk))
                {
                    continue;
                }
                double ddx = pos[k * 3] - xc;
                double ddy = pos[(k * 3) + 1] - yc;
                double dz = bk - b;
                sxx += ddx * ddx;
                sxy += ddx * ddy;
                syy += ddy * ddy;
                bx += ddx * dz;
                by += ddy * dz;
            }
            double det = (sxx * syy) - (sxy * sxy);
            // 이웃이 없거나 한 줄로 늘어서 있으면 기울기를 정할 수 없어 0 으로 둡니다 (통로가 물 높이를 그대로 따라감).
            if (!(det > 1e-9 * sxx * syy))
            {
                return;
            }
            double gx = ((syy * bx) - (sxy * by)) / det;
            double gy = ((sxx * by) - (sxy * bx)) / det;
            double g2 = (gx * gx) + (gy * gy);
            dip[c] = Math.Sqrt(g2 / (1.0 + g2));
        });
        return (bed, dip);
    }
}
