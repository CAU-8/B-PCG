using System;
using System.Runtime.CompilerServices;

namespace Bpcg.Core;

/// <summary>
/// 결정적 3D 그래디언트 노이즈와 fbm (src/bpcg/core/noise.py, 가이드 5장 ξ).
/// </summary>
/// <remarks>
/// 격자 꼭짓점의 기울기는 <see cref="Hashing"/> 의 splitmix64 로 고르고, 보간은 퀸틱 6t⁵ − 15t⁴ + 10t³ 입니다.
/// 식과 결합 순서는 numba 커널과 같고 FMA 를 쓰지 않습니다(numba 도 묶지 않음, docs/csharp_port.md 6장 core ①).
/// 점마다 독립이라 <c>Parallel.For</c> 로 나눠도 스레드 수와 상관없이 결과가 같습니다.
/// </remarks>
public static class Noise
{
    // Perlin 2002 의 12개 기울기 (정육면체 모서리 중점 방향), 행 우선 (12, 3).
    private static readonly double[] Grad3 =
    [
        1.0, 1.0, 0.0,
        -1.0, 1.0, 0.0,
        1.0, -1.0, 0.0,
        -1.0, -1.0, 0.0,
        1.0, 0.0, 1.0,
        -1.0, 0.0, 1.0,
        1.0, 0.0, -1.0,
        -1.0, 0.0, -1.0,
        0.0, 1.0, 1.0,
        0.0, -1.0, 1.0,
        0.0, 1.0, -1.0,
        0.0, -1.0, -1.0,
    ];

    private const long StreamOctave = 201; // 옥타브별 시드
    private const long StreamOffset = 210; // 옥타브별 좌표 이동 (축마다 +0, +1, +2)
    private const long StreamVector = 220; // vector_fbm3 의 성분별 시드
    private const double OffsetSpan = 64.0; // 옥타브마다 좌표를 [0, 64)³ 안에서 옮김

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Fade(double t) => t * t * t * ((t * ((t * 6.0) - 15.0)) + 10.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GradDot(ulong h, double dx, double dy, double dz)
    {
        // 위쪽 32비트에 12를 곱하고 다시 32비트 내리면 0..11 이 거의 고르게 나옵니다.
        int k = (int)(unchecked((h >> 32) * 12UL) >> 32);
        int o = k * 3;
        return (Grad3[o] * dx) + (Grad3[o + 1] * dy) + (Grad3[o + 2] * dz);
    }

    /// <summary>이미 섞은 시드 s 로 3D 그래디언트 노이즈 한 값.</summary>
    private static double Noise3Mixed(double x, double y, double z, ulong s)
    {
        double fx = Math.Floor(x);
        double fy = Math.Floor(y);
        double fz = Math.Floor(z);
        long ix = (long)fx;
        long iy = (long)fy;
        long iz = (long)fz;
        double tx = x - fx;
        double ty = y - fy;
        double tz = z - fz;

        unchecked
        {
            // 꼭짓점 해시를 x → y → z 순으로 섞습니다 (8꼭짓점에 14번).
            ulong hx0 = Hashing.SplitMix64(s ^ (ulong)ix);
            ulong hx1 = Hashing.SplitMix64(s ^ (ulong)(ix + 1));
            ulong hx0y0 = Hashing.SplitMix64(hx0 ^ (ulong)iy);
            ulong hx0y1 = Hashing.SplitMix64(hx0 ^ (ulong)(iy + 1));
            ulong hx1y0 = Hashing.SplitMix64(hx1 ^ (ulong)iy);
            ulong hx1y1 = Hashing.SplitMix64(hx1 ^ (ulong)(iy + 1));
            ulong z0 = (ulong)iz;
            ulong z1 = (ulong)(iz + 1);

            double n000 = GradDot(Hashing.SplitMix64(hx0y0 ^ z0), tx, ty, tz);
            double n100 = GradDot(Hashing.SplitMix64(hx1y0 ^ z0), tx - 1.0, ty, tz);
            double n010 = GradDot(Hashing.SplitMix64(hx0y1 ^ z0), tx, ty - 1.0, tz);
            double n110 = GradDot(Hashing.SplitMix64(hx1y1 ^ z0), tx - 1.0, ty - 1.0, tz);
            double n001 = GradDot(Hashing.SplitMix64(hx0y0 ^ z1), tx, ty, tz - 1.0);
            double n101 = GradDot(Hashing.SplitMix64(hx1y0 ^ z1), tx - 1.0, ty, tz - 1.0);
            double n011 = GradDot(Hashing.SplitMix64(hx0y1 ^ z1), tx, ty - 1.0, tz - 1.0);
            double n111 = GradDot(Hashing.SplitMix64(hx1y1 ^ z1), tx - 1.0, ty - 1.0, tz - 1.0);

            double u = Fade(tx);
            double v = Fade(ty);
            double w = Fade(tz);
            double nx00 = n000 + (u * (n100 - n000));
            double nx10 = n010 + (u * (n110 - n010));
            double nx01 = n001 + (u * (n101 - n001));
            double nx11 = n011 + (u * (n111 - n011));
            double nxy0 = nx00 + (v * (nx10 - nx00));
            double nxy1 = nx01 + (v * (nx11 - nx01));
            return nxy0 + (w * (nxy1 - nxy0));
        }
    }

    /// <summary>
    /// 정수 격자 해시 3D 그래디언트 노이즈 ν(x, y, z) 한 값 (gradient_noise3). 값은 대략 [-1, 1].
    /// </summary>
    public static double GradientNoise3(double x, double y, double z, long seed) =>
        Noise3Mixed(x, y, z, Hashing.SplitMix64(unchecked((ulong)seed)));

    /// <summary>옥타브별 시드·좌표 이동·주파수·진폭과 정규화 계수 1/Σg^o (_octave_tables).</summary>
    internal static (ulong[] Seeds, double[] Offsets, double[] Freqs, double[] Amps, double InvNorm)
        OctaveTables(long seed, int nComp, int octaves, double gain, double lacunarity, double frequency)
    {
        if (octaves < 1)
        {
            throw new ArgumentException($"octaves 는 1 이상의 정수여야 합니다: {octaves}");
        }
        CheckPositive("gain", gain);
        CheckPositive("lacunarity", lacunarity);
        CheckPositive("frequency", frequency);
        ulong[] seeds = new ulong[nComp * octaves];
        double[] offsets = new double[nComp * octaves * 3];
        for (int c = 0; c < nComp; c++)
        {
            // 성분마다 다른 시드 (fbm3 는 성분 하나, 원래 시드 그대로).
            long compSeed = nComp == 1 ? seed : unchecked((long)Hashing.Hash3(seed, c, StreamVector));
            for (int o = 0; o < octaves; o++)
            {
                seeds[(c * octaves) + o] = Hashing.Hash3(compSeed, o, StreamOctave);
                for (int ax = 0; ax < 3; ax++)
                {
                    double u = Hashing.HashUnit(compSeed, o, StreamOffset + ax);
                    offsets[(((c * octaves) + o) * 3) + ax] = u * OffsetSpan;
                }
            }
        }
        // 거듭제곱 대신 곱셈을 되풀이합니다 (Python 과 같은 반올림).
        double[] freqs = new double[octaves];
        double[] amps = new double[octaves];
        double f = frequency;
        double a = 1.0;
        double total = 0.0;
        for (int o = 0; o < octaves; o++)
        {
            freqs[o] = f;
            amps[o] = a;
            total += a;
            f *= lacunarity;
            a *= gain;
        }
        return (seeds, offsets, freqs, amps, 1.0 / total);
    }

    private static void CheckPositive(string name, double val)
    {
        if (!(double.IsFinite(val) && val > 0.0))
        {
            throw new ArgumentException($"{name} 은 0 보다 큰 유한한 값이어야 합니다: {val}");
        }
    }

    private static void CheckPoints(double[] points)
    {
        if (points.Length % 3 != 0)
        {
            throw new ArgumentException($"points 는 (M, 3) 배열이어야 합니다: 원소 수 {points.Length}");
        }
        foreach (double v in points)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("points 에 NaN 이나 inf 가 있습니다");
            }
        }
    }

    /// <summary>
    /// fbm 커널: points (M,3) 행 우선, seeds (C,O), offsets (C,O,3), freqs (O,), amps (O,) → out (M,C).
    /// numba prange 와 같이 점마다 나눕니다.
    /// </summary>
    private static void FbmKernel(
        double[] points, ulong[] seeds, double[] offsets, double[] freqs, double[] amps, double invNorm,
        double[] output, int nComp)
    {
        int nPoints = points.Length / 3;
        int nOct = freqs.Length;
        Parallelism.For(0, nPoints, k =>
        {
            double px = points[k * 3];
            double py = points[(k * 3) + 1];
            double pz = points[(k * 3) + 2];
            for (int c = 0; c < nComp; c++)
            {
                double acc = 0.0;
                for (int o = 0; o < nOct; o++)
                {
                    double f = freqs[o];
                    int b = ((c * nOct) + o) * 3;
                    acc += amps[o] * Noise3Mixed(
                        (px * f) + offsets[b],
                        (py * f) + offsets[b + 1],
                        (pz * f) + offsets[b + 2],
                        seeds[(c * nOct) + o]);
                }
                output[(k * nComp) + c] = acc * invNorm;
            }
        });
    }

    /// <summary>
    /// 구면·공간 점에서 읽는 fbm ξ(p) = Σ g^o ν(λ·L^o·p + o_o) / Σ g^o (fbm3).
    /// points: (M, 3) 행 우선. 반환: (M,).
    /// </summary>
    public static double[] Fbm3(
        double[] points, long seed, int octaves = 5, double gain = 0.5, double lacunarity = 2.0,
        double frequency = 1.0)
    {
        CheckPoints(points);
        (ulong[] seeds, double[] offsets, double[] freqs, double[] amps, double invNorm) =
            OctaveTables(seed, 1, octaves, gain, lacunarity, frequency);
        double[] output = new double[points.Length / 3];
        FbmKernel(points, seeds, offsets, freqs, amps, invNorm, output, 1);
        return output;
    }

    /// <summary>서로 다른 시드의 fbm 세 개를 묶은 벡터 노이즈 (vector_fbm3). 반환: (M, 3) 행 우선.</summary>
    public static double[] VectorFbm3(
        double[] points, long seed, int octaves = 5, double gain = 0.5, double lacunarity = 2.0,
        double frequency = 1.0)
    {
        CheckPoints(points);
        (ulong[] seeds, double[] offsets, double[] freqs, double[] amps, double invNorm) =
            OctaveTables(seed, 3, octaves, gain, lacunarity, frequency);
        double[] output = new double[points.Length];
        FbmKernel(points, seeds, offsets, freqs, amps, invNorm, output, 3);
        return output;
    }
}
