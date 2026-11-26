using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 해안의 기준 고도 (docs/pipeline.md 4.2 '전이'). 대륙 지각과 바다 지각이 만나는 선 양쪽 50 km 안의 칸은 대륙 높이와
/// 바다 높이를 섞습니다. 섞을 반대쪽 높이는 맞은편 지각의 칸에서 가져와야 합니다. 바다 칸이 자기 칸의 '대륙 높이'
/// (두께 7 km 지각을 대륙 식에 넣은 약 −5,800 m)를 섞으면, 해안 칸이 같은 나이의 먼 바다보다 깊어집니다.
/// golden 자료는 면당 8·16칸 격자(칸 한 변 약 1,250 km·625 km)라 이 띠에 드는 칸이 없어서, 작은 행성으로 따로 봅니다.
/// </summary>
public sealed class CrustCoastTests
{
    private const int NSlots = CellGraph.NSlots;

    // 칸 0·1 이 region 안, 칸 2·3 이 밖인 한 줄 격자입니다. 이웃은 왼쪽과 오른쪽 하나씩이고, 칸 사이 거리는 10 m 입니다.
    [Fact]
    public void EdgeCellsMarksOnlyRegionCellsNextToTheOutside()
    {
        const int n = 4;
        int[] nbr = new int[n * NSlots];
        double[] dist = new double[n * NSlots];
        Array.Fill(nbr, -1);
        for (int c = 0; c < n; c++)
        {
            nbr[c * NSlots] = c - 1;                           // 왼쪽 (칸 0 은 −1, 이웃 없음)
            nbr[(c * NSlots) + 1] = c + 1 < n ? c + 1 : -1;    // 오른쪽
            dist[c * NSlots] = 10.0;
            dist[(c * NSlots) + 1] = 10.0;
        }

        (bool[] edge, double[] half) = Crust.EdgeCells(nbr, [true, true, false, false], dist);
        Assert.Equal([false, true, false, false], edge);       // 칸 2 는 바깥 칸이라 가장자리가 아닙니다
        Assert.Equal([0.0, 5.0, 0.0, 0.0], half);

        (bool[] edgeOut, double[] halfOut) = Crust.EdgeCells(nbr, [false, false, true, true], dist);
        Assert.Equal([false, false, true, false], edgeOut);
        Assert.Equal([0.0, 0.0, 5.0, 0.0], halfOut);
    }

    // 반지름 600 km 인 작은 행성(면당 16칸, 칸 간격 약 59 km)에 둥근 대륙 하나를 둡니다. 그러면 경계에 맞닿은 칸이
    // 양쪽 50 km 띠에 듭니다. 바다 바닥은 모두 같은 나이입니다: 해령에서 1,000 km, 한쪽 확장 속도 2.5 cm/yr 라
    // 4,000만 년이고, 깊이는 2,500 + 350 × √40 ≈ 4,714 m 입니다. 수렴 경계가 없어 해구와 충돌대도 없습니다.
    [Fact]
    public void CoastCellsBlendTheHeightOfTheOppositeCrust()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        CellGraph g = Graph.SphereGraph(16, 600_000.0);
        int n = g.NCells;
        bool[] cont = CapMask(g, axis: (1.0, 2.0, 3.0), radiusDeg: 50.0);
        var plates = new FieldSet
        {
            ["dist_divergent_m"] = Filled(n, 1_000_000.0),
            ["spreading_m_per_yr"] = Filled(n, 0.025),
            ["dist_convergent_m"] = Filled(n, double.PositiveInfinity),
            ["convergence_kind"] = new byte[n],
            ["subduction_side"] = new sbyte[n],
        };

        (FieldSet fields, OrderedDictionary<string, object?> info) = Crust.GenerateCrust(g, plates, cont, cfg);
        double[] zBase = (double[])info["z_platform_base_m"]!;
        double[] signed = (double[])info["coast_signed_m"]!;
        double[] zCont = Crust.AiryElevationM(fields.Get<double>("crust_thickness_m"), cfg);
        double[] age = fields.Get<double>("ocean_age_myr");
        double ridge = cfg.F("ocean.ridge_depth_m");

        // 바다 칸의 바다 높이는 모두 같습니다.
        double zOcean = double.NaN;
        for (int c = 0; c < n; c++)
        {
            if (!cont[c])
            {
                double z = -Crust.OceanDepthM(age[c], ridge);
                zOcean = double.IsNaN(zOcean) ? z : zOcean;
                Assert.Equal(zOcean, z);
            }
        }
        Assert.InRange(zOcean, -4_714.0, -4_713.0);

        // 대륙 가장자리 칸(바다와 맞닿은 대륙 칸)의 대륙 높이 범위. 지각이 얇아지는 가장자리 띠(margin_width_m) 안이라
        // 약 −2,800 m 입니다. 바다 칸이 자기 칸의 대륙 높이를 쓰면 이 범위 대신 약 −5,800 m 가 섞입니다.
        double edgeLo = double.PositiveInfinity;
        double edgeHi = double.NegativeInfinity;
        for (int c = 0; c < n; c++)
        {
            if (cont[c] && HasNeighbor(g, c, v => !cont[v]))
            {
                edgeLo = Math.Min(edgeLo, zCont[c]);
                edgeHi = Math.Max(edgeHi, zCont[c]);
            }
        }
        Assert.True(edgeLo > -4_000.0, $"대륙 가장자리 칸의 대륙 높이가 너무 낮습니다: {edgeLo:F1} m");

        int nSea = 0;
        int nLand = 0;
        const double band = Crust.TransitionHalfWidthM;
        for (int c = 0; c < n; c++)
        {
            if (!(Math.Abs(signed[c]) < band))
            {
                continue;
            }
            double w = Crust.Smoothstep((signed[c] + band) / (2.0 * band));
            if (cont[c])
            {
                // 대륙 쪽 해안 칸: 자기 대륙 높이와, 맞은편 바다 칸의 바다 높이(−4,714 m)를 섞습니다.
                // 자기 칸의 바다 높이(나이 0 인 해령 깊이 −2,500 m)를 섞으면 안 됩니다.
                Assert.Equal((w * zCont[c]) + ((1.0 - w) * zOcean), zBase[c], 6);
                nLand++;
            }
            else
            {
                // 바다 쪽 해안 칸: 같은 나이의 먼 바다보다 얕고, 섞은 대륙 높이는 대륙 가장자리 칸 높이 범위 안입니다.
                Assert.True(zBase[c] > zOcean, $"바다 쪽 해안 칸 {c} 이 먼 바다보다 깊습니다: {zBase[c]:F1} m < {zOcean:F1} m");
                Assert.InRange(zBase[c], (w * edgeLo) + ((1.0 - w) * zOcean) - 1e-6, (w * edgeHi) + ((1.0 - w) * zOcean) + 1e-6);
                nSea++;
            }
        }
        Assert.True(nSea >= 20 && nLand >= 20, $"띠 안의 칸이 너무 적습니다: 바다 쪽 {nSea}개, 대륙 쪽 {nLand}개");
    }

    private static bool[] CapMask(CellGraph g, (double X, double Y, double Z) axis, double radiusDeg)
    {
        double norm = Math.Sqrt((axis.X * axis.X) + (axis.Y * axis.Y) + (axis.Z * axis.Z));
        double cosR = Math.Cos(radiusDeg * Math.PI / 180.0);
        double[] unit = g.Unit();
        bool[] mask = new bool[g.NCells];
        for (int c = 0; c < mask.Length; c++)
        {
            double dot = ((unit[c * 3] * axis.X) + (unit[(c * 3) + 1] * axis.Y) + (unit[(c * 3) + 2] * axis.Z)) / norm;
            mask[c] = dot > cosR;
        }
        return mask;
    }

    private static bool HasNeighbor(CellGraph g, int c, Func<int, bool> test)
    {
        for (int s = 0; s < NSlots; s++)
        {
            int v = g.Nbr[(c * NSlots) + s];
            if (v >= 0 && test(v))
            {
                return true;
            }
        }
        return false;
    }

    private static double[] Filled(int n, double value)
    {
        double[] a = new double[n];
        Array.Fill(a, value);
        return a;
    }
}
