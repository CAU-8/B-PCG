using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 판이 갈라지는 곳(발산 경계)까지 거리를 거친 격자에서 L0 로 옮기는 규칙 (docs/pipeline.md 4.1 '거리', 4.4 '대륙 열곡').
/// 거친 격자의 경계 칸은 경계선에서 반 칸 떨어져 있어서, 그대로 보간하면 L0 의 거리가 반 칸 아래로 내려가지 않았습니다.
/// 그러면 열곡 축이 설정만큼 가라앉지 않고, 가라앉는 세기가 거친 격자의 크기(프로필)에 따라 달라집니다.
/// </summary>
public sealed class RiftDistanceTests
{
    private const double EarthR = 6_371_000.0;

    // 손으로 놓은 두 판: x < 0 이 판 0, x > 0 이 판 1 이라 경계선은 대원 x = 0 입니다. 북반구(z > 0) 쪽 경계는 발산, 남반구 쪽은 변환으로
    // 둡니다. L0 점 p 에서 북쪽 경계선까지 실제 거리는 R·asin|p_x| 입니다 (z 가 충분히 클 때).
    [Fact]
    public void DivergentDistanceFollowsTheBoundaryLineAndReachesZero()
    {
        const int nCoarse = 16;
        CellGraph coarse = Graph.SphereGraph(nCoarse, EarthR);
        CellGraph fine = Graph.SphereGraph(64, EarthR, jitter: 1.0, seed: 7);
        double[] uc = coarse.Unit();
        int nc = coarse.NCells;
        int[] plate = new int[nc];
        for (int c = 0; c < nc; c++)
        {
            plate[c] = uc[c * 3] < 0.0 ? 0 : 1;
        }
        // 생성기(Plates.GeneratePlates)와 같게 잽니다: 경계 칸은 다른 판 이웃까지 거리의 반, 나머지는 같은 판 안의 가장 가까운 발산 경계 칸까지.
        byte[] boundary = new byte[nc];
        double[] halfGap = new double[nc];
        for (int c = 0; c < nc; c++)
        {
            double gap = double.PositiveInfinity;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = coarse.Nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0 && plate[v] != plate[c])
                {
                    gap = Math.Min(gap, coarse.Dist[(c * CellGraph.NSlots) + s]);
                }
            }
            if (double.IsFinite(gap))
            {
                boundary[c] = uc[(c * 3) + 2] > 0.0 ? Plates.Divergent : Plates.Transform;
                halfGap[c] = 0.5 * gap;
            }
        }
        bool[] isDiv = Array.ConvertAll(boundary, b => b == Plates.Divergent);
        (double[] dist, _) = Plates.BoundaryDistance(Plates.SamePlateGraph(coarse, plate), isDiv, halfGap);
        var coarseFields = new FieldSet { ["plate_id"] = plate, ["boundary_type"] = boundary, ["dist_divergent_m"] = dist };
        long[] nearest = Cubesphere.CellOf(fine.Unit(), nCoarse);
        double big = Math.PI * EarthR;

        double[] now = Materials.DivergentDistanceToFine(coarse, coarseFields, fine, big, nearest);
        double[] plain = Materials.FiniteResample(dist, nCoarse, fine, big, nearest);

        double h = coarse.Spacing;
        double[] uf = fine.Unit();
        double worst = 0.0;
        double closest = double.PositiveInfinity;
        double plainClosest = double.PositiveInfinity;
        int nNear = 0;
        for (int f = 0; f < now.Length; f++)
        {
            double x = uf[f * 3];
            double z = uf[(f * 3) + 2];
            double truth = EarthR * Math.Asin(Math.Abs(x));
            if (z > 0.3 && truth < h)
            {
                nNear++;
                worst = Math.Max(worst, Math.Abs(now[f] - truth));
                closest = Math.Min(closest, now[f]);
                plainClosest = Math.Min(plainClosest, plain[f]);
            }
            if (z < -0.3)
            {
                // 변환 구간: 경계 양쪽 부호가 반대여도 0 을 만들지 않고, 그대로 보간한 거리를 씁니다.
                Compare.Bits([plain[f]], [now[f]], $"변환 구간 L0 칸 {f}");
                Assert.True(now[f] > h, $"변환 구간 L0 칸 {f} 의 발산 경계 거리 {Km(now[f])} 가 거친 칸 하나({Km(h)})보다 짧습니다");
            }
        }
        // 거친 격자의 발산 경계 칸이 가진 가장 작은 거리(경계선까지 반 칸). 그대로 보간한 값은 이보다 작아질 수 없습니다.
        double halfCell = double.PositiveInfinity;
        for (int c = 0; c < nc; c++)
        {
            halfCell = isDiv[c] ? Math.Min(halfCell, dist[c]) : halfCell;
        }
        Assert.True(nNear > 50, $"경계선에서 거친 칸 하나 안의 L0 칸이 {nNear}개뿐입니다");
        Assert.True(worst < 0.05 * h, $"경계선까지 거리의 오차가 {Km(worst)} 로, 거친 칸({Km(h)})의 20분의 1을 넘습니다");
        Assert.True(closest < 0.05 * halfCell, $"경계선에 가장 가까운 L0 칸의 거리가 {Km(closest)} 로, 0 에 닿지 않습니다 (반 칸 {Km(halfCell)})");
        // 옛 방법(그대로 보간)은 반 칸 아래로 내려가지 않았습니다. 이 시험이 그 차이를 잡는지 확인합니다.
        Assert.True(plainClosest >= halfCell * (1.0 - 1e-12), $"그대로 보간한 거리의 최솟값 {Km(plainClosest)} 가 반 칸 {Km(halfCell)} 보다 작습니다");
    }

    // 같은 행성(지구 설정, 시드 0)을 거친 격자 면당 32·64·128칸으로 만들어 같은 L0(면당 128칸)로 옮깁니다. 프로필마다 다른 값은
    // 거친 격자의 크기이므로(tiny 16, laptop 128, lab 256), 거친 격자만 바꿔 봅니다. 고치기 전에는 면당 32·64칸에서 가라앉는 칸이
    // 하나도 없었고, 128칸에서는 가장 빠른 칸이 설정의 3분의 1 남짓이었습니다.
    [Fact]
    public void RiftSubsidenceIsAboutTheSameForEveryCoarseGrid()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        double axis = cfg.F("uplift.rift_subsidence_m_per_yr") + (cfg.F("uplift.craton_erosion_m_per_myr") * 1e-6);
        var rows = new List<(int N, int NSub, double Fastest, double Mean)>();
        foreach (int n in new[] { 32, 64, 128 })
        {
            (int nSub, double fastest, double mean) = RiftStats(cfg, n, 128);
            rows.Add((n, nSub, fastest, mean));
        }
        string table = string.Join("; ", rows.Select(r =>
            $"면당 {r.N}칸: 가라앉는 칸 {r.NSub}개, 가장 빠름 {r.Fastest:E2}, 평균 {r.Mean:E2} m/yr"));
        foreach ((int n, int nSub, double fastest, _) in rows)
        {
            Assert.True(nSub > 0, $"거친 격자 면당 {n}칸에서 가라앉는 칸이 없습니다 ({table})");
            // 축(거리 0)의 융기는 기본 깎임 + 열곡 침강 = 5e-6 − 1e-4 m/yr 입니다. 축에 가까운 L0 칸이 그 80% 넘게 가라앉아야 합니다.
            Assert.True(fastest < 0.8 * axis, $"거친 격자 면당 {n}칸에서 가장 빨리 가라앉는 칸이 {fastest:E2} m/yr 입니다 ({table})");
        }
        double slowest = rows.Max(r => r.Mean);
        double strongest = rows.Min(r => r.Mean);
        Assert.True(strongest / slowest < 1.5, $"가라앉는 칸의 평균 속도가 거친 격자에 따라 1.5배 넘게 다릅니다 ({table})");
    }

    // 부호를 정하는 '경계 건너편 판'은 경계 판정(ClassifyBoundaries)이 경계 칸에 적는 Other 와 같은 규칙입니다. 경계가 아닌 칸은 둘 다 −1 입니다.
    [Fact]
    public void AcrossPlateMatchesTheBoundaryClassification()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        CellGraph g = Graph.SphereGraph(16, cfg.F("planet.radius_m"));
        (FieldSet plates, OrderedDictionary<string, object?> info) = Plates.GeneratePlates(g, Crust.ContinentMask(g, cfg), cfg);
        int[] plate = plates.Get<int>("plate_id");
        double[] omega = (double[])((Bpcg.IO.NpyArray)info["omegas"]!).Data;
        BoundaryClassification cls = Plates.ClassifyBoundaries(g, plate, omega, cfg.F("plates.boundary_kappa"));
        int[] across = Plates.AcrossPlate(g.Nbr, plate);
        Assert.Equal(cls.Other, across);
        Assert.Contains(across, k => k >= 0);
    }

    // 1단계(거친 격자 재료 → L0)만 돌려 육지의 가라앉는 칸 수, 가장 빠른 속도, 평균 속도 [m/yr] 를 셉니다.
    private static (int NSub, double Fastest, double Mean) RiftStats(Config cfg, int nCoarse, int nFine)
    {
        double r = cfg.F("planet.radius_m");
        CellGraph coarse = Graph.SphereGraph(nCoarse, r);
        (FieldSet coarseFields, OrderedDictionary<string, object?> coarseInfo) = Materials.BuildMaterials(coarse, cfg);
        CellGraph fine = Graph.SphereGraph(nFine, r, cfg.F("landscape.jitter"), cfg.I("planet.seed"));
        (FieldSet fields, _) = Materials.TransferMaterials(coarse, coarseFields, coarseInfo, fine, cfg);
        double[] u = fields.GetFloat64("uplift_m_per_yr");
        bool[] ocean = fields.Get<bool>("is_ocean");
        int nSub = 0;
        double fastest = 0.0;
        double sum = 0.0;
        for (int c = 0; c < u.Length; c++)
        {
            if (!ocean[c] && u[c] < 0.0)
            {
                nSub++;
                fastest = Math.Min(fastest, u[c]);
                sum += u[c];
            }
        }
        return (nSub, fastest, nSub > 0 ? sum / nSub : 0.0);
    }

    private static string Km(double m) => (m / 1e3).ToString("F1", CultureInfo.InvariantCulture) + " km";
}
