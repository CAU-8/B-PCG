using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Geology;

/// <summary>
/// 암석 표와 변성 정도 (src/bpcg/geology/rocks.py, docs/pipeline.md 5.1).
/// </summary>
public static class Rocks
{
    public const byte Alluvium = 0;
    public const byte Sandstone = 1;
    public const byte Shale = 2;
    public const byte Limestone = 3;
    public const byte Granite = 4;
    public const byte Volcanic = 5;
    public const byte Slate = 6;
    public const byte Schist = 7;
    public const byte Gneiss = 8;
    public const byte Marble = 9;
    public const byte Quartzite = 10;
    public const byte Soil = 11;
    public const int NRocks = 12;

    public static readonly string[] RockNames =
    [
        "alluvium", "sandstone", "shale", "limestone", "granite", "volcanic",
        "slate", "schist", "gneiss", "marble", "quartzite", "soil",
    ];

    /// <summary>침식 계수 배율 K = K_ref · K_MULT[암석].</summary>
    public static readonly double[] KMult = [3.0, 0.5, 2.0, 0.6, 0.3, 0.5, 0.8, 0.6, 0.35, 0.5, 0.25, 4.0];

    /// <summary>산비탈이 버티는 가장 가파른 경사 [m/m].</summary>
    public static readonly double[] SCrit = [0.4, 0.75, 0.5, 0.8, 0.8, 0.75, 0.7, 0.7, 0.8, 0.8, 0.85, 0.6];

    /// <summary>투수성 log10 k [m²].</summary>
    public static readonly double[] Log10Perm =
        [-10.9, -12.5, -16.5, -11.8, -14.1, -12.5, -14.1, -14.1, -14.1, -11.8, -14.1, -11.0];

    /// <summary>물에 녹는 암석 (석회암, 대리암).</summary>
    public static readonly bool[] Soluble = BuildSoluble();

    /// <summary>단면 셰이더용 sRGB 색 (N_ROCKS, 3) 행 우선.</summary>
    public static readonly byte[] ColorRgb =
    [
        214, 190, 128,
        226, 150, 82,
        92, 98, 116,
        222, 218, 196,
        206, 140, 136,
        60, 32, 36,
        52, 62, 84,
        128, 140, 92,
        156, 108, 146,
        248, 248, 252,
        232, 170, 180,
        104, 74, 46,
    ];

    public const double TSlateC = 250.0;
    public const double TSchistC = 400.0;
    public const double TGneissC = 600.0;
    public const double TMarbleC = 350.0;
    public const double TQuartziteC = 350.0;
    public const double TGraniteGneissC = 650.0;

    /// <summary>암석마다 (문턱 온도, 바뀐 암석) 목록 (Python dict 순서 그대로).</summary>
    public static readonly OrderedDictionary<int, (double TMin, byte Product)[]> MetamorphicSeries = new()
    {
        [Shale] = [(TSlateC, Slate), (TSchistC, Schist), (TGneissC, Gneiss)],
        [Slate] = [(TSchistC, Schist), (TGneissC, Gneiss)],
        [Schist] = [(TGneissC, Gneiss)],
        [Limestone] = [(TMarbleC, Marble)],
        [Sandstone] = [(TQuartziteC, Quartzite)],
        [Granite] = [(TGraniteGneissC, Gneiss)],
    };

    private const int MaxSteps = 3;

    // (N_ROCKS, MaxSteps): 단계 k 의 문턱 [°C](없으면 inf)와 그때의 암석
    private static readonly (double[] T, byte[] To) MetaTables = SeriesTables();

    private static bool[] BuildSoluble()
    {
        bool[] s = new bool[NRocks];
        s[Limestone] = true;
        s[Marble] = true;
        return s;
    }

    private static (double[] T, byte[] To) SeriesTables()
    {
        double[] t = new double[NRocks * MaxSteps];
        byte[] to = new byte[NRocks * MaxSteps];
        for (int r = 0; r < NRocks; r++)
        {
            for (int k = 0; k < MaxSteps; k++)
            {
                t[(r * MaxSteps) + k] = double.PositiveInfinity;
                to[(r * MaxSteps) + k] = (byte)r;
            }
        }
        foreach (KeyValuePair<int, (double TMin, byte Product)[]> kv in MetamorphicSeries)
        {
            for (int k = 0; k < kv.Value.Length; k++)
            {
                t[(kv.Key * MaxSteps) + k] = kv.Value[k].TMin;
                to[(kv.Key * MaxSteps) + k] = kv.Value[k].Product;
            }
        }
        return (t, to);
    }

    /// <summary>T_peak = surface_temperature_c + geotherm_c_per_m · 깊이 [°C].</summary>
    public static double PeakTemperatureC(double depthM, Config cfg) =>
        cfg.F("geology.surface_temperature_c") + (cfg.F("geology.geotherm_c_per_m") * depthM);

    /// <summary>최고 온도로 원래 암석을 변성암으로 바꿉니다. 문턱과 같은 온도는 변성된 쪽, NaN 은 그대로.</summary>
    public static byte Metamorphose(int rock, double tPeakC)
    {
        if (rock < 0 || rock >= NRocks)
        {
            throw new ArgumentException($"암석 번호는 0..{NRocks - 1} 이어야 합니다");
        }
        byte output = (byte)rock;
        for (int k = 0; k < MaxSteps; k++)
        {
            if (tPeakC >= MetaTables.T[(rock * MaxSteps) + k])
            {
                output = MetaTables.To[(rock * MaxSteps) + k];
            }
        }
        return output;
    }

    /// <summary>배열 판 metamorphose (rock 과 t 는 같은 길이).</summary>
    public static byte[] Metamorphose(byte[] rock, double[] tPeakC)
    {
        if (rock.Length != tPeakC.Length)
        {
            throw new ArgumentException("rock 과 t_peak_c 의 길이가 다릅니다");
        }
        byte[] o = new byte[rock.Length];
        for (int i = 0; i < o.Length; i++)
        {
            o[i] = Metamorphose(rock[i], tPeakC[i]);
        }
        return o;
    }
}
