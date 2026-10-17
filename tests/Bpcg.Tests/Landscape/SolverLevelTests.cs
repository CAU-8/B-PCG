using System;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>층별 높이 쌓기와 경사 법칙 표가 한 줄로 돌릴 때와 비트까지 같은지 (칸 32,768개 이상에서 층별 병렬이 켜짐).</summary>
public sealed class SolverLevelTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(300)]
    public void LawTableGivesSameValuesAsArrays(int kinds)
    {
        const int m = 50_000;
        double invN = 0.5;
        double[] kMult = new double[m];
        double[] sCrit = new double[m];
        for (int j = 0; j < m; j++)
        {
            int kind = (int)(Hashing.HashUnit(21, j, 0) * kinds);
            kMult[j] = 0.25 + (0.01 * kind);
            sCrit[j] = 0.4 + (0.001 * kind);
        }
        Solver.LawTable law = Solver.BuildLawTable(kMult, sCrit, invN);
        for (int j = 0; j < m; j++)
        {
            int slot = law.Slot(j);
            Compare.Bits(Math.Pow(kMult[j], -invN), law.KPow[slot], $"K 배율^(-1/n) [{j}]");
            Compare.Bits(sCrit[j], law.SCrit[slot], $"S_crit [{j}]");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SteadyStateSameForAnyThreadCount(bool useIncremental)
    {
        const int ny = 192;
        const int nx = 192;
        int n = ny * nx;
        CellGraph graph = Graph.FlatGraph(ny, nx, 25.0, 1.0, 5);
        bool[] outlet = new bool[n];
        for (int i = 0; i < nx; i++)
        {
            outlet[((ny - 1) * nx) + i] = true;
        }
        double[] bottom = new double[n * 2];
        byte[] rock = new byte[n * 3];
        for (int c = 0; c < n; c++)
        {
            bottom[c * 2] = 40.0 + (20.0 * Hashing.HashUnit(22, c, 0));
            bottom[(c * 2) + 1] = 10.0;
            rock[c * 3] = Rocks.Sandstone;
            rock[(c * 3) + 1] = Rocks.Limestone;
            rock[(c * 3) + 2] = Rocks.Granite;
        }
        LayerColumns layers = LayerColumns.FromColumns(bottom, rock, 2);
        Config cfg = Config.LoadConfig("earth", "tiny");

        SolverResult Solve(int threads)
        {
            Parallelism.Configure(threads);
            try
            {
                return Solver.SolveSteadyState(graph, outlet, [0.0], [1.0e-3], [0.5], layers, cfg, maxIter: 25,
                    useIncremental: useIncremental);
            }
            finally
            {
                Parallelism.Configure(null);
            }
        }

        SolverResult one = Solve(1);
        SolverResult four = Solve(4);
        Assert.Equal(one.Iterations, four.Iterations);
        Compare.Equal(one.Receiver, four.Receiver, "receiver");
        Compare.Bits(one.Z, four.Z, "z");
        Compare.Bits(one.Discharge, four.Discharge, "discharge");
    }
}
