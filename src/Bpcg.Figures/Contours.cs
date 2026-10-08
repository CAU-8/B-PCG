using System.Collections.Generic;

namespace Bpcg.Figures;

/// <summary>
/// 한 높이의 등고선 선분 (marching squares, ax.contour(img, levels=[level], extent=…) 대체).
/// </summary>
/// <remarks>
/// matplotlib 처럼 픽셀 중심을 격자점으로 보고 extent 의 양 끝에 맞춥니다: X(c) = x0 + (x1 − x0)·c/(W − 1), Y(r) 도 같음.
/// 안장점(대각선만 높은 칸)은 한 방향으로 정해 잇습니다. 선분을 잇지 않고 그대로 돌려줍니다.
/// </remarks>
internal static class Contours
{
    // 칸 번호(왼위 8, 오른위 4, 오른아래 2, 왼아래 1) → 잇는 변 쌍. 변: 0 위, 1 오른, 2 아래, 3 왼.
    private static readonly int[][] Table =
    [
        [], [3, 2], [2, 1], [3, 1], [0, 1], [3, 0, 2, 1], [0, 2], [3, 0],
        [3, 0], [0, 2], [3, 2, 0, 1], [0, 1], [3, 1], [2, 1], [3, 2], [],
    ];

    public static List<(double X0, double Y0, double X1, double Y1)> Segments(
        double[] v, int h, int w, double level, double x0, double x1, double y0, double y1)
    {
        var o = new List<(double, double, double, double)>();
        double sx = w > 1 ? (x1 - x0) / (w - 1) : 0, sy = h > 1 ? (y1 - y0) / (h - 1) : 0;
        for (int r = 0; r + 1 < h; r++)
        {
            for (int c = 0; c + 1 < w; c++)
            {
                double tl = v[(r * w) + c], tr = v[(r * w) + c + 1];
                double br = v[((r + 1) * w) + c + 1], bl = v[((r + 1) * w) + c];
                int k = (tl > level ? 8 : 0) | (tr > level ? 4 : 0) | (br > level ? 2 : 0) | (bl > level ? 1 : 0);
                int[] e = Table[k];
                for (int s = 0; s < e.Length; s += 2)
                {
                    (double ra, double ca) = Edge(e[s], r, c, tl, tr, br, bl, level);
                    (double rb, double cb) = Edge(e[s + 1], r, c, tl, tr, br, bl, level);
                    o.Add((x0 + (ca * sx), y0 + (ra * sy), x0 + (cb * sx), y0 + (rb * sy)));
                }
            }
        }
        return o;
    }

    private static (double R, double C) Edge(int edge, int r, int c, double tl, double tr, double br, double bl, double level)
    {
        static double T(double a, double b, double level) => b == a ? 0.5 : (level - a) / (b - a);
        return edge switch
        {
            0 => (r, c + T(tl, tr, level)),
            1 => (r + T(tr, br, level), c + 1),
            2 => (r + 1, c + T(bl, br, level)),
            _ => (r + T(tl, bl, level), c),
        };
    }
}
