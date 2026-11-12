using System;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Subsurface;
using Xunit;

namespace Bpcg.Tests.Subsurface;

/// <summary>흙과 맨 암반 (docs/pipeline.md 8.2): 가파른 비탈의 맨 암반은 그 암석 임계 경사의 비율로 정합니다.</summary>
public sealed class SoilTests
{
    private static FieldSet Run(double[] slope, double[] sCrit, CellGraph? graph = null, bool[]? ocean = null)
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
        return Soil.SoilAndAlluvium(zeros, slope, sCrit, uplift, temp, precip, zeros, zeros, null, rock, cfg, ocean, graph);
    }

    [Fact]
    public void SlopeAtOrAboveRatioOfCriticalSlopeIsBareRock()
    {
        // 지구 설정 bare_slope_ratio = 0.9. 임계 경사 0.8 이면 문턱은 0.72, 임계 경사 0.5 면 0.45.
        // 문턱과 꼭 같은 값은 넣지 않습니다. 0.9 × 0.8 은 double 로 0.7200000000000001 이라,
        // 0.72 와 비교하면 끝자리 차이로 결과가 정해집니다.
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
        // 솔버가 경사를 임계 경사에서 자른 칸은 반올림 끝자리가 달라도 모두 맨 암반이어야 합니다.
        // 문턱이 임계 경사와 같으면 이 칸들이 끝자리 차이로 서로 다르게 판정되어 깨알 무늬가 생깁니다.
        double[] slope = [0.8 - 1e-15, 0.8, 0.8 + 1e-15, 0.7999999];
        double[] sCrit = [0.8, 0.8, 0.8, 0.8];
        bool[] bare = Run(slope, sCrit).Get<bool>("bare_rock");
        Assert.All(bare, Assert.True);
    }

    [Fact]
    public void WithGraphACheckerboardOfSlopesNearTheThresholdGetsOneAnswer()
    {
        // 임계 경사 0.8 인 비탈에서 경사가 0.80 과 0.56 으로 한 칸씩 번갈아 놓인 격자입니다 (경사비 1.0 과 0.7).
        // 칸 하나로 정하면 암반과 흙이 한 칸씩 번갈아 놓입니다. 둘레 칸과 평균하면 어느 칸이든 경사비가 0.83~0.87 이라
        // 문턱 0.9 에 못 미쳐 모두 흙이 남습니다.
        const int size = 6;
        CellGraph g = Graph.FlatGraph(size, size, 25.0);
        double[] slope = new double[size * size];
        double[] sCrit = new double[size * size];
        for (int c = 0; c < slope.Length; c++)
        {
            slope[c] = ((c / size) + (c % size)) % 2 == 0 ? 0.80 : 0.56;
            sCrit[c] = 0.8;
        }
        bool[] alone = Run(slope, sCrit).Get<bool>("bare_rock");
        bool[] together = Run(slope, sCrit, g).Get<bool>("bare_rock");
        for (int c = 0; c < slope.Length; c++)
        {
            Assert.Equal(slope[c] > 0.7, alone[c]);
            Assert.False(together[c]);
        }
    }

    [Fact]
    public void WithGraphASlopeThatIsSteepEverywhereStaysBare()
    {
        const int size = 5;
        CellGraph g = Graph.FlatGraph(size, size, 25.0);
        double[] slope = new double[size * size];
        double[] sCrit = new double[size * size];
        Array.Fill(slope, 0.8);
        Array.Fill(sCrit, 0.8);
        Assert.All(Run(slope, sCrit, g).Get<bool>("bare_rock"), Assert.True);
    }

    [Fact]
    public void NeighbourMeanLeavesSeaCellsOut()
    {
        // 3 × 3 격자의 가운데 칸만 육지이고 경사비가 1.0 입니다. 둘레 바다 칸(경사비 0)을 평균에 넣으면 1/9 이 되지만,
        // 바다 칸은 넣지 않으므로 1.0 그대로입니다. 바다 칸의 값은 바꾸지 않습니다.
        CellGraph g = Graph.FlatGraph(3, 3, 25.0);
        double[] ratio = new double[9];
        bool[] land = new bool[9];
        ratio[4] = 1.0;
        land[4] = true;
        double[] mean = Soil.NeighbourMeanSlopeRatio(g, ratio, land);
        Assert.Equal(1.0, mean[4]);
        Assert.Equal(0.0, mean[0]);
        Assert.Throws<ArgumentException>(() => Soil.NeighbourMeanSlopeRatio(g, new double[8], new bool[8]));
    }
}
