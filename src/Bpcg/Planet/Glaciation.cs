using System;
using Bpcg.Core;

namespace Bpcg.Planet;

/// <summary>
/// 빙하가 덮였던 땅 (docs/pipeline.md 7.5). 지구의 호수는 마지막 빙하기에 얼음이 덮였다가 물러난 땅에 몰려 있습니다.
/// 얼음이 바닥을 고르지 않게 긁어, 강이 아직 메우지 못한 우묵한 자리가 남았기 때문입니다. 그래서 호수를 놓기 전에
/// 어느 칸이 얼음에 덮였는지부터 가립니다.
/// </summary>
/// <remarks>
/// 얼음은 한 해에 내린 눈이 여름에 다 녹지 못하는 곳에 쌓입니다. 눈이 딱 다 녹는 경계에서는 여름 기온 T [°C] 와
/// 한 해 강수 P [mm] 사이에 P = 645 + 296·T + 9·T² 가 맞습니다 (Ohmura, Kasser &amp; Funk 1992, J. Glaciol. 38,
/// 빙하 70개의 관측을 맞춘 식, 표준오차 200 mm). 여름이 추울수록 적은 눈으로도 얼음이 남습니다.
/// 예: 여름 기온 1.2 °C 인 곳은 강수가 1,000 mm 넘어야 얼음이 남고, 여름 기온 −2.3 °C 아래에서는 눈이 조금만 와도 남습니다.
/// 우리 기후에는 계절이 없어서 여름 기온을 '연평균 + 여름 더위 × |sin 위도|' 로 어림합니다 (가정).
/// 빙하기 기온은 지금보다 lgm_cooling_c 만큼 낮았다고 봅니다 (지구 평균 약 6 °C, Tierney 외 2020).
/// </remarks>
public static class Glaciation
{
    /// <summary>얼음이 덮인 적 없는 칸.</summary>
    public const byte None = 0;

    /// <summary>빙하기에만 얼음이 덮였던 칸.</summary>
    public const byte IceAge = 1;

    /// <summary>
    /// 지금 기온에서도 눈이 다 녹지 못하는 칸. 실제라면 지금도 얼음 아래입니다. 이 생성기는 지금의 얼음을 만들지 않으므로
    /// 표시로만 남기고, 얼음이 긁은 호수 자리는 IceAge 칸과 똑같이 둡니다.
    /// </summary>
    public const byte Now = 2;

    // Ohmura 외 1992 의 계수: P [mm] = A + B·T + C·T².
    private const double OhmuraA = 645.0;
    private const double OhmuraB = 296.0;
    private const double OhmuraC = 9.0;

    /// <summary>이 여름 기온 [°C] 아래에서는 식의 값이 0 이하라, 강수가 얼마든 눈이 남습니다 (약 −2.35 °C).</summary>
    public static readonly double AlwaysIceSummerC =
        (-OhmuraB + Math.Sqrt((OhmuraB * OhmuraB) - (4.0 * OhmuraC * OhmuraA))) / (2.0 * OhmuraC);

    /// <summary>여름(가장 따뜻한 석 달) 기온 [°C] = 연평균 + 여름 더위 × |sin 위도|. 적도에는 계절이 없어 연평균과 같습니다.</summary>
    public static double SummerTemperature(double annualC, double latRad, double summerAmplitudeC) =>
        annualC + (summerAmplitudeC * Math.Abs(Math.Sin(latRad)));

    /// <summary>
    /// 여름 기온이 summerC [°C] 인 곳에서 얼음이 남는 데 필요한 한 해 강수 [m/yr] (Ohmura 외 1992).
    /// AlwaysIceSummerC 아래에서는 0 입니다.
    /// </summary>
    public static double RequiredPrecip(double summerC)
    {
        if (summerC <= AlwaysIceSummerC)
        {
            return 0.0;
        }
        return (OhmuraA + (OhmuraB * summerC) + (OhmuraC * summerC * summerC)) / 1000.0;
    }

    /// <summary>연평균 기온 annualC [°C], 강수 precip [m/yr], 위도 latRad [rad] 인 땅에 얼음이 남는지.</summary>
    public static bool HoldsIce(double annualC, double precip, double latRad, double summerAmplitudeC) =>
        precip >= RequiredPrecip(SummerTemperature(annualC, latRad, summerAmplitudeC));

    /// <summary>
    /// 칸마다 얼음이 덮였는지 가립니다: None, IceAge(빙하기에만), Now(지금도). 바다 칸은 None 입니다.
    /// temperatureC: 지금의 연평균 기온 [°C]. precip: 한 해 강수 [m/yr]. latRad: 위도 [rad].
    /// coolingC: 빙하기에 지금보다 낮았던 기온 [°C]. summerAmplitudeC: 극에서 여름이 연평균보다 따뜻한 정도 [°C].
    /// 빙하기 강수는 지금과 같다고 봅니다 (가정).
    /// </summary>
    public static byte[] IceCover(
        double[] temperatureC, double[] precip, double[] latRad, bool[] isOcean, double coolingC, double summerAmplitudeC)
    {
        int n = temperatureC.Length;
        if (precip.Length != n || latRad.Length != n || isOcean.Length != n)
        {
            throw new ArgumentException(
                $"temperatureC, precip, latRad, isOcean 은 같은 길이여야 합니다. 받은 길이: {n}, {precip.Length}, {latRad.Length}, {isOcean.Length}");
        }
        if (!(double.IsFinite(coolingC) && coolingC >= 0.0 && double.IsFinite(summerAmplitudeC) && summerAmplitudeC >= 0.0))
        {
            throw new ArgumentException(
                $"lgm_cooling_c ≥ 0, summer_amplitude_c ≥ 0 이어야 합니다. 받은 값: lgm_cooling_c {coolingC}, summer_amplitude_c {summerAmplitudeC}");
        }
        byte[] ice = new byte[n];
        Parallelism.For(0, n, c =>
        {
            if (isOcean[c])
            {
                return;
            }
            if (HoldsIce(temperatureC[c], precip[c], latRad[c], summerAmplitudeC))
            {
                ice[c] = Now;
            }
            else if (HoldsIce(temperatureC[c] - coolingC, precip[c], latRad[c], summerAmplitudeC))
            {
                ice[c] = IceAge;
            }
        });
        return ice;
    }
}
