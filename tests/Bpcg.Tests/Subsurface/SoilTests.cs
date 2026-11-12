using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>흙과 맨 암반 (docs/pipeline.md 8.2): 가파른 비탈의 맨 암반은 그 암석 임계 경사의 비율로 정합니다.</summary>
public sealed class SoilTests
{
    private static FieldSet Run(double[] slope, double[] sCrit)
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        int n = slope.Length;
        double[] zeros = new double[n];
        double[] uplift = new double[n];
        double[] temp = new double[n];
        double[] precip = new double[n];
        byte[] rock = new byte[n];
        for (int c = 0; c < n; c++)
        {
            uplift[c] = 1e-5; // 0.01 mm/yr: 흙이 생기는 속도보다 느려 경사 규칙이 없으면 흙이 쌓임
            temp[c] = 15.0;
            precip[c] = 1.0;
            rock[c] = Rocks.Granite;
        }
        return Soil.SoilAndAlluvium(zeros, slope, sCrit, uplift, temp, precip, zeros, zeros, null, rock, cfg);
    }

    [Fact]
    public void SlopeAtOrAboveRatioOfCriticalSlopeIsBareRock()
    {
        // 지구 설정 bare_slope_ratio = 0.9. 임계 경사 0.8 이면 문턱은 0.72, 임계 경사 0.5 면 0.45.
        // 문턱과 꼭 같은 값은 넣지 않습니다 (0.9 × 0.8 은 double 로 0.7200000000000001 이라 0.72 와 견주면 끝자리에 갈립니다).
        double[] slope = [0.30, 0.71, 0.73, 0.80, 0.44, 0.46];
        double[] sCrit = [0.80, 0.80, 0.80, 0.80, 0.50, 0.50];
        FieldSet f = Run(slope, sCrit);
        bool[] bare = f.Get<bool>("bare_rock");
        double[] soil = f.Get<double>("soil_thickness_m");
        Assert.Equal([false, false, true, true, false, true], bare);
        Assert.True(soil[0] > 0.0 && soil[1] > 0.0 && soil[4] > 0.0);
        Assert.Equal(0.0, soil[2]);
        Assert.Equal(0.0, soil[3]);
        Assert.Equal(0.0, soil[5]);
    }

    [Fact]
    public void CellsCappedAtCriticalSlopeAreBareWhateverTheRounding()
    {
        // 솔버가 경사를 임계 경사에서 자른 칸은 반올림 끝자리가 달라도 모두 맨 암반이어야 합니다
        // (예전 규칙 'slope > 0.8' 은 이 칸들을 끝자리에 따라 갈라 깨알 무늬를 만들었습니다).
        double[] slope = [0.8 - 1e-15, 0.8, 0.8 + 1e-15, 0.7999999];
        double[] sCrit = [0.8, 0.8, 0.8, 0.8];
        bool[] bare = Run(slope, sCrit).Get<bool>("bare_rock");
        Assert.All(bare, Assert.True);
    }
}
