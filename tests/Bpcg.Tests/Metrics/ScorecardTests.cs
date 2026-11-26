using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Metrics;
using Xunit;

namespace Bpcg.Tests.Metrics;

/// <summary>
/// 점수표 지표의 손 사례 (docs/pipeline.md 12장): 격자 정렬 지수의 무작위 기준, 칸마다 견주는 수지 오차, 계산 실패 표시.
/// </summary>
public sealed class ScorecardTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.4142135623730951, 1.0)]
    [InlineData(2.0, 0.4015086683702013)]
    [InlineData(6.0, 0.186012660842851)]
    [InlineData(8.0, 0.11446189201515994)]
    public void AlignmentNullKnownValues(double r, double expected) =>
        Assert.Equal(expected, Drainage.AlignmentNull(r), 1e-12);

    /// <summary>각도를 촘촘히 나눠 끝점을 반올림해 센 비율이 식으로 구한 확률과 같은지 봅니다.</summary>
    [Theory]
    [InlineData(2.5)]
    [InlineData(5.0)]
    [InlineData(5.656854249492381)]
    [InlineData(7.3)]
    public void AlignmentNullMatchesBruteForce(double r)
    {
        const int k = 200_000;
        int hit = 0;
        for (int i = 0; i < k; i++)
        {
            double phi = (i + 0.5) * (Math.PI / 4.0) / k;
            long x = (long)Math.Round(r * Math.Cos(phi), MidpointRounding.ToEven);
            long y = (long)Math.Round(r * Math.Sin(phi), MidpointRounding.ToEven);
            hit += y == 0 || x == y ? 1 : 0;
        }
        Assert.Equal((double)hit / k, Drainage.AlignmentNull(r), 1e-4);
    }

    /// <summary>
    /// 기대값을 반올림한 변위의 길이로 구해도 치우치지 않는지 봅니다. 고른 방향, 고른 길이(lo~hi 칸)의 직선을 반올림했을 때
    /// 실제로 정렬된 비율과, 반올림한 변위 길이마다 AlignmentNull 을 더한 기대 비율이 3% 안에서 같아야 합니다.
    /// </summary>
    [Theory]
    [InlineData(1.0, 8.5)]
    [InlineData(3.0, 8.5)]
    [InlineData(5.5, 8.5)]
    public void AlignmentNullOfRoundedLengthIsUnbiased(double lo, double hi)
    {
        const int nl = 300;
        const int na = 720;
        var cache = new Dictionary<long, double>();
        double hit = 0.0;
        double expected = 0.0;
        for (int a = 0; a < nl; a++)
        {
            double len = lo + ((a + 0.5) * (hi - lo) / nl);
            for (int b = 0; b < na; b++)
            {
                double phi = (b + 0.5) * (Math.PI / 4.0) / na;
                long x = (long)Math.Round(len * Math.Cos(phi), MidpointRounding.ToEven);
                long y = (long)Math.Round(len * Math.Sin(phi), MidpointRounding.ToEven);
                hit += y == 0 || x == y ? 1.0 : 0.0;
                long r2 = (x * x) + (y * y);
                if (!cache.TryGetValue(r2, out double p))
                {
                    p = Drainage.AlignmentNull(Math.Sqrt(r2));
                    cache[r2] = p;
                }
                expected += p;
            }
        }
        Assert.InRange(hit / expected, 0.97, 1.03);
    }

    [Fact]
    public void AlignmentNullRejectsShortLength() =>
        Assert.Throws<ArgumentException>(() => Drainage.AlignmentNull(0.5));

    /// <summary>모든 강이 동쪽으로 곧게 흐르면 모두 정렬되고, 지수는 6칸 변위의 무작위 확률의 역수입니다.</summary>
    [Fact]
    public void StraightEastRiversAreFullyAligned()
    {
        const int ny = 5;
        const int nx = 20;
        CellGraph g = Graph.FlatGraph(ny, nx, 25.0);
        long[] rcv = new long[ny * nx];
        bool[] river = new bool[ny * nx];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = (j * nx) + i;
                rcv[c] = i == nx - 1 ? c : c + 1;
                river[c] = true;
            }
        }
        (long hit, long used, double expected) = Drainage.AlignmentCounts(g, rcv, river);
        // 6칸을 따라갈 수 있는 칸은 줄마다 nx − 6 개입니다.
        Assert.Equal(ny * (nx - 6), used);
        Assert.Equal(used, hit);
        Assert.Equal(used * Drainage.AlignmentNull(6.0), expected, 1e-9);
        Assert.Equal(1.0 / Drainage.AlignmentNull(6.0), Drainage.GridAlignment(g, rcv, river), 1e-12);
    }

    /// <summary>
    /// 작은 개울이 10% 를 잃으면, 큰 강이 함께 흘러 전체 합에 견준 오차는 2e-10 이어도 칸마다 견준 오차는 0.1 입니다.
    /// 칸 0 이 출구, 칸 1 은 큰 강, 칸 3 → 2 → 0 은 개울입니다.
    /// </summary>
    [Fact]
    public void BudgetErrorCatchesSmallCreekLeak()
    {
        long[] rcv = [0, 0, 0, 2];
        double[] source = [0.0, 1.0e9, 1.0, 1.0];
        double[] exact = [1.0e9 + 2.0, 1.0e9, 2.0, 1.0];
        Assert.Equal(0.0, Drainage.BudgetError(rcv, exact, source));
        double[] leak = [1.0e9 + 1.8, 1.0e9, 1.8, 1.0];
        Assert.Equal(0.1, Drainage.BudgetError(rcv, leak, source), 1e-12);
    }

    /// <summary>들어온 양이 없는 칸에 흐름이 생기면 무한대입니다.</summary>
    [Fact]
    public void BudgetErrorIsInfiniteForFlowFromNothing()
    {
        long[] rcv = [0, 0];
        double[] source = [0.0, 0.0];
        Assert.True(double.IsPositiveInfinity(Drainage.BudgetError(rcv, [0.0, 1.0], source)));
    }

    /// <summary>
    /// 판정이 없는 종류(emergent)의 계산이 예외로 끝나도 note 머리 '계산 실패: ' 와 요약의 errors 로 드러납니다.
    /// Hack 지수의 최소 넓이를 0 으로 두면 HackFit 이 ArgumentException 을 던집니다.
    /// </summary>
    [Fact]
    public void ComputeFailureOfEmergentItemIsReported()
    {
        CellGraph g = Graph.FlatGraph(3, 3, 25.0);
        var fields = new FieldSet
        {
            ["receiver"] = new long[] { 0, 0, 1, 0, 0, 4, 3, 4, 5 },
            ["drainage_area_m2"] = new double[9],
        };
        Config cfg = Config.FromDict(new OrderedDictionary<string, object?>
        {
            ["metrics"] = new OrderedDictionary<string, object?> { ["hack_min_area_cells"] = 0.0 },
        });
        OrderedDictionary<string, object?> card = Scorecard.Build(heroFields: fields, heroGraph: g, cfg: cfg);
        var hack = (OrderedDictionary<string, object?>)card["hero.hack_exponent"]!;
        Assert.Null(hack["value"]);
        Assert.Null(hack["pass"]);
        Assert.Equal(Scorecard.Emergent, hack["kind"]);
        Assert.StartsWith(Scorecard.ErrorPrefix, (string)hack["note"]!, StringComparison.Ordinal);
        Assert.Equal(new List<string> { "hero.hack_exponent" }, Scorecard.ComputeErrors(card));
        OrderedDictionary<string, object?> summary = Pipeline.ScorecardSummary(card);
        Assert.Equal(1L, summary["n_errors"]);
        Assert.Equal(0L, summary["n_failed"]);
        Assert.Equal("불합격 없음, 계산 실패 1개 (hero.hack_exponent)", Pipeline.ScorecardLogText(summary));
    }
}
