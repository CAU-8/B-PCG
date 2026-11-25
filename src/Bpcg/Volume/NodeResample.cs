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

    /// <summary>
    /// 계단 너머 이웃을 가르는 배수 (SurfaceToCellCenters): 제 칸에서 이웃까지의 기울기가, 이웃 기울기를 완만한 쪽부터 세어
    /// 절반째 값의 이 배보다 가파르면 그 이웃은 계단 너머로 보고 뺍니다. 평평하게 기운 면에서는 가장 가파른 이웃이 절반째의
    /// 약 1.4배라 빠지는 이웃이 없습니다.
    /// </summary>
    public const double StepRatio = 3.0;

    /// <summary>
    /// 계단 너머 이웃을 가르는 기울기의 아래 한계 [m/m] (SurfaceToCellCenters). 거의 평평한 면에서 작은 높이 차까지 계단으로
    /// 보지 않게 합니다. 0.1 은 25 m 칸 사이에서 2.5 m 입니다.
    /// </summary>
    public const double StepMinSlope = 0.1;

    /// <summary>
    /// 흔든 격자점에서 정한 높이 v (N,) [m] 를 칸 중심의 높이로 옮깁니다. 지표 말고 땅속의 면(지하수면, 지층 바닥)에 씁니다.
    /// 계산은 <see cref="ToCellCenters"/> 와 같습니다: 둘레 이웃으로 그 면의 기울기를 구해, 옮긴 거리만큼 높이를 되돌립니다.
    /// </summary>
    /// <remarks>
    /// 땅속의 면은 지표와 달리 계단처럼 끊기는 자리가 있습니다. 예를 들어 지하수면은 석회암 땅과 다른 암석의 땅 사이, 두 강
    /// 사이 분수령 밑에서 수십~수백 m 끊깁니다. 계단 너머 이웃까지 넣어 기울기를 구하면 기울기가 크게 틀려, 칸 하나가 계단
    /// 높이만큼 움직입니다 (laptop 시드 0 지하수면에서 가장 크게 187 m). 그래서 다섯 가지를 더 지킵니다.
    /// 1. 값이 유한하지 않은 칸(그 면이 없는 칸)은 그대로 두고, 그런 이웃은 기울기 계산에서 뺍니다.
    /// 2. sameSurface(c, k) 가 거짓인 이웃도 뺍니다. 예: 지층 순서가 다른 이웃의 층 바닥은 다른 면이라 섞지 않습니다.
    /// 3. 계단 너머 이웃을 뺍니다. 이웃마다 (값 차이 ÷ 거리)를 재고, 완만한 쪽부터 세어 절반째 값(쓸 이웃이 여덟이면 넷째)의
    ///    StepRatio 배와 StepMinSlope 가운데 큰 값보다 가파른 이웃을 뺍니다.
    ///    예: 지하수면이 거의 평평한 칸(기울기 0.002)의 이웃 셋이 190 m 높은 분수령 너머에 있으면, 그 셋은 25 m 에 190 m 를
    ///    오르는 기울기 7.6 이라 빠지고, 나머지 다섯으로 기울기를 구합니다.
    /// 4. 구한 기울기가 maxSlope 보다 가파르면 그 면이 끊긴 자리로 보고 그대로 둡니다. 제 칸이 계단의 좁은 쪽에 있어 이웃
    ///    대부분이 건너편이면 3번으로 거르지 못하는데, 이때 기울기가 이 한계를 넘습니다.
    /// 5. 옮긴 값은 제 칸과 쓴 이웃 값들의 가장 작은 값과 가장 큰 값 사이로 자릅니다. 칸 중심은 둘레 여덟 이웃의 점들로
    ///    둘러싸여 있어, 평평하게 기운 면이면 칸 중심의 값이 늘 이 범위 안에 듭니다.
    /// 예: 동쪽으로 0.5 기운 지층 바닥에서 대표점이 칸 중심보다 동쪽으로 10 m 가 있으면, 칸 중심의 층 바닥은 5 m 낮습니다.
    /// keep 이 참인 칸(물 칸 등)은 그대로 둡니다. 칸마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
    /// </remarks>
    public static double[] SurfaceToCellCenters(
        CellGraph graph, double[] v, bool[]? keep = null, Func<int, int, bool>? sameSurface = null,
        double maxSlope = double.PositiveInfinity)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException($"칸 중심으로 옮기기는 평면 그래프에서만 합니다. 받은 그래프: '{graph.Kind}'");
        }
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        int n = ny * nx;
        if (v.Length != n || (keep is not null && keep.Length != n))
        {
            throw new ArgumentException(
                $"v 와 keep 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: v {v.Length}, keep {keep?.Length.ToString() ?? "없음"}");
        }
        if (!(maxSlope > 0.0))
        {
            throw new ArgumentException($"maxSlope 는 0 보다 커야 합니다. 받은 값: {maxSlope}");
        }
        const int nSlots = CellGraph.NSlots;
        double dx = graph.Spacing;
        (double east, double north) = graph.Origin;
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        double[] output = new double[n];
        Parallelism.For(0, n, c =>
        {
            double vc = v[c];
            output[c] = vc;
            if ((keep is not null && keep[c]) || !double.IsFinite(vc))
            {
                return;
            }
            double xc = pos[c * 3];
            double yc = pos[(c * 3) + 1];
            double ox = xc - (east + (((c % nx) + 0.5) * dx));
            double oy = yc - (north - (((c / nx) + 0.5) * dx));
            if (ox == 0.0 && oy == 0.0)
            {
                return;
            }
            // 쓸 수 있는 이웃과, 제 칸에서 그 이웃까지의 기울기 (값 차이 ÷ 거리).
            Span<int> use = stackalloc int[nSlots];
            Span<double> steep = stackalloc double[nSlots];
            int m = 0;
            for (int s = 0; s < nSlots; s++)
            {
                int k = nbr[(c * nSlots) + s];
                if (k < 0 || !double.IsFinite(v[k]) || (sameSurface is not null && !sameSurface(c, k)))
                {
                    continue;
                }
                double ddx = pos[k * 3] - xc;
                double ddy = pos[(k * 3) + 1] - yc;
                use[m] = k;
                steep[m] = Math.Abs(v[k] - vc) / Math.Sqrt((ddx * ddx) + (ddy * ddy));
                m++;
            }
            if (m == 0)
            {
                return;
            }
            Span<double> sorted = stackalloc double[m];
            steep[..m].CopyTo(sorted);
            sorted.Sort();
            double limit = Math.Max(StepRatio * sorted[(m - 1) / 2], StepMinSlope);
            double sxx = 0.0;
            double sxy = 0.0;
            double syy = 0.0;
            double bx = 0.0;
            double by = 0.0;
            double lo = vc;
            double hi = vc;
            for (int q = 0; q < m; q++)
            {
                if (steep[q] > limit)
                {
                    continue;
                }
                int k = use[q];
                double ddx = pos[k * 3] - xc;
                double ddy = pos[(k * 3) + 1] - yc;
                double dz = v[k] - vc;
                sxx += ddx * ddx;
                sxy += ddx * ddy;
                syy += ddy * ddy;
                bx += ddx * dz;
                by += ddy * dz;
                lo = Math.Min(lo, v[k]);
                hi = Math.Max(hi, v[k]);
            }
            double det = (sxx * syy) - (sxy * sxy);
            // 쓸 이웃이 없거나 한 줄로 늘어서 있으면 기울기를 정할 수 없어 그대로 둡니다.
            if (!(det > 1e-9 * sxx * syy))
            {
                return;
            }
            double gx = ((syy * bx) - (sxy * by)) / det;
            double gy = ((sxx * by) - (sxy * bx)) / det;
            if ((gx * gx) + (gy * gy) > maxSlope * maxSlope)
            {
                return;
            }
            output[c] = Math.Clamp(vc - ((gx * ox) + (gy * oy)), lo, hi);
        });
        return output;
    }

    /// <summary>
    /// 산비탈 칸의 높이를 둘레 칸과 섞어 칸 크기의 울퉁불퉁함을 줄입니다. z (ny·nx) [m] 는 칸 중심 높이이고,
    /// keep (ny·nx) 이 참인 칸(물 칸과 물길 칸)은 그대로 둡니다. passes 번 되풀이하며, 0 이면 z 와 같은 값을 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// 왜 필요한가: 솔버는 산비탈 칸의 높이를 '물이 흘러가는 이웃 칸의 높이 + 거리 × 경사'로 쌓습니다. 이웃한 두 칸이
    /// 서로 다른 길로 물을 보내면 두 칸의 높이가 따로 정해져, 칸마다 몇 m 씩 어긋납니다. 지구의 산비탈은 흙이 천천히
    /// 흘러내려 이 크기(25 m)에서는 매끈합니다.
    /// 한 번에 칸 높이를 (4 × 자기 + 2 × 위·아래·왼쪽·오른쪽 칸 + 1 × 대각선 칸) ÷ 16 으로 바꿉니다. 가장자리 칸은
    /// 있는 이웃만으로 같은 비율을 씁니다. 물길 칸은 바꾸지 않으므로 골짜기 선과 강바닥 높이는 그대로입니다.
    /// 섞은 값은 둘레에서 가장 낮은 칸보다 낮아지지 않으므로 새 웅덩이가 생기지 않습니다.
    /// 보기용입니다: 3D 샘플 함수와 엔진 지형만 이 높이를 쓰고, 히어로 묶음의 z_m 은 바뀌지 않습니다.
    /// </remarks>
    public static double[] SmoothHillslopes(int ny, int nx, double[] z, bool[] keep, int passes)
    {
        int n = ny * nx;
        if (z.Length != n || keep.Length != n)
        {
            throw new ArgumentException($"z 와 keep 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: z {z.Length}, keep {keep.Length}");
        }
        if (passes < 0)
        {
            throw new ArgumentException($"섞는 횟수는 0 이상이어야 합니다. 받은 값: {passes}");
        }
        double[] cur = (double[])z.Clone();
        double[] next = new double[n];
        for (int pass = 0; pass < passes; pass++)
        {
            double[] src = cur;
            double[] dst = next;
            Parallelism.For(0, n, c =>
            {
                if (keep[c])
                {
                    dst[c] = src[c];
                    return;
                }
                int j = c / nx;
                int i = c % nx;
                double sum = 0.0;
                double wsum = 0.0;
                for (int dj = -1; dj <= 1; dj++)
                {
                    int jj = j + dj;
                    if (jj < 0 || jj >= ny)
                    {
                        continue;
                    }
                    for (int di = -1; di <= 1; di++)
                    {
                        int ii = i + di;
                        if (ii < 0 || ii >= nx)
                        {
                            continue;
                        }
                        double w = (dj == 0 ? 2.0 : 1.0) * (di == 0 ? 2.0 : 1.0);
                        sum += w * src[(jj * nx) + ii];
                        wsum += w;
                    }
                }
                dst[c] = sum / wsum;
            });
            (cur, next) = (next, cur);
        }
        return cur;
    }
}
