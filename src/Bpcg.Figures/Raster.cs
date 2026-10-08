using System;

namespace Bpcg.Figures;

/// <summary>
/// 그림용 래스터 계산: matplotlib 의 색표 조회·Normalize·TwoSlopeNorm·LightSource(soft 섞기)를 옮겼습니다.
/// </summary>
/// <remarks>
/// 그림은 matplotlib 판과 눈으로 같으면 됩니다(픽셀 단위로 맞추지 않음). 배열은 모두 행 우선이고 0번 행이 그림 맨 위입니다.
/// RGB 는 (H, W, 3), RGBA 는 (H, W, 4) 이며 값은 [0, 1] 입니다.
/// </remarks>
internal static class Raster
{
    /// <summary>matplotlib Colormap.__call__ (실수 입력): NaN 은 (0, 0, 0, 0), 범위 밖은 끝 색.</summary>
    public static void Lookup(double[] lut, double x, Span<double> rgba)
    {
        int n = lut.Length / 4;
        if (double.IsNaN(x))
        {
            rgba.Clear();
            return;
        }
        double xa = x * n;
        int idx = xa < 0 ? 0 : (xa >= n ? n - 1 : (int)xa);
        lut.AsSpan(idx * 4, 4).CopyTo(rgba);
    }

    /// <summary>plt.Normalize(vmin, vmax): (v − vmin) / (vmax − vmin). vmin = vmax 이면 0.</summary>
    public static double Normalize(double v, double vmin, double vmax) =>
        vmax > vmin ? (v - vmin) / (vmax - vmin) : 0.0;

    /// <summary>TwoSlopeNorm(vmin, vcenter, vmax): [vmin, vcenter] → [0, 0.5], [vcenter, vmax] → [0.5, 1], 밖은 ±∞.</summary>
    public static double TwoSlope(double v, double vmin, double vcenter, double vmax)
    {
        if (double.IsNaN(v))
        {
            return double.NaN;
        }
        if (v < vmin)
        {
            return double.NegativeInfinity;
        }
        if (v > vmax)
        {
            return double.PositiveInfinity;
        }
        return v <= vcenter ? 0.5 * (v - vmin) / (vcenter - vmin) : 0.5 + (0.5 * (v - vcenter) / (vmax - vcenter));
    }

    /// <summary>값 → RGBA (H·W·4): norm 을 거쳐 색표에서 찾습니다. NaN 은 투명.</summary>
    public static double[] Colorize(double[] values, double[] lut, Func<double, double> norm)
    {
        double[] o = new double[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            Lookup(lut, double.IsNaN(values[i]) ? double.NaN : norm(values[i]), o.AsSpan(i * 4, 4));
        }
        return o;
    }

    /// <summary>RGBA → RGB (알파는 버림).</summary>
    public static double[] DropAlpha(double[] rgba)
    {
        int n = rgba.Length / 4;
        double[] o = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            o[i * 3] = rgba[i * 4];
            o[(i * 3) + 1] = rgba[(i * 4) + 1];
            o[(i * 3) + 2] = rgba[(i * 4) + 2];
        }
        return o;
    }

    /// <summary>
    /// LightSource(azdeg, altdeg).shade_rgb(rgb, z, vert_exag, dx, dy, blend_mode="soft", fraction=1):
    /// np.gradient 로 법선을 구하고, 빛 세기를 [0, 1] 로 늘린 뒤 soft light 로 섞습니다. 반환은 새 (H, W, 3).
    /// </summary>
    public static double[] Shade(
        double[] rgb, double[] z, int h, int w, double vertExag, double dx, double dy,
        double azDeg = 315.0, double altDeg = 40.0)
    {
        double az = (90.0 - azDeg) * Math.PI / 180.0;
        double alt = altDeg * Math.PI / 180.0;
        double lx = Math.Cos(az) * Math.Cos(alt), ly = Math.Sin(az) * Math.Cos(alt), lz = Math.Sin(alt);
        double rowStep = -dy; // matplotlib 은 dy = −dy 로 행 방향(남쪽) 기울기 부호를 뒤집습니다
        double[] it = new double[h * w];
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                double ex = Gradient(z, w, r, c, w, 1, dx) * vertExag;
                double ey = Gradient(z, w, r, c, h, 0, rowStep) * vertExag;
                double nx = -ex, ny = -ey, nz = 1.0;
                double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                double v = ((nx * lx) + (ny * ly) + (nz * lz)) / len;
                it[(r * w) + c] = v;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        }
        double[] o = new double[rgb.Length];
        for (int k = 0; k < it.Length; k++)
        {
            double t = it[k];
            if (hi - lo > 1e-6)
            {
                t = (t - lo) / (hi - lo);
            }
            t = Math.Clamp(t, 0.0, 1.0);
            for (int ch = 0; ch < 3; ch++)
            {
                double a = rgb[(k * 3) + ch];
                o[(k * 3) + ch] = (2 * t * a) + ((1 - (2 * t)) * a * a);
            }
        }
        return o;
    }

    /// <summary>np.gradient (edge_order=1): 안쪽은 중앙 차분, 끝은 한쪽 차분. axis 1 = 열, 0 = 행.</summary>
    private static double Gradient(double[] z, int w, int r, int c, int len, int axis, double step)
    {
        int i = axis == 1 ? c : r;
        if (len < 2)
        {
            return 0.0;
        }
        double At(int k) => axis == 1 ? z[(r * w) + k] : z[(k * w) + c];
        if (i == 0)
        {
            return (At(1) - At(0)) / step;
        }
        if (i == len - 1)
        {
            return (At(i) - At(i - 1)) / step;
        }
        return (At(i + 1) - At(i - 1)) / (2 * step);
    }

    /// <summary>face_edges: 오른쪽 또는 아래 이웃과 면 번호가 다른 픽셀 (H·W).</summary>
    public static bool[] FaceEdges(int[] face, int h, int w)
    {
        bool[] e = new bool[h * w];
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int k = (r * w) + c;
                if ((c > 0 && face[k] != face[k - 1]) || (r > 0 && face[k] != face[k - w]))
                {
                    e[k] = true;
                }
            }
        }
        return e;
    }

    /// <summary>mask 가 참인 픽셀을 한 색으로 칠합니다 (RGB, 제자리).</summary>
    public static void Paint(double[] rgb, bool[] mask, (double R, double G, double B) color)
    {
        for (int k = 0; k < mask.Length; k++)
        {
            if (mask[k])
            {
                rgb[k * 3] = color.R;
                rgb[(k * 3) + 1] = color.G;
                rgb[(k * 3) + 2] = color.B;
            }
        }
    }

    /// <summary>NaN 을 건너뛴 최솟값·최댓값 (np.nanmin, np.nanmax).</summary>
    public static (double Min, double Max) NanMinMax(double[] v)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (double x in v)
        {
            if (!double.IsNaN(x))
            {
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
        }
        return (lo, hi);
    }
}
