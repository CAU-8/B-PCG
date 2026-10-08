using System;
using System.Threading.Tasks;
using Bpcg.Core;

namespace Bpcg.Figures;

/// <summary>
/// 큐브스피어 셀 값 → 평면 그림 픽셀의 셀 번호 (render_results.py 의 equirect·orthographic).
/// </summary>
/// <remarks>
/// Python 은 필드마다 cell_of 를 다시 불렀지만, 픽셀 → 셀 대응은 필드와 무관하므로 한 번만 구하고 <see cref="Sample"/> 로 값을 읽습니다.
/// </remarks>
internal static class Projections
{
    /// <summary>위경도 그림 (H = W/2, W): 경도 −180~180, 위도 90~−90 의 픽셀 중심 셀 번호 (H·W).</summary>
    public static (long[] Cells, int H, int W) Equirect(int n, int width = 2400)
    {
        int h = width / 2;
        double[] p = new double[h * width * 3];
        Parallel.For(0, h, r =>
        {
            double lat = (Math.PI / 2) - (Math.PI * r / h) - (Math.PI / (2 * h));
            for (int c = 0; c < width; c++)
            {
                double lon = -Math.PI + (2 * Math.PI * c / width) + (Math.PI / width);
                int k = ((r * width) + c) * 3;
                p[k] = Math.Cos(lat) * Math.Cos(lon);
                p[k + 1] = Math.Cos(lat) * Math.Sin(lon);
                p[k + 2] = Math.Sin(lat);
            }
        });
        return (Cubesphere.CellOf(p, n), h, width);
    }

    /// <summary>
    /// 정사영 지구본 (size, size): 방향 center 에서 본 반구의 셀 번호와 원 안 여부. 0번 행이 위(up 쪽)입니다.
    /// </summary>
    public static (long[] Cells, bool[] Inside) Orthographic(int n, double[] center, int size = 900, double[]? up = null)
    {
        double[] c = Normalized(center);
        double[] u = up is null ? [0.0, 0.0, 1.0] : (double[])up.Clone();
        double uc = Dot(u, c);
        for (int k = 0; k < 3; k++)
        {
            u[k] -= uc * c[k];
        }
        if (Norm(u) < 1e-6)
        {
            u = [1.0 - (c[0] * c[0]), -c[0] * c[1], -c[0] * c[2]];
        }
        u = Normalized(u);
        double[] right = [(u[1] * c[2]) - (u[2] * c[1]), (u[2] * c[0]) - (u[0] * c[2]), (u[0] * c[1]) - (u[1] * c[0])];
        double[] p = new double[size * size * 3];
        bool[] inside = new bool[size * size];
        for (int r = 0; r < size; r++)
        {
            double y = -(-1.0 + (2.0 * r / (size - 1)));
            for (int col = 0; col < size; col++)
            {
                double x = -1.0 + (2.0 * col / (size - 1));
                double rr = (x * x) + (y * y);
                int idx = (r * size) + col;
                inside[idx] = rr < 1;
                double z = Math.Sqrt(Math.Clamp(1 - rr, 0, 1));
                for (int k = 0; k < 3; k++)
                {
                    p[(idx * 3) + k] = (x * right[k]) + (y * u[k]) + (z * c[k]);
                }
            }
        }
        return (Cubesphere.CellOf(p, n), inside);
    }

    /// <summary>field[cells] 를 실수로 (H·W).</summary>
    public static double[] Sample(double[] field, long[] cells)
    {
        double[] o = new double[cells.Length];
        for (int k = 0; k < cells.Length; k++)
        {
            o[k] = field[cells[k]];
        }
        return o;
    }

    /// <summary>셀 번호 → 면 번호 (c // n²).</summary>
    public static int[] Faces(long[] cells, int n)
    {
        int[] f = new int[cells.Length];
        long nn = (long)n * n;
        for (int k = 0; k < cells.Length; k++)
        {
            f[k] = (int)(cells[k] / nn);
        }
        return f;
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));

    private static double[] Normalized(double[] a)
    {
        double l = Norm(a);
        return [a[0] / l, a[1] / l, a[2] / l];
    }
}
