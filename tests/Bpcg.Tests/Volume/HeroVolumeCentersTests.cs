using System;
using Bpcg.Core;
using Bpcg.Volume;
using Xunit;

namespace Bpcg.Tests.Volume;

/// <summary>
/// 3D 샘플 함수가 대표점(흔들어 둔 격자점)의 값을 칸 중심의 값으로 옮겨 쓰는지 봅니다 (docs/pipeline.md 10장 '입력').
/// tiny 프로필·시드 0 의 행성과 히어로 유역을 한 번 만들어 씁니다 (약 3초).
/// </summary>
public sealed class HeroVolumeCentersTests
{
    private static readonly Lazy<(HeroState Hero, Config Cfg)> Tiny = new(() =>
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        PlanetState planet = Pipeline.GeneratePlanet(cfg, null);
        return (Pipeline.GenerateHero(cfg, planet, null), cfg);
    });

    private static HeroVolume Volume() => new(Tiny.Value.Hero, Tiny.Value.Cfg);

    private static bool[] WaterCells(HeroState hero)
    {
        bool[] B(string name) => (bool[])FieldSet.CastTo(hero.Fields[name], typeof(bool));
        bool[] river = B("is_river");
        bool[] lake = B("is_lake");
        bool[] ocean = hero.Fields.Contains("is_ocean") ? B("is_ocean") : new bool[river.Length];
        bool[] water = new bool[river.Length];
        for (int c = 0; c < water.Length; c++)
        {
            water[c] = river[c] || lake[c] || ocean[c];
        }
        return water;
    }

    [Fact]
    public void WaterTablesStayBelowTheCenteredSurface()
    {
        HeroState hero = Tiny.Value.Hero;
        HeroVolume vol = Volume();
        bool[] water = WaterCells(hero);
        double[] node = hero.Fields.GetFloat64("water_table_m");
        int nodeAbove = 0;
        for (int c = 0; c < water.Length; c++)
        {
            if (water[c])
            {
                continue;
            }
            // 대표점의 지하수면을 칸 중심 값처럼 쓰면 이 칸들에서 지표 위로 나옵니다 (가파른 땅에서 칸 하나의 높이 차만큼).
            nodeAbove += node[c] > vol.Z[c] + 0.01 ? 1 : 0;
            Assert.True(vol.ZGwMap[c] <= vol.Z[c], $"칸 {c}: 지하수면 {vol.ZGwMap[c]} > 지표 {vol.Z[c]}");
            Assert.True(vol.ZGwLowerMap[c] <= vol.ZGwMap[c], $"칸 {c}: 아래 {vol.ZGwLowerMap[c]} > 맨 위 {vol.ZGwMap[c]}");
        }
        Assert.True(nodeAbove > 0, "이 히어로에는 대표점의 지하수면이 칸 중심 지표 위에 있는 칸이 없어 시험이 아무것도 확인하지 않습니다");
    }

    [Fact]
    public void BankSpringHolesEndInTheRiverWater()
    {
        HeroVolume vol = Volume();
        int nBank = vol.NBankSprings;
        Assert.True(nBank > 0, "강가 샘이 없어 시험이 아무것도 확인하지 않습니다");
        int first = vol.EntranceCapsules.Length - nBank;
        double[] x = new double[nBank];
        double[] y = new double[nBank];
        for (int q = 0; q < nBank; q++)
        {
            int e = vol.EntranceCapsules[first + q];
            x[q] = vol.CapB[e * 3];
            y[q] = vol.CapB[(e * 3) + 1];
        }
        double[] ground = vol.SurfaceHeight(x, y);
        double[] level = vol.WaterSurface(x, y);
        for (int q = 0; q < nBank; q++)
        {
            Assert.True(
                double.IsFinite(level[q]) && level[q] > ground[q],
                $"강가 샘 {q}: 굴 끝 ({x[q]:F1}, {y[q]:F1}) 이 물 밖입니다 (수면 {level[q]}, 지표 {ground[q]})");
        }
    }

    [Fact]
    public void StrataBoundariesLoseTheCellSizeSawtooth()
    {
        HeroVolume vol = Volume();
        int nx = vol.Nx;
        int ny = vol.Ny;
        int nL = vol.NLayers;
        bool SameColumn(int a, int b)
        {
            for (int i = 0; i <= nL; i++)
            {
                if (vol.StrataRock[(a * (nL + 1)) + i] != vol.StrataRock[(b * (nL + 1)) + i])
                {
                    return false;
                }
            }
            return true;
        }
        // 칸 값 − 둘레 여덟 칸 평균의 RMS (맨 위 층 바닥, 3 × 3 이 모두 같은 지층 순서인 칸). 기운 평면이면 0 입니다.
        double Rms(double[] bottom)
        {
            double s2 = 0.0;
            int count = 0;
            for (int j = 1; j < ny - 1; j++)
            {
                for (int i = 1; i < nx - 1; i++)
                {
                    int c = (j * nx) + i;
                    double mean = 0.0;
                    bool ok = true;
                    for (int dj = -1; dj <= 1 && ok; dj++)
                    {
                        for (int di = -1; di <= 1 && ok; di++)
                        {
                            int k = ((j + dj) * nx) + i + di;
                            ok = SameColumn(c, k);
                            mean += k == c ? 0.0 : bottom[k * nL];
                        }
                    }
                    if (ok)
                    {
                        double r = bottom[c * nL] - (mean / 8.0);
                        s2 += r * r;
                        count++;
                    }
                }
            }
            Assert.True(count > 100, $"같은 지층 순서인 칸이 {count}개뿐입니다");
            return Math.Sqrt(s2 / count);
        }
        double before = Rms(vol.StrataBottom);
        double after = Rms(vol.StrataBottomCenter);
        Assert.True(before > 0.5, $"대표점의 층 바닥에 톱니가 없어({before:F2} m) 시험이 아무것도 확인하지 않습니다");
        Assert.True(after < 0.3 * before, $"칸 크기 톱니의 RMS 가 {before:F2} m → {after:F2} m 로, 충분히 줄지 않았습니다");
    }
}
