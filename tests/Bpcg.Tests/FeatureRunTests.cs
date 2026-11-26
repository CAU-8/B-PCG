using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Bpcg.Subsurface;
using Bpcg.Volume;
using Xunit;

namespace Bpcg.Tests;

/// <summary>
/// 출력 지문 시험(RunDigestTests: 결과 파일마다 SHA-256 을 구해 저장해 둔 값과 비교하는 시험)이 돌리는 tiny 프로필·시드 0 에는
/// 세 기능이 생기지 않습니다. 석회암 땅에서 땅속으로 사라지는 개울(docs/pipeline.md 8.5), 그 개울이 들어가는 구멍(싱크홀)에서
/// 샘까지 이어지는 동굴 통로(10장 4번), 증발이 많아 물이 차다가 마는 호수(넘치지 못하는 호수, 8.1)입니다. 그래서 이 세 기능이
/// 바뀌거나 망가져도 지문 시험이 잡지 못합니다. 이 실행은 tiny 프로필에 설정 다섯 개를 덮어써 세 기능이 실제로 생기게 합니다.
/// 행성과 히어로 유역(행성에서 고른 강 유역 하나를 더 촘촘한 칸으로 다시 계산한 땅)을 메모리에서 만들고, 굽기가 쓰는 동굴
/// 그물(HeroVolume)까지 만듭니다. 파일은 쓰지 않습니다.
/// </summary>
public sealed class FeatureRun
{
    /// <summary>
    /// tiny 프로필에 덮어쓰는 설정. 기본 tiny 에서는 히어로 유역의 강이 동쪽에서 들어오는 큰 강(초당 400 m³ 이상) 하나뿐이고,
    /// 행성의 호수 자리는 얼음이 덮였던 추운 땅에만 생겨 모두 넘칩니다. 또 히어로 유역이 빨리 솟는 습곡충상대(지층이 휘고 밀려
    /// 겹친 띠, 지층 두께 2.6 km)에 놓여, 3천만 년 동안 깎인 두께(약 5.5 km)가 지층을 모두 지나 화강암 기반암만 드러납니다.
    /// </summary>
    public static readonly (string Key, object Value)[] Overrides =
    [
        // 초당 0.02 m³ 넘게 흐르는 칸을 강으로 봅니다(기본 0.3). 석회암 위의 작은 개울이 강이 되어, 그 가운데 일부가 땅속으로 사라집니다.
        ("rivers.min_discharge_m3_per_s", 0.02),
        // 산맥이 솟아 온 기간을 1,500만 년으로 봅니다(기본 3천만 년). 깎인 두께(융기 × 기간)가 약 2.8 km 로 줄어, 히어로 유역에
        // 화강암 대신 습곡충상대 지층(사암, 셰일, 석회암)이 드러납니다. 이 다섯 설정으로 tiny 시드 0 히어로 유역에 땅속으로 사라지는
        // 개울이 19곳 생깁니다(2026-11-26).
        ("uplift.orogen_duration_myr", 15.0),
        // 빙하기 기온을 지금보다 40 °C 낮게 봅니다(기본 6). 얼음이 덮였던 땅이 덥고 마른 땅까지 넓어집니다.
        ("lakes.lgm_cooling_c", 40.0),
        // 얼음이 덮였던 땅의 절반을 1,000 m 까지 낮춥니다(기본 3%, 300 m). 마른 땅에도 우묵한 자리가 생겨, 증발이 들어오는 물보다 많은 호수가 생깁니다.
        ("lakes.hollow_fraction", 0.5),
        ("lakes.hollow_depth_m", 1000.0),
    ];

    public FeatureRun()
    {
        Cfg = Config.LoadConfig("earth", "tiny").WithOverrides(Overrides.Select(o => new KeyValuePair<string, object?>(o.Key, o.Value)));
        Planet = Pipeline.GeneratePlanet(Cfg, null);
        Hero = Pipeline.GenerateHero(Cfg, Planet, null);
        Volume = new HeroVolume(Hero, Cfg);
    }

    public Config Cfg { get; }

    public PlanetState Planet { get; }

    public HeroState Hero { get; }

    /// <summary>굽기가 히어로 유역에서 만드는 동굴 통로 그물과 3D 샘플러 (재질 부피는 굽지 않음).</summary>
    public HeroVolume Volume { get; }
}

/// <summary>FeatureRun 의 결과가 세 기능의 약속을 지키는지 봅니다. 기능을 고쳐 정확한 값이 바뀌어도 이 성질은 그대로 맞아야 합니다.</summary>
public sealed class FeatureRunTests(FeatureRun run) : IClassFixture<FeatureRun>
{
    private static OrderedDictionary<string, object?> Stage(OrderedDictionary<string, object?> diag, string name) =>
        (OrderedDictionary<string, object?>)((OrderedDictionary<string, object?>)diag["stages"]!)[name]!;

    /// <summary>pick 이 참인 칸을 서로 이웃한 묶음으로 나눕니다.</summary>
    private static List<List<int>> Groups(CellGraph g, Func<int, bool> pick)
    {
        int n = g.NCells;
        bool[] seen = new bool[n];
        var groups = new List<List<int>>();
        var stack = new Stack<int>();
        for (int c0 = 0; c0 < n; c0++)
        {
            if (seen[c0] || !pick(c0))
            {
                continue;
            }
            var cells = new List<int>();
            seen[c0] = true;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                cells.Add(c);
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = g.Nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && !seen[v] && pick(v))
                    {
                        seen[v] = true;
                        stack.Push(v);
                    }
                }
            }
            groups.Add(cells);
        }
        return groups;
    }

    [Fact]
    public void ClosedLakesOnThePlanetEvaporateNoMoreWaterThanFlowsIn()
    {
        OrderedDictionary<string, object?> hollows = Stage(run.Planet.Diag, "hollows");
        long nClosed = (long)hollows["n_closed_lakes"]!;
        Assert.True(nClosed > 0, "덮어쓴 설정으로도 행성에 넘치지 못하는 호수가 생기지 않았습니다. 이 시험이 기능을 보지 못합니다");
        Assert.True((long)hollows["n_dry_basins"]! > 0, "덮어쓴 설정으로도 행성에 마른 분지가 생기지 않았습니다");

        CellGraph g = run.Planet.Graph;
        FieldSet f = run.Planet.Fields;
        int n = g.NCells;
        bool[] closed = f.Get<bool>("lake_closed");
        bool[] isLake = f.Get<bool>("is_lake");
        bool[] ocean = f.Get<bool>("is_ocean");
        long[] rcv = f.Get<long>("receiver");
        double[] z = f.GetFloat64("z_m");
        double[] level = f.GetFloat64("water_level_m");
        double[] runoff = f.GetFloat64("runoff_m_per_yr");
        double[] precip = f.GetFloat64("precip_m_per_yr");
        double[] pet = f.GetFloat64("pet_m_per_yr");

        // 물길이 끝나는 육지 칸. 솔버는 모든 땅을 바다까지 이어 놓으므로, 이런 칸은 물수지가 만든 칸뿐입니다
        // (넘치지 못하는 호수에 잠긴 칸과, 한 칸도 잠그지 못한 마른 분지의 가장 낮은 칸).
        bool[] sink = Enumerable.Range(0, n).Select(c => !ocean[c] && rcv[c] == c).ToArray();
        // 칸마다 생기는 물(유출 × 넓이)을 지금의 물길로 모으면, 물길이 끝나는 칸의 값이 그 칸으로 들어오는 물입니다.
        double[] made = Enumerable.Range(0, n).Select(c => Math.Max(runoff[c], 0.0) * g.Area[c]).ToArray();
        double[] q = AccumulateModule.Accumulate(rcv, Routing.TopoOrder(rcv), made);
        // 칸이 물에 잠기면 한 해에 더 드는 물 = (잠재 증발산 − 땅일 때의 실제 증발산) × 넓이, 실제 증발산 = 강수 − 유출.
        double[] cost = Enumerable.Range(0, n)
            .Select(c => Math.Max(pet[c] - Math.Max(precip[c] - Math.Max(runoff[c], 0.0), 0.0), 0.0) * g.Area[c]).ToArray();

        // 호수의 물은 바다로 나가지 않습니다: 넘치지 못하는 호수 칸에서 물길을 따라가면 육지에서 끝납니다.
        for (int c0 = 0; c0 < n; c0++)
        {
            if (!closed[c0])
            {
                continue;
            }
            Assert.True(isLake[c0], $"넘치지 못하는 호수 칸 {c0} 가 호수로 그려지지 않았습니다");
            Assert.True(level[c0] > z[c0], $"넘치지 못하는 호수 칸 {c0} 의 수면 {level[c0]} m 가 바닥 {z[c0]} m 보다 높지 않습니다");
            long c = c0;
            for (int step = 0; rcv[c] != c && step <= n; step++)
            {
                c = rcv[c];
            }
            Assert.True(sink[c], $"넘치지 못하는 호수 칸 {c0} 의 물이 바다(칸 {c})로 나갑니다");
        }
        foreach (List<int> lake in Groups(g, c => sink[c] || closed[c]))
        {
            if (!lake.Any(c => closed[c]))
            {
                // 마른 분지: 물길은 바닥에서 끝나지만 물이 고이지 않아 호수가 아닙니다.
                Assert.All(lake, c => Assert.False(isLake[c], $"마른 분지 바닥 칸 {c} 가 호수로 그려졌습니다"));
                continue;
            }
            // 넘치지 못하는 호수는 들어오는 물로 감당할 수 있는 만큼만 잠깁니다: 물이 고인 칸이 수면에서 더 증발시키는 물 ≤ 들어오는 물.
            double inflow = lake.Where(c => sink[c]).Sum(c => q[c]);
            double spent = lake.Where(c => closed[c]).Sum(c => cost[c]);
            Assert.True(
                spent <= inflow * (1.0 + 1e-9),
                $"넘치지 못하는 호수(칸 {string.Join(", ", lake)})가 수면에서 한 해 {spent:E3} m³ 를 더 증발시키는데, 들어오는 물은 {inflow:E3} m³ 뿐입니다");
        }
    }

    [Fact]
    public void StreamsOnLimestoneSinkAndTheirWaterComesBackAtSprings()
    {
        OrderedDictionary<string, object?> karst = Stage(run.Hero.Diag, "karst_streams");
        long nSinks = (long)karst["n_sinks"]!;
        Assert.True(nSinks > 0, "덮어쓴 설정으로도 히어로 유역에 땅속으로 사라지는 개울이 생기지 않았습니다. 이 시험이 기능을 보지 못합니다");
        Assert.True((long)karst["n_springs"]! > 0, "사라진 개울의 물이 다시 솟는 샘이 없습니다");
        Assert.True((bool)karst["settled"]!, "물을 다시 모으는 횟수 안에 샘의 자리가 정해지지 않았습니다");
        double lost = (double)karst["lost_m3_per_yr"]!;

        CellGraph g = run.Hero.Graph;
        FieldSet f = run.Hero.Fields;
        int n = g.NCells;
        double[] exchange = f.GetFloat64("karst_exchange_m3_per_yr");
        double[] q = f.GetFloat64("discharge_m3_per_yr");
        double[] runoffEff = f.GetFloat64("runoff_eff_m_per_yr");
        double[] z = f.GetFloat64("z_m");
        double[] lowerTable = f.GetFloat64("water_table_lower_m");
        long[] rcv = f.Get<long>("receiver");
        byte[] rock = f.Get<byte>("surface_rock");
        bool[] isRiver = f.Get<bool>("is_river");
        bool[] isLake = f.Get<bool>("is_lake");
        bool[] ocean = f.Get<bool>("is_ocean");
        double[]? extra = run.Hero.Diag.GetValueOrDefault("extra_inflow_m3_per_yr") as double[];

        // 땅속으로 사라진 물은 모두 샘에서 다시 솟습니다.
        Assert.Equal(nSinks, exchange.Count(v => v < 0.0));
        Assert.Equal(1.0, -exchange.Where(v => v < 0.0).Sum() / lost, 9);
        Assert.True(Math.Abs(exchange.Sum()) <= 1e-9 * lost, $"사라진 물과 솟은 물의 합이 {exchange.Sum():E3} m³/yr 만큼 다릅니다");

        // 칸마다 물수지: 칸에서 나가는 물 = 위쪽 칸에서 들어온 물 + 그 칸에서 생긴 물 + 땅속과 주고받은 물.
        double[] fromAbove = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (rcv[c] != c)
            {
                fromAbove[rcv[c]] += q[c];
            }
        }
        for (int c = 0; c < n; c++)
        {
            double made = (g.Area[c] * runoffEff[c]) + (extra is null ? 0.0 : extra[c]);
            double expect = fromAbove[c] + made + exchange[c];
            Assert.True(
                Math.Abs(q[c] - expect) <= 1e-9 * Math.Max(Math.Max(q[c], expect), 1.0),
                $"칸 {c} 에서 나가는 물 {q[c]:E6} m³/yr 이 들어온 물 + 생긴 물 + 주고받은 물 {expect:E6} 과 다릅니다");
        }

        double qMin = run.Cfg.F("rivers.min_discharge_m3_per_s") * Constants.SecondsPerYear;
        double maxDepth = run.Cfg.F("groundwater.max_depth_m");
        double aquitardLogK = run.Cfg.F("groundwater.aquitard_log10_perm_m2");
        foreach (int s in Enumerable.Range(0, n).Where(c => exchange[c] < 0.0))
        {
            // 강만큼의 물이 닿던 석회암·대리암 위의 칸에서, 닿은 물이 모두 땅속으로 들어가 그 아래 골짜기는 마릅니다.
            Assert.True(Rocks.Soluble[rock[s]], $"개울이 사라지는 칸 {s} 의 지표 암석({rock[s]})이 물에 녹는 암석이 아닙니다");
            Assert.Equal(0.0, q[s]);
            Assert.False(isRiver[s]);
            Assert.True(fromAbove[s] + (g.Area[s] * runoffEff[s]) >= qMin, $"칸 {s} 에 닿은 물이 강으로 치는 양보다 적습니다");
            // 개울 바닥은 석회암 지하수면보다 PerchMinM(10 m) 넘게 떠 있고, 동굴을 두는 깊이(max_depth_m) 안이며,
            // 그 사이에 물을 막는 층(셰일처럼 물이 거의 통하지 않는 층)이 없습니다.
            double drop = z[s] - lowerTable[s];
            Assert.InRange(drop, KarstStreams.PerchMinM, maxDepth);
            Assert.True(
                Groundwater.AquitardTop(run.Hero.Columns, s, z[s], aquitardLogK) < lowerTable[s],
                $"개울이 사라지는 칸 {s} 의 바닥과 지하수면 사이에 물을 막는 층이 있습니다");
        }
        foreach (int s in Enumerable.Range(0, n).Where(c => exchange[c] > 0.0))
        {
            // 샘은 석회암 지하수면의 기준이 되는 큰 물(강, 호수, 바다)에 있습니다.
            Assert.True(isRiver[s] || isLake[s] || ocean[s], $"샘 칸 {s} 이 강·호수·바다가 아닙니다");
        }
    }

    [Fact]
    public void EverySinkingStreamHasASinkholeWhoseWaterReachesASpring()
    {
        CaveNetwork net = run.Volume.Network;
        double[] exchange = run.Hero.Fields.GetFloat64("karst_exchange_m3_per_yr");
        int n = run.Hero.Graph.NCells;
        int nStream = net.SinkIsStream.Count(b => b);
        Assert.True(nStream > 0, "개울이 사라지는 자리에 싱크홀이 하나도 없습니다. 이 시험이 기능을 보지 못합니다");
        // 개울이 사라지는 칸은 싱크홀이 되거나, 아래에 샘까지 이어지는 동굴 층이 없어 세어 둔 칸입니다.
        Assert.Equal(exchange.Count(v => v < 0.0), nStream + net.NStreamSinksWithoutCave);
        int[] sinkAt = Enumerable.Repeat(-1, n).ToArray();
        for (int i = 0; i < net.Sinks.Length; i++)
        {
            sinkAt[net.Sinks[i]] = i;
            Assert.True(!net.SinkIsStream[i] || exchange[net.Sinks[i]] < 0.0, $"싱크홀 {net.Sinks[i]} 은 개울이 사라지는 칸이 아닌데 개울 싱크홀로 적혔습니다");
        }

        // 통로 칸마다 물수지: 칸을 지나는 물 = 위쪽 통로에서 흘러든 물 + 그 칸의 싱크홀로 들어온 물. 위층은 예전 물길이라,
        // 그 물길이 쓰이던 때에 같은 싱크홀 물이 흘렀다고 보고 층마다 따로 셉니다. 그래서 물수지도 층마다 따로 봅니다.
        bool[] fed = new bool[net.Sinks.Length];
        for (int k = 0; k < net.NLevels; k++)
        {
            double[] fromAbove = new double[n];
            for (int c = 0; c < n; c++)
            {
                int down = net.Parent[(k * n) + c];
                if (down >= 0)
                {
                    fromAbove[down] += net.Flow[(k * n) + c];
                }
            }
            for (int c = 0; c < n; c++)
            {
                double flow = net.Flow[(k * n) + c];
                double added = flow - fromAbove[c];
                double tol = 1e-9 * Math.Max(flow, 1.0);
                if (Math.Abs(added) <= tol)
                {
                    continue;
                }
                int i = sinkAt[c];
                Assert.True(
                    i >= 0 && Math.Abs(added - net.SinkDischarge[i]) <= tol,
                    $"층 {k} 의 칸 {c}: 지나는 물 {flow:E6} m³/yr 가 위쪽 통로에서 온 물 {fromAbove[c]:E6} 과 그 칸 싱크홀로 들어온 물의 합이 아닙니다");
                fed[i] = true;
            }
        }
        for (int i = 0; i < net.Sinks.Length; i++)
        {
            int s = net.Sinks[i];
            // 물이 들어가는 싱크홀은 적어도 한 층에 그 물을 보냅니다.
            Assert.True(fed[i] || !(net.SinkDischarge[i] > 0.0), $"싱크홀 {s} 로 들어간 물이 어느 층의 통로에도 없습니다");
            // 싱크홀은 샘까지 길이 닿는 동굴 층에만 둡니다. 그 길은 같은 칸을 두 번 지나지 않고 샘(다음 칸이 없는 통로 칸)에서 끝납니다.
            Assert.Contains(Enumerable.Range(0, net.NLevels), k => net.Parent[(k * n) + s] >= 0);
            for (int k = 0; k < net.NLevels; k++)
            {
                int c = s;
                int steps = 0;
                while (net.Parent[(k * n) + c] >= 0)
                {
                    c = net.Parent[(k * n) + c];
                    Assert.True(++steps <= n, $"싱크홀 {s} 의 층 {k} 물길이 샘에 닿지 않고 돕니다");
                }
            }
        }
    }
}
