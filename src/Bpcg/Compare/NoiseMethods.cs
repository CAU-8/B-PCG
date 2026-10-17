using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;

namespace Bpcg.Compare;

/// <summary>
/// 노이즈 계열 (Galin 2019 3.1.3): fBm, 능선 노이즈, 다중 프랙탈, 영역 비틀기.
/// </summary>
/// <remarks>
/// 모두 <see cref="Noise.GradientNoise3"/>(정수 해시 그래디언트 노이즈)를 옥타브마다 다른 시드·좌표 이동으로 더합니다.
/// 결과는 [0, grid.relief_m] 으로 맞춥니다(절차적 방법은 높이 단위가 없기 때문).
/// 같은 시드면 fbm 과 침식 방법의 시작 지형이 같습니다(<see cref="BaseFbm"/>).
/// </remarks>
public static class NoiseMethods
{
    public const long StreamBase = 7000; // 방법마다 시드를 가르는 해시 용도 번호의 시작
    private const long StreamSeed = StreamBase + 101;
    private const long StreamOffset = StreamBase + 102;
    private const double OffsetSpan = 1000.0; // 옥타브마다 좌표를 옮기는 범위 (노이즈 칸 단위)

    public static readonly Param Wavelength = new("wavelength_m", 6000.0, "가장 큰 물결의 파장 (산 하나의 크기쯤)", Lo: 1.0, Unit: "m");
    public static readonly Param Octaves = new("octaves", 8L, "겹쳐 더하는 물결 수 (겹마다 파장이 짧아짐)", Kind: "int", Lo: 1, Hi: 16);
    public static readonly Param Lacunarity = new("lacunarity", 2.0, "겹마다 파장을 몇 배 짧게 하나 (2면 반씩)", Lo: 1.1, Hi: 4.0);
    public static readonly Param Gain = new("gain", 0.5, "겹마다 물결 높이를 몇 배로 하나 (0.5면 반씩, persistence)", Lo: 0.05, Hi: 1.0);

    /// <summary>fbm 과 침식 방법이 같이 쓰는 매개변수.</summary>
    public static readonly Param[] FbmParams = [Wavelength, Octaves, Lacunarity, Gain];

    /// <summary>옥타브별 표: 시드 (O,), 좌표 이동 (O·3,), 주파수 (O,). salt 로 용도를 가릅니다.</summary>
    public sealed record OctaveTable(long[] Seeds, double[] Offsets, double[] Freqs)
    {
        public int Count => Seeds.Length;

        public OctaveTable Prefix(int k) => new(Seeds[..k], Offsets[..(3 * k)], Freqs[..k]);
    }

    public static OctaveTable Tables(long seed, int octaves, double lacunarity, long salt)
    {
        long[] seeds = new long[octaves];
        double[] offs = new double[octaves * 3];
        double[] freqs = new double[octaves];
        double f = 1.0;
        for (int o = 0; o < octaves; o++)
        {
            long key = (salt * 64) + o;
            seeds[o] = (long)(Hashing.Hash3(seed, key, StreamSeed) >> 1);
            for (int ax = 0; ax < 3; ax++)
            {
                offs[(o * 3) + ax] = Hashing.HashUnit(seed, key, StreamOffset + ax) * OffsetSpan;
            }
            freqs[o] = f;
            f *= lacunarity;
        }
        return new OctaveTable(seeds, offs, freqs);
    }

    /// <summary>칸 중심을 노이즈 좌표(가장 큰 파장 = 1)로: (px, py), 각각 (n·n,).</summary>
    public static (double[] Px, double[] Py) Points(CompareGrid grid, double wavelengthM)
    {
        double[] x = grid.X();
        double[] y = grid.Y();
        for (int k = 0; k < x.Length; k++)
        {
            x[k] /= wavelengthM;
            y[k] /= wavelengthM;
        }
        return (x, y);
    }

    private static double N(double x, double y, int o, OctaveTable t)
    {
        double f = t.Freqs[o];
        return Noise.GradientNoise3((x * f) + t.Offsets[o * 3], (y * f) + t.Offsets[(o * 3) + 1], t.Offsets[(o * 3) + 2], t.Seeds[o]);
    }

    /// <summary>t(p) = Σ a_o n(φ_o p) (Galin 2019 3.1.3 식).</summary>
    public static double[] FbmKernel(double[] px, double[] py, OctaveTable t, double[] amps)
    {
        double[] o = new double[px.Length];
        Parallelism.For(0, px.Length, k =>
        {
            double acc = 0.0;
            for (int i = 0; i < t.Count; i++)
            {
                acc += amps[i] * N(px[k], py[k], i, t);
            }
            o[k] = acc;
        });
        return o;
    }

    /// <summary>능선 다중 프랙탈 (Ebert 외 1998, Musgrave): 신호 (offset − |n|)², 앞 층 신호로 가중.</summary>
    public static double[] RidgedKernel(double[] px, double[] py, OctaveTable t, double[] amps, double offset, double gain)
    {
        double[] o = new double[px.Length];
        Parallelism.For(0, px.Length, k =>
        {
            double s = offset - Math.Abs(N(px[k], py[k], 0, t));
            s *= s;
            double result = s;
            for (int i = 1; i < t.Count; i++)
            {
                double w = Math.Clamp(s * gain, 0.0, 1.0);
                s = offset - Math.Abs(N(px[k], py[k], i, t));
                s *= s;
                s *= w;
                result += s * amps[i];
            }
            o[k] = result;
        });
        return o;
    }

    /// <summary>혼합 다중 프랙탈: t_{k+1} = α(t_k)·a_{k+1}·n + t_k (Galin 2019 3.1.3, Ebert 외 1998).</summary>
    public static double[] HybridKernel(double[] px, double[] py, OctaveTable t, double[] amps, double offset)
    {
        double[] o = new double[px.Length];
        Parallelism.For(0, px.Length, k =>
        {
            double result = (N(px[k], py[k], 0, t) + offset) * amps[0];
            double weight = result;
            for (int i = 1; i < t.Count; i++)
            {
                weight = Math.Min(weight, 1.0);
                double sig = (N(px[k], py[k], i, t) + offset) * amps[i];
                result += weight * sig;
                weight *= sig;
            }
            o[k] = result;
        });
        return o;
    }

    /// <summary>층마다 gain 배 (a_o = gain^o).</summary>
    public static double[] GainAmps(int octaves, double gain)
    {
        double[] a = new double[octaves];
        double v = 1.0;
        for (int o = 0; o < octaves; o++)
        {
            a[o] = v;
            v *= gain;
        }
        return a;
    }

    /// <summary>다중 프랙탈의 층별 진폭 a_o = lacunarity^(−H·o).</summary>
    public static double[] SpectralAmps(int octaves, double lacunarity, double h)
    {
        double[] a = new double[octaves];
        double step = Math.Pow(lacunarity, -h);
        double v = 1.0;
        for (int o = 0; o < octaves; o++)
        {
            a[o] = v;
            v *= step;
        }
        return a;
    }

    /// <summary>fbm 방법과 같은 지형 (n·n,) [m]. 침식 방법의 시작 지형으로도 씁니다.</summary>
    public static double[] BaseFbm(CompareGrid grid, long seed, ParamValues p)
    {
        (double[] px, double[] py) = Points(grid, p.F("wavelength_m"));
        OctaveTable t = Tables(seed, p.I("octaves"), p.F("lacunarity"), 0);
        double[] raw = FbmKernel(px, py, t, GainAmps(p.I("octaves"), p.F("gain")));
        return CompareGrid.NormalizeRelief(raw, grid.ReliefM);
    }

    /// <summary>과정 기록에서 보여 줄 층 수 (1, 2, 3, 4, 6, 8, 12, 16 가운데 octaves 미만, 마지막에 octaves).</summary>
    public static List<int> PrefixCounts(int octaves) =>
        [.. new[] { 1, 2, 3, 4, 6, 8, 12, 16 }.Where(k => k < octaves), octaves];

    /// <summary>처음 k 층만 더한 지형을 최종 지형과 같은 높이 축으로 남깁니다. partial(k) → (n·n,).</summary>
    public static void RecordOctaves(CompareContext ctx, CompareGrid grid, double[] full, Func<int, double[]> partial, int octaves, string what)
    {
        (double lo, double hi) = CompareGrid.MinMax(full);
        double scale = grid.ReliefM / Math.Max(hi - lo, 1e-12);
        foreach (int k in PrefixCounts(octaves))
        {
            double[] t = k == octaves ? full : partial(k);
            ctx.Snapshot(
                $"{k}층까지",
                Array.ConvertAll(t, v => (v - lo) * scale),
                scale: "final",
                unit: "m",
                note: $"{what}의 처음 {k}층만 더한 지형입니다. 높이 축은 최종 지형과 같습니다.");
        }
    }

    public static void RecordAmplitudes(CompareContext ctx, OctaveTable t, double[] amps, double wavelengthM) =>
        ctx.Bar(
            "amplitude",
            Enumerable.Range(0, amps.Length).Select(o => ($"{o + 1}층 {wavelengthM / t.Freqs[o]:F0} m", amps[o])),
            label: "층별 진폭 (파장)");

    public static readonly CompareMethod Fbm = new(
        "fbm",
        "fBm 노이즈",
        "procedural",
        "Galin 2019 3.1.3; Musgrave 외 1989, Ebert 외 1998",
        "크기가 다른 무작위 물결(그래디언트 노이즈) 여러 겹을 더한 지형. 게임 지형 도구에서 가장 흔한 방식",
        FbmParams,
        (grid, seed, p, ctx) =>
        {
            (double[] px, double[] py) = Points(grid, p.F("wavelength_m"));
            int oct = p.I("octaves");
            OctaveTable t = Tables(seed, oct, p.F("lacunarity"), 0);
            double[] amps = GainAmps(oct, p.F("gain"));
            double[] raw = FbmKernel(px, py, t, amps);
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    RecordOctaves(ctx, grid, raw, k => FbmKernel(px, py, t.Prefix(k), amps[..k]), oct, "fBm");
                    RecordAmplitudes(ctx, t, amps, p.F("wavelength_m"));
                }
            }
            return new MethodOutput(CompareGrid.NormalizeRelief(raw, grid.ReliefM));
        });

    public static readonly CompareMethod Ridged = new(
        "ridged",
        "능선 노이즈",
        "procedural",
        "Galin 2019 3.1.3 (그림 7·8); Ebert 외 1998",
        "물결의 절댓값을 뒤집어 날카로운 산마루 선을 만들고, 앞 겹이 높은 곳에서 다음 겹을 키움",
        [
            Wavelength, Octaves, Lacunarity,
            new Param("h", 1.0, "겹마다 물결 높이를 줄이는 지수 H (o번째 겹 높이 = lacunarity^−H·o)", Lo: 0.0, Hi: 2.0),
            new Param("offset", 1.0, "산마루로 뒤집을 기준 높이", Lo: 0.0, Hi: 2.0),
            new Param("ridge_gain", 2.0, "앞 겹이 높은 곳에서 다음 겹을 얼마나 키우나", Lo: 0.0, Hi: 8.0),
        ],
        (grid, seed, p, ctx) =>
        {
            (double[] px, double[] py) = Points(grid, p.F("wavelength_m"));
            int oct = p.I("octaves");
            OctaveTable t = Tables(seed, oct, p.F("lacunarity"), 0);
            double[] amps = SpectralAmps(oct, p.F("lacunarity"), p.F("h"));
            double[] raw = RidgedKernel(px, py, t, amps, p.F("offset"), p.F("ridge_gain"));
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    RecordOctaves(ctx, grid, raw, k => RidgedKernel(px, py, t.Prefix(k), amps[..k], p.F("offset"), p.F("ridge_gain")), oct, "능선 노이즈");
                    RecordAmplitudes(ctx, t, amps, p.F("wavelength_m"));
                }
            }
            return new MethodOutput(CompareGrid.NormalizeRelief(raw, grid.ReliefM));
        });

    public static readonly CompareMethod Multifractal = new(
        "multifractal",
        "다중 프랙탈",
        "procedural",
        "Galin 2019 3.1.3 (다중 프랙탈 식); Ebert 외 1998",
        "낮은 곳은 잔 물결을 줄여 평야로, 높은 곳은 잔 물결을 키워 거친 산으로 만드는 방식",
        [
            Wavelength, Octaves, Lacunarity,
            new Param("h", 0.25, "겹마다 물결 높이를 줄이는 지수 H", Lo: 0.0, Hi: 2.0),
            new Param("offset", 0.7, "물결에 더하는 값 (클수록 거칠어짐)", Lo: 0.0, Hi: 2.0),
        ],
        (grid, seed, p, ctx) =>
        {
            (double[] px, double[] py) = Points(grid, p.F("wavelength_m"));
            int oct = p.I("octaves");
            OctaveTable t = Tables(seed, oct, p.F("lacunarity"), 0);
            double[] amps = SpectralAmps(oct, p.F("lacunarity"), p.F("h"));
            double[] raw = HybridKernel(px, py, t, amps, p.F("offset"));
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    RecordOctaves(ctx, grid, raw, k => HybridKernel(px, py, t.Prefix(k), amps[..k], p.F("offset")), oct, "다중 프랙탈");
                    RecordAmplitudes(ctx, t, amps, p.F("wavelength_m"));
                }
            }
            return new MethodOutput(CompareGrid.NormalizeRelief(raw, grid.ReliefM));
        });

    public static readonly CompareMethod Warped = new(
        "warped",
        "비튼 fBm",
        "procedural",
        "Galin 2019 3.1.3 (영역 비틀기, 그림 8); de Carpentier & Bidarra 2009",
        "느린 물결로 좌표를 비튼 뒤 fBm 을 읽어, 규칙적인 무늬를 흐르는 듯한 무늬로 바꿈",
        [
            .. FbmParams,
            new Param("warp", 0.6, "좌표를 비트는 세기 (가장 큰 파장에 대한 비율)", Lo: 0.0, Hi: 4.0),
            new Param("warp_octaves", 4L, "좌표를 비트는 물결의 겹 수", Kind: "int", Lo: 1, Hi: 8),
        ],
        (grid, seed, p, ctx) =>
        {
            (double[] px, double[] py) = Points(grid, p.F("wavelength_m"));
            double lac = p.F("lacunarity");
            int wo = p.I("warp_octaves");
            double[] wAmps = GainAmps(wo, 0.5);
            double[] qx = FbmKernel(px, py, Tables(seed, wo, lac, 1), wAmps);
            double[] qy = FbmKernel(px, py, Tables(seed, wo, lac, 2), wAmps);
            double warp = p.F("warp");
            double[] wx = new double[px.Length];
            double[] wy = new double[px.Length];
            for (int k = 0; k < px.Length; k++)
            {
                wx[k] = px[k] + (warp * qx[k]);
                wy[k] = py[k] + (warp * qy[k]);
            }
            int oct = p.I("octaves");
            OctaveTable t = Tables(seed, oct, lac, 0);
            double[] amps = GainAmps(oct, p.F("gain"));
            double[] raw = FbmKernel(wx, wy, t, amps);
            double[] z = CompareGrid.NormalizeRelief(raw, grid.ReliefM);
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    ctx.Snapshot("비틀기 전 fBm", CompareGrid.NormalizeRelief(FbmKernel(px, py, t, amps), grid.ReliefM), unit: "m",
                        note: "같은 시드·같은 층으로 좌표를 비틀지 않고 읽은 지형입니다.");
                    double wl = p.F("wavelength_m");
                    double[] shift = new double[px.Length];
                    for (int k = 0; k < px.Length; k++)
                    {
                        shift[k] = warp * Math.Sqrt((qx[k] * qx[k]) + (qy[k] * qy[k])) * wl;
                    }
                    ctx.Snapshot("좌표를 옮긴 거리", shift, kind: "field", unit: "m", note: "칸마다 노이즈를 읽는 자리를 얼마나 옮겼는지입니다.");
                    ctx.Snapshot("비튼 결과", z, unit: "m", note: "옮긴 자리에서 읽은 fBm 입니다.");
                }
            }
            return new MethodOutput(z);
        });
}
