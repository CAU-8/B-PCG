using System;

namespace Bpcg.Core;

/// <summary>물리 상수와 행성 상수 계산 (src/bpcg/core/constants.py, docs/pipeline.md 3장).</summary>
public static class Constants
{
    /// <summary>만유인력 상수 [m³/(kg·s²)].</summary>
    public const double GGrav = 6.674e-11;

    /// <summary>율리우스년 1년 [s/yr].</summary>
    public const double SecondsPerYear = 3.15576e7;

    /// <summary>민물 밀도 [kg/m³].</summary>
    public const double RhoWater = 1000.0;

    /// <summary>물의 점성 [Pa·s].</summary>
    public const double MuWater = 1e-3;

    /// <summary>
    /// 균질한 구의 표면 중력 g = 4/3·π·G·ρ·R [m/s²]. radiusM [m], densityKgM3 [kg/m³].
    /// Python 과 같이 왼쪽부터 곱합니다.
    /// </summary>
    public static double Gravity(double radiusM, double densityKgM3)
    {
        if (!(double.IsFinite(radiusM) && radiusM > 0.0))
        {
            throw new ArgumentException($"반지름은 0 보다 큰 유한한 값이어야 합니다: {radiusM}");
        }
        if (!(double.IsFinite(densityKgM3) && densityKgM3 > 0.0))
        {
            throw new ArgumentException($"밀도는 0 보다 큰 유한한 값이어야 합니다: {densityKgM3}");
        }
        return 4.0 / 3.0 * Math.PI * GGrav * densityKgM3 * radiusM;
    }
}
