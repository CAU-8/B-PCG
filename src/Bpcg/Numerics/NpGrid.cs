namespace Bpcg.Numerics;

/// <summary>numpy 격자 함수(<c>linspace</c> 등)를 같은 식으로 옮긴 것.</summary>
public static class NpGrid
{
    /// <summary>
    /// np.linspace(start, stop, num) (endpoint=True, float64): y[k] = k·step + start,
    /// step = (stop − start)/(num − 1), 마지막 원소만 stop 으로 덮습니다.
    /// </summary>
    public static double[] Linspace(double start, double stop, int num)
    {
        double[] y = new double[num];
        if (num == 0)
        {
            return y;
        }
        int div = num - 1;
        double delta = stop - start;
        if (div > 0)
        {
            double step = delta / div;
            if (step == 0.0)
            {
                for (int k = 0; k < num; k++)
                {
                    y[k] = ((k / (double)div) * delta) + start;
                }
            }
            else
            {
                for (int k = 0; k < num; k++)
                {
                    y[k] = (k * step) + start;
                }
            }
            y[num - 1] = stop;
        }
        else
        {
            y[0] = (0.0 * delta) + start;
        }
        return y;
    }
}
