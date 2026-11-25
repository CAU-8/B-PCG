using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>
/// 판이 갈라지는 곳(열곡)을 분지와 턱으로 나누는 계산 (docs/pipeline.md 7.5, 설정 lakes.rift_segment_m). 지구의 열곡은 끊기지 않는
/// 골 하나가 아니라, 길이 약 100 km 의 분지가 가라앉지 않는 턱을 사이에 두고 늘어선 모양입니다. 생성기는 노이즈(파장 rift_segment_m)로
/// 칸마다 가라앉는 몫(0~1)을 정해 이 모양을 만듭니다. 이 시험은 몫이 띠를 분지 여럿으로 나누는지와, 나누는 길이를 줄이면 분지가
/// 늘어나는지를 봅니다.
/// </summary>
public sealed class RiftSegmentTests
{
    private const double SinkMPerYr = -3.0e-5;

    private static Config Cfg(double segmentM) =>
        Config.LoadConfig("earth", "tiny").WithOverrides([new("lakes.rift_segment_m", segmentM)]);

    /// <summary>
    /// 남위 30° 아래가 바다이고 북쪽으로 갈수록 높아지는 행성 (면당 32칸, 칸 한 변 약 310 km). 적도를 따라 한 바퀴(약 4만 km) 도는
    /// 폭 두 칸의 띠(위도 ±2.3° 안, 256칸)가 한 해 0.03 mm 씩 가라앉고, 흙은 들어오지 않습니다. 몫이 1 인 칸은 3천만 년 동안 흙이
    /// 메우지 못한 깊이가 900 m 입니다. 바닥은 바다보다 낮아지지 않게 줄인 높이(Hollows.LoweredBed)라, 약 500 m 의 땅이 170~200 m
    /// 까지 내려갑니다. 기온은 어디나 25 °C 라 얼음이 덮인 적이 없어, 얼음이 긁은 자리는 생기지 않습니다.
    /// </summary>
    private static (CellGraph Graph, double[] Z, bool[] Ocean, bool[] Belt, BasinCauses Causes) RiftPlanet()
    {
        CellGraph g = Graph.SphereGraph(32, 6.371e6);
        int n = g.NCells;
        double[] unit = g.Unit();
        double[] z = new double[n];
        bool[] ocean = new bool[n];
        bool[] belt = new bool[n];
        double[] uplift = new double[n];
        double[] lat = new double[n];
        for (int c = 0; c < n; c++)
        {
            double up = unit[(c * 3) + 2];
            lat[c] = Math.Asin(up);
            ocean[c] = up < -0.5;
            z[c] = ocean[c] ? 0.0 : 10.0 + (1000.0 * (up + 0.5));
            belt[c] = !ocean[c] && Math.Abs(up) < 0.04;
            uplift[c] = belt[c] ? SinkMPerYr : 0.0;
        }
        long[] rcv = Routing.D8Receivers(z, g.Nbr, g.Dist, ocean).Rcv;
        var causes = new BasinCauses(
            rcv, Routing.TopoOrder(rcv), uplift, Enumerable.Repeat(25.0, n).ToArray(), Enumerable.Repeat(0.5, n).ToArray(), lat);
        return (g, z, ocean, belt, causes);
    }

    /// <summary>낮춘 칸을 서로 이웃한 묶음(분지)으로 나눈 수.</summary>
    private static int CountBasins(CellGraph g, bool[] hollow)
    {
        bool[] seen = new bool[g.NCells];
        var stack = new Stack<int>();
        int count = 0;
        for (int c0 = 0; c0 < g.NCells; c0++)
        {
            if (!hollow[c0] || seen[c0])
            {
                continue;
            }
            count++;
            seen[c0] = true;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && hollow[v] && !seen[v])
                    {
                        seen[v] = true;
                        stack.Push(v);
                    }
                }
            }
        }
        return count;
    }

    [Fact]
    public void TheRiftSplitsIntoBasinsWithSillsAndShorterSegmentsMakeMoreBasins()
    {
        (CellGraph g, double[] z, bool[] ocean, bool[] belt, BasinCauses causes) = RiftPlanet();
        int n = g.NCells;
        double[] noSediment = new double[n];
        // 나누지 않으면(rift_segment_m = 0) 띠 전체가 고르게 가라앉아, 행성을 한 바퀴 도는 골 하나가 물이 고이는 자리입니다.
        BasinResult whole = Hollows.AddBasins(g, z, ocean, noSediment, Cfg(0.0), causes);
        Assert.Equal(belt, whole.Hollow);
        Assert.Equal(1, CountBasins(g, whole.Hollow));
        Assert.Equal(belt.Count(b => b), whole.NTectonic);
        Assert.Equal(0, whole.NGlacial);

        // 나누면 턱(가라앉는 몫 0)이 띠를 끊어 분지가 여럿 생깁니다. 턱 칸은 낮추지 않아 처음 높이 그대로입니다.
        int previous = 0;
        foreach (double segment in new[] { 6.0e6, 1.5e6 })
        {
            BasinResult parted = Hollows.AddBasins(g, z, ocean, noSediment, Cfg(segment), causes);
            int basins = CountBasins(g, parted.Hollow);
            int sills = Enumerable.Range(0, n).Count(c => belt[c] && !parted.Hollow[c]);
            Assert.True(basins >= 2, $"분지 하나와 턱을 합친 길이 {segment / 1e3} km: 띠가 분지 {basins}개로만 나뉩니다");
            Assert.True(sills > 0, $"분지 하나와 턱을 합친 길이 {segment / 1e3} km: 턱 칸이 없습니다");
            for (int c = 0; c < n; c++)
            {
                // 낮춘 칸은 띠 안에만 있고, 어느 칸도 나누지 않았을 때보다 깊이 낮아지지 않습니다 (몫 ≤ 1).
                Assert.True(!parted.Hollow[c] || belt[c], $"칸 {c} 는 가라앉는 띠 밖인데 낮췄습니다");
                Assert.True(parted.Z[c] >= whole.Z[c], $"칸 {c} 가 나누지 않았을 때보다 깊이 낮아졌습니다");
                Assert.True(parted.Hollow[c] || parted.Z[c] == z[c], $"물이 고이지 않는 칸 {c} 의 높이가 바뀌었습니다");
            }
            // 분지 하나와 턱을 합친 길이를 줄이면 띠가 더 잘게 나뉩니다.
            Assert.True(basins > previous, $"분지 하나와 턱을 합친 길이를 {segment / 1e3} km 로 줄였는데 분지가 {previous}개에서 {basins}개입니다");
            previous = basins;
        }
    }
}
