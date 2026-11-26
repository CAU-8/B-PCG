using System;
using System.Runtime.CompilerServices;
using Bpcg.Core;

namespace Bpcg.NoiseWorld;

/// <summary>
/// 물결 하나하나(옥타브)의 시드·좌표 이동·파장·진폭. 파장이 반씩 줄어드는 물결을 겹칩니다.
/// </summary>
/// <remarks>
/// 정규화 계수(<see cref="Norm"/>)는 물결을 모두 더했을 때의 진폭 합으로 정합니다. 표본 간격이 커서 짧은 물결을 빼더라도
/// 긴 물결의 세기는 그대로입니다. 그래서 행성 지도(칸 약 20 km), 히어로 유역(25 m), 회랑(2 m)이 같은 함수의 서로 다른
/// 해상도가 됩니다. 예: 언덕 노이즈의 첫 물결(파장 40 km)은 세 곳 모두에서 같은 높이를 더합니다.
/// </remarks>
internal sealed class Octaves
{
    // 옥타브마다 좌표를 [0, 64)³ 안에서 옮겨 물결끼리 겹치는 무늬를 없앱니다 (Bpcg.Core.Noise 와 같은 생각).
    private const double OffsetSpan = 64.0;

    public Octaves(long seed, long stream, double lambda0M, int count, double gain)
    {
        if (count < 1)
        {
            throw new ArgumentException($"옥타브 수는 1 이상이어야 합니다 (받은 값 {count})");
        }
        if (!(double.IsFinite(lambda0M) && lambda0M > 0))
        {
            throw new ArgumentException($"노이즈 파장은 0 보다 큰 m 값이어야 합니다 (받은 값 {lambda0M})");
        }
        if (!(double.IsFinite(gain) && gain > 0 && gain < 1))
        {
            throw new ArgumentException($"노이즈 gain 은 0 과 1 사이여야 합니다 (받은 값 {gain}). 0.5 면 물결이 반으로 짧아질 때 세기도 반");
        }
        Seeds = new ulong[count];
        Offsets = new double[count * 3];
        Freq = new double[count];
        Lambda = new double[count];
        Amp = new double[count];
        long s = unchecked((long)Hashing.Hash3(seed, stream, 0));
        double lam = lambda0M;
        double a = 1.0;
        double total = 0.0;
        for (int o = 0; o < count; o++)
        {
            Seeds[o] = Hashing.SplitMix64(Hashing.Hash3(s, o, 1));
            for (int ax = 0; ax < 3; ax++)
            {
                Offsets[(o * 3) + ax] = Hashing.HashUnit(s, o, 10 + ax) * OffsetSpan;
            }
            Lambda[o] = lam;
            Freq[o] = 1.0 / lam;
            Amp[o] = a;
            total += a;
            lam *= 0.5;
            a *= gain;
        }
        Norm = 1.0 / total;
    }

    public ulong[] Seeds { get; }

    public double[] Offsets { get; }

    public double[] Freq { get; }

    public double[] Lambda { get; }

    public double[] Amp { get; }

    public double Norm { get; }

    public int Count => Seeds.Length;

    /// <summary>
    /// 파장이 표본 간격에 비해 얼마나 살아남는지 (0~1). 파장이 간격의 4배 이상이면 1, 2배 이하면 0, 그 사이는 곧게 줄입니다.
    /// 간격보다 짧은 물결을 그대로 두면 칸마다 값이 들쭉날쭉 튀므로(앨리어싱) 뺍니다.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Band(double lambdaM, double spacingM)
    {
        double r = lambdaM / (2.0 * spacingM);
        return r >= 2.0 ? 1.0 : r <= 1.0 ? 0.0 : r - 1.0;
    }

    /// <summary>한 물결의 노이즈 값 (대략 −1~1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double One(int o, double x, double y, double z) =>
        Noise.Noise3Mixed((x * Freq[o]) + Offsets[o * 3], (y * Freq[o]) + Offsets[(o * 3) + 1], (z * Freq[o]) + Offsets[(o * 3) + 2], Seeds[o]);

    /// <summary>
    /// fBm (크기가 반씩 줄어드는 물결을 겹친 노이즈). 값은 대략 −1~1. spacingM 은 표본 간격 [m]: 그보다 짧은 물결은 뺍니다.
    /// </summary>
    public double Fbm(double x, double y, double z, double spacingM)
    {
        double acc = 0.0;
        for (int o = 0; o < Seeds.Length; o++)
        {
            double b = Band(Lambda[o], spacingM);
            if (b <= 0.0)
            {
                break;
            }
            acc += Amp[o] * b * One(o, x, y, z);
        }
        return acc * Norm;
    }

    /// <summary>
    /// ridged 노이즈 (Musgrave 의 ridged multifractal). 값을 뒤집어 0 근처를 날카로운 마루로 만들고, 마루 위에서만 다음 물결을
    /// 세게 더합니다. 값은 0~1 쯤.
    /// </summary>
    public double Ridged(double x, double y, double z, double spacingM)
    {
        double acc = 0.0;
        double weight = 1.0;
        for (int o = 0; o < Seeds.Length; o++)
        {
            double b = Band(Lambda[o], spacingM);
            if (b <= 0.0)
            {
                break;
            }
            double s = 1.0 - Math.Abs(One(o, x, y, z));
            s *= s;
            s *= weight;
            weight = Math.Clamp(s * 2.0, 0.0, 1.0);
            acc += Amp[o] * b * s;
        }
        return acc * Norm;
    }
}

/// <summary>노이즈 비교본이 쓰는 작은 계산 도구.</summary>
internal static class NoiseMath
{
    /// <summary>x 가 e0 에서 e1 로 갈 때 0 에서 1 로 매끈하게 오르는 값 (e0 &gt; e1 이면 반대로 내려감).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    /// <summary>점 (xs, ys) 를 곧게 잇는 꺾은선의 x 위치 값. xs 는 오름차순.</summary>
    public static double Piecewise(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0])
        {
            return ys[0];
        }
        for (int i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + (t * (ys[i] - ys[i - 1]));
            }
        }
        return ys[^1];
    }

    /// <summary>
    /// 노이즈 값의 기울기 크기 × 첫 파장 (가운데값). 노이즈 값을 거리(m)로 바꿀 때 씁니다: 거리 ≈ 값 ÷ (이 값 ÷ 첫 파장).
    /// 행성 위 고정된 점 2,048개에서 짧은 차분으로 잽니다. 점과 방향은 해시로 정하므로 늘 같은 값이 나옵니다.
    /// </summary>
    public static double GradientScale(Octaves o, double radiusM, long seed, long stream)
    {
        const int n = 2048;
        double[] g = new double[n];
        double h = o.Lambda[^1] * 0.01;
        for (int k = 0; k < n; k++)
        {
            double zu = (2.0 * Hashing.HashUnit(seed, k, stream)) - 1.0;
            double ph = 2.0 * Math.PI * Hashing.HashUnit(seed, k, stream + 1);
            double rr = Math.Sqrt(Math.Max(1.0 - (zu * zu), 0.0));
            double x = radiusM * rr * Math.Cos(ph);
            double y = radiusM * rr * Math.Sin(ph);
            double z = radiusM * zu;
            double f0 = o.Fbm(x, y, z, NoiseTerrain.AllOctaves);
            double gx = (o.Fbm(x + h, y, z, NoiseTerrain.AllOctaves) - f0) / h;
            double gy = (o.Fbm(x, y + h, z, NoiseTerrain.AllOctaves) - f0) / h;
            double gz = (o.Fbm(x, y, z + h, NoiseTerrain.AllOctaves) - f0) / h;
            g[k] = Math.Sqrt((gx * gx) + (gy * gy) + (gz * gz)) * o.Lambda[0];
        }
        Array.Sort(g);
        return 0.5 * (g[(n / 2) - 1] + g[n / 2]);
    }

    /// <summary>
    /// 3D 세포 노이즈(Worley F1): 공간을 한 변 cellM 의 정육면체로 나누고 칸마다 해시로 점 하나를 둡니다. 가장 가까운 점의 칸 번호를
    /// 돌려줍니다. 같은 번호의 땅은 한 덩어리(구역)입니다.
    /// </summary>
    public static long NearestCell(double x, double y, double z, double cellM, long seed)
    {
        double ux = x / cellM;
        double uy = y / cellM;
        double uz = z / cellM;
        long ix = (long)Math.Floor(ux);
        long iy = (long)Math.Floor(uy);
        long iz = (long)Math.Floor(uz);
        double best = double.PositiveInfinity;
        long bestId = 0;
        for (long dz = -1; dz <= 1; dz++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                for (long dx = -1; dx <= 1; dx++)
                {
                    long cx = ix + dx;
                    long cy = iy + dy;
                    long cz = iz + dz;
                    long id = unchecked((long)Hashing.Hash3(cx, (cy * 73_856_093L) ^ (cz * 19_349_663L), seed));
                    double px = cx + Hashing.HashUnit(id, 1, seed);
                    double py = cy + Hashing.HashUnit(id, 2, seed);
                    double pz = cz + Hashing.HashUnit(id, 3, seed);
                    double d = ((px - ux) * (px - ux)) + ((py - uy) * (py - uy)) + ((pz - uz) * (pz - uz));
                    if (d < best)
                    {
                        best = d;
                        bestId = id;
                    }
                }
            }
        }
        return bestId;
    }
}
