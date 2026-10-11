using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hero;
using Xunit;

namespace Bpcg.Tests.Hero;

public sealed class HeroBoundaryInflowTests
{
    [Fact]
    public void DistributedBoundaryPortsConserveTotalInflowAndAreDeterministic()
    {
        (double[] first, long[] firstCells) = Refine.DistributeInflow(
            100,
            [(0, 1.0), (1, 2.0), (1, 1.0)],
            12345.6789);
        (double[] second, long[] secondCells) = Refine.DistributeInflow(
            100,
            [(0, 1.0), (1, 2.0), (1, 1.0)],
            12345.6789);

        Assert.Equal(first, second);
        Assert.Equal(firstCells, secondCells);
        Assert.Equal(12345.6789, Sum(first), 10);
        Assert.Equal(3_086.419725, first[0], 8);
        Assert.Equal(9_259.259175, first[1], 8);
        Assert.Equal(0.0, first[2]);
    }

    [Fact]
    public void DistributedBoundaryPortsRejectInvalidWeightsAndCells()
    {
        Assert.Throws<ArgumentException>(() => Refine.DistributeInflow(10, [(2, -1.0)], 1.0));
        Assert.Throws<ArgumentException>(() => Refine.DistributeInflow(10, [(10, 1.0)], 1.0));
        Assert.Throws<ArgumentException>(() => Refine.DistributeInflow(10, [], 1.0));
    }

    [Fact]
    public void HeroBoundaryV2UsesParentTributariesAndPreservesTotalWater()
    {
        const double radius = 6_371_000.0;
        CellGraph graph = Graph.SphereGraph(8, radius);
        int center = graph.NCells / 2;
        int[] neighbors = new int[3];
        int found = 0;
        for (int slot = 0; slot < CellGraph.NSlots && found < neighbors.Length; slot++)
        {
            int neighbor = graph.Nbr[(center * CellGraph.NSlots) + slot];
            if (neighbor >= 0)
            {
                neighbors[found++] = neighbor;
            }
        }
        Assert.Equal(3, found);

        long[] receiver = new long[graph.NCells];
        for (int c = 0; c < receiver.Length; c++)
        {
            receiver[c] = c;
        }
        receiver[center] = neighbors[0];
        receiver[neighbors[1]] = center;
        receiver[neighbors[2]] = center;
        double[] discharge = new double[graph.NCells];
        discharge[center] = 100.0;
        discharge[neighbors[1]] = 40.0;
        discharge[neighbors[2]] = 30.0;
        var fields = new FieldSet
        {
            ["receiver"] = receiver,
            ["discharge_m3_per_yr"] = discharge,
            ["z_m"] = new double[graph.NCells],
        };
        var state = new PlanetState(graph, fields, null!, [], []);
        double[] centerUnit =
        [graph.Pos[center * 3] / radius, graph.Pos[(center * 3) + 1] / radius, graph.Pos[(center * 3) + 2] / radius];
        (double[] east, double[] north) = Domain.TangentFrame(centerUnit, [0.0, 0.0, 1.0]);
        var site = new HeroSite(centerUnit, east, north, center, 0.0, new OrderedDictionary<string, double>(), 0.0, 0.0);
        Config cfg = Config.LoadConfig("earth", "tiny");
        (CellGraph heroGraph, _) = Domain.HeroGraph(site, cfg);
        double[] runoff = new double[heroGraph.NCells];

        OrderedDictionary<string, object?> v2 = Refine.HeroBoundary(state, site, heroGraph, runoff, cfg);
        OrderedDictionary<string, object?> legacy = Refine.HeroBoundary(state, site, heroGraph, runoff, cfg, legacySinglePort: true);
        double[] distributed = (double[])v2["extra_inflow"]!;
        double[] single = (double[])legacy["extra_inflow"]!;

        Assert.Equal(100.0, Sum(distributed), 12);
        Assert.Equal(100.0, Sum(single), 12);
        Assert.True(CountPositive(distributed) > 1);
        Assert.Equal(1, CountPositive(single));
        Assert.Equal("v2_distributed", v2["inflow_mode"]);
        Assert.Equal("legacy", legacy["inflow_mode"]);
    }

    private static double Sum(double[] values)
    {
        double sum = 0.0;
        foreach (double value in values)
        {
            sum += value;
        }
        return sum;
    }

    private static int CountPositive(double[] values)
    {
        int count = 0;
        foreach (double value in values)
        {
            if (value > 0.0)
            {
                count++;
            }
        }
        return count;
    }
}
