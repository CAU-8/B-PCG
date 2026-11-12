using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

public sealed class SolverParallelTests
{
    [Fact]
    public void IncrementalSolverMatchesLegacyStateAndRecordsAffectedRegions()
    {
        Config cfg = Config.LoadConfig("earth", "tiny").WithOverrides(
        [
            new KeyValuePair<string, object?>("planet.seed", 421L),
            new KeyValuePair<string, object?>("landscape.max_flow_iterations", 12L),
        ]);
        CellGraph graph = Graph.FlatGraph(64, 64, 1000.0);
        bool[] outlets = graph.BoundaryMask();

        try
        {
            Parallelism.Configure(1);
            SolverResult legacy = Solver.SolveSteadyState(graph, outlets, [0.0], [0.0002], [0.0005], null, cfg,
                useIncremental: false);
            SolverResult incremental = Solver.SolveSteadyState(graph, outlets, [0.0], [0.0002], [0.0005], null, cfg,
                useIncremental: true, verifyIncremental: true);

            Assert.Equal(legacy.Z, incremental.Z);
            Assert.Equal(legacy.Receiver, incremental.Receiver);
            Assert.Equal(legacy.Discharge, incremental.Discharge);
            Assert.Equal(legacy.DrainageArea, incremental.DrainageArea);
            Assert.Equal(legacy.SedimentFlux, incremental.SedimentFlux);
            Assert.Equal(legacy.History.Count, incremental.History.Count);
            Assert.All(incremental.History, row =>
            {
                Assert.Contains("receiver_changed_cells", row.Keys);
                Assert.Contains("dirty_hydrology_cells", row.Keys);
                Assert.Contains("dirty_integration_cells", row.Keys);
                Assert.Contains("routing_seconds", row.Keys);
                Assert.Contains("accumulation_seconds", row.Keys);
                Assert.Contains("integration_seconds", row.Keys);
            });
            Assert.Contains(incremental.History, row => Equals(row["incremental"], true));
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    /// <summary>
    /// 가라앉는 땅(융기 &lt; 0, 열곡)이 섞여 있어도 증분 누적이 전체 누적과 같은 답을 내는지 봅니다.
    /// 퇴적물 유량은 누적한 뒤 0 아래를 자르는데, 자른 값을 다음 반복의 증분 누적이 다시 읽으면 하류 값이 커집니다.
    /// </summary>
    [Fact]
    public void IncrementalSolverMatchesLegacyWithSubsidingCells()
    {
        Config cfg = Config.LoadConfig("earth", "tiny").WithOverrides(
        [
            new KeyValuePair<string, object?>("planet.seed", 421L),
            new KeyValuePair<string, object?>("landscape.max_flow_iterations", 12L),
        ]);
        const int Side = 64;
        CellGraph graph = Graph.FlatGraph(Side, Side, 1000.0);
        bool[] outlets = graph.BoundaryMask();
        // 가운데 띠(열 24~39)는 −0.1 mm/yr 로 가라앉고, 나머지는 0.2 mm/yr 로 솟습니다.
        double[] uplift = new double[graph.NCells];
        for (int c = 0; c < uplift.Length; c++)
        {
            int col = c % Side;
            uplift[c] = col is >= 24 and < 40 ? -0.0001 : 0.0002;
        }

        try
        {
            Parallelism.Configure(1);
            SolverResult legacy = Solver.SolveSteadyState(graph, outlets, [0.0], uplift, [0.0005], null, cfg,
                useIncremental: false);
            SolverResult incremental = Solver.SolveSteadyState(graph, outlets, [0.0], uplift, [0.0005], null, cfg,
                useIncremental: true, verifyIncremental: true);

            Assert.Contains(incremental.History, row => Equals(row["incremental"], true));
            Assert.Equal(legacy.SedimentFlux, incremental.SedimentFlux);
            Assert.Equal(legacy.Z, incremental.Z);
            Assert.Equal(legacy.Receiver, incremental.Receiver);
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }

    [Fact]
    public void SolverProducesBitIdenticalFieldsWithParallelKernels()
    {
        Config cfg = Config.LoadConfig("earth", "tiny").WithOverrides(
        [
            new KeyValuePair<string, object?>("planet.seed", 6226L),
            new KeyValuePair<string, object?>("landscape.max_flow_iterations", 8L),
        ]);
        CellGraph graph = Graph.FlatGraph(160, 160, 1000.0);
        bool[] outlets = graph.BoundaryMask();
        double[] uplift = [0.0002];
        double[] runoff = [0.0005];

        try
        {
            Parallelism.Configure(1);
            SolverResult serial = Solver.SolveSteadyState(graph, outlets, [0.0], uplift, runoff, null, cfg);
            Parallelism.Configure(2);
            SolverResult parallel = Solver.SolveSteadyState(graph, outlets, [0.0], uplift, runoff, null, cfg);

            Assert.Equal(serial.Z, parallel.Z);
            Assert.Equal(serial.Receiver, parallel.Receiver);
            Assert.Equal(serial.Discharge, parallel.Discharge);
            Assert.Equal(serial.DrainageArea, parallel.DrainageArea);
            Assert.Equal(serial.SedimentFlux, parallel.SedimentFlux);
            Assert.Equal(serial.Slope, parallel.Slope);
            Assert.Equal(serial.KS, parallel.KS);
            Assert.Equal(serial.History.Count, parallel.History.Count);
            Assert.Equal(serial.Converged, parallel.Converged);
            Assert.True(serial.RoutingSeconds >= 0.0);
            Assert.True(serial.AccumulationSeconds >= 0.0);
            Assert.True(serial.IntegrationSeconds >= 0.0);
            Assert.True(serial.SurfaceLawSeconds >= 0.0);
            Assert.True(serial.RoutingSeconds + serial.AccumulationSeconds + serial.IntegrationSeconds
                <= serial.Seconds + 0.01);
        }
        finally
        {
            Parallelism.Configure(null);
        }
    }
}
