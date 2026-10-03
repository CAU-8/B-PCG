using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Hero;

/// <summary>영역 가운데에서 그은 반직선이 가장자리와 만나는 곳 (EdgeHit).</summary>
public sealed record EdgeHit(string Edge, int Index, double X, double Y);

/// <summary>
/// 히어로 영역: 접평면 평면 격자와 구면 점 사이 변환 (src/bpcg/hero/domain.py, docs/pipeline.md 9장).
/// </summary>
public static class Domain
{
    private const long StreamHeroJitter = 9101;

    public static readonly string[] Edges = ["north", "south", "east", "west"];

    public const long MaxHeroCells = 5_000L * 5_000L;

    /// <summary>(한 변 칸 수 n, 간격 [m]) (hero_grid_size). n = round(size_m / spacing_m).</summary>
    public static (int N, double Spacing) HeroGridSize(Config cfg)
    {
        double size = cfg.F("profile.hero.size_m");
        double dx = cfg.F("profile.hero.spacing_m");
        if (!(double.IsFinite(size) && double.IsFinite(dx) && dx > 0 && size >= 3 * dx))
        {
            throw new ArgumentException($"profile.hero 는 spacing_m > 0, size_m ≥ 3·spacing_m 이어야 합니다: {size}, {dx}");
        }
        long n = (long)Math.Round(size / dx);
        if (n * n > MaxHeroCells)
        {
            long side = (long)Math.Sqrt(MaxHeroCells);
            throw new ArgumentException(
                $"히어로 {n}² = {(n * n).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 칸은 상한 {side}² 를 넘습니다. "
                + $"profile.hero.size_m 을 {side * dx:F0} m 이하로 줄이거나 spacing_m 을 {size / side:F1} m 이상으로 늘리세요");
        }
        return ((int)n, dx);
    }

    /// <summary>히어로 격자 진단값 (hero_grid_info).</summary>
    public static OrderedDictionary<string, object?> HeroGridInfo(Config cfg)
    {
        (int n, double dx) = HeroGridSize(cfg);
        return new OrderedDictionary<string, object?>
        {
            ["n_side"] = (long)n,
            ["spacing_m"] = dx,
            ["size_m"] = n * dx,
            ["requested_size_m"] = cfg.F("profile.hero.size_m"),
            ["n_cells"] = (long)n * n,
        };
    }

    /// <summary>히어로 노드 흔들기 시드 = hash3(planet.seed, 9101, 0) 의 위 31비트 (hero_jitter_seed).</summary>
    public static long HeroJitterSeed(Config cfg) => (long)(Hashing.Hash3(cfg.I("planet.seed"), StreamHeroJitter, 0) >> 33);

    /// <summary>히어로 평면 그래프 (hero_flat_graph): n×n, 가운데가 원점.</summary>
    public static CellGraph HeroFlatGraph(Config cfg)
    {
        (int n, double dx) = HeroGridSize(cfg);
        double half = 0.5 * n * dx;
        return Graph.FlatGraph(n, n, dx, cfg.F("landscape.jitter"), HeroJitterSeed(cfg), (-half, half));
    }

    /// <summary>중심 단위 벡터에서 동·북 단위 벡터 (tangent_frame).</summary>
    public static (double[] East, double[] North) TangentFrame(double[] center, double[] axis)
    {
        if (center.Length != 3 || axis.Length != 3)
        {
            throw new ArgumentException("center 와 axis 는 (3,) 벡터여야 합니다");
        }
        double nc = LinAlg.Norm3(center);
        double na = LinAlg.Norm3(axis);
        if (!(nc > 0 && na > 0))
        {
            throw new ArgumentException("center 와 axis 는 길이가 0 이 아니어야 합니다");
        }
        double[] c = [center[0] / nc, center[1] / nc, center[2] / nc];
        double[] a = [axis[0] / na, axis[1] / na, axis[2] / na];
        double[] e = new double[3];
        LinAlg.Cross3(a, c, e);
        if (LinAlg.Norm3(e) < 1e-9)
        {
            double[] alt = new double[3];
            alt[ArgMinAbs(c)] = 1.0;
            LinAlg.Cross3(alt, c, e);
        }
        double ne = LinAlg.Norm3(e);
        e = [e[0] / ne, e[1] / ne, e[2] / ne];
        double[] north = new double[3];
        LinAlg.Cross3(c, e, north);
        double nn = LinAlg.Norm3(north);
        return (e, [north[0] / nn, north[1] / nn, north[2] / nn]);
    }

    internal static int ArgMinAbs(double[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (Math.Abs(v[i]) < Math.Abs(v[best]))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>국소 (동 x, 북 y) [m] → 구면 단위 벡터 (M, 3) (local_to_unit).</summary>
    public static double[] LocalToUnit(HeroSite site, double[] x, double[] y, double radiusM)
    {
        if (x.Length != y.Length)
        {
            throw new ArgumentException($"x, y 는 같은 (M,) 모양이어야 합니다: ({x.Length},) ({y.Length},)");
        }
        double[] o = new double[x.Length * 3];
        for (int k = 0; k < x.Length; k++)
        {
            double px = (radiusM * site.CenterUnit[0]) + (x[k] * site.East[0]) + (y[k] * site.North[0]);
            double py = (radiusM * site.CenterUnit[1]) + (x[k] * site.East[1]) + (y[k] * site.North[1]);
            double pz = (radiusM * site.CenterUnit[2]) + (x[k] * site.East[2]) + (y[k] * site.North[2]);
            double nrm = Math.Sqrt((px * px) + (py * py) + (pz * pz));
            o[k * 3] = px / nrm;
            o[(k * 3) + 1] = py / nrm;
            o[(k * 3) + 2] = pz / nrm;
        }
        return o;
    }

    /// <summary>3D 방향 벡터를 접평면 국소 방향 (동, 북) 으로 (direction_to_local).</summary>
    public static (double East, double North) DirectionToLocal(HeroSite site, ReadOnlySpan<double> vector) =>
        (LinAlg.Dot3Np(vector, site.East), LinAlg.Dot3Np(vector, site.North));

    /// <summary>히어로 평면 그래프와 칸마다 L0 값을 읽을 구면 단위 벡터 (hero_graph).</summary>
    public static (CellGraph Graph, double[] UnitPoints) HeroGraph(HeroSite site, Config cfg)
    {
        CellGraph graph = HeroFlatGraph(cfg);
        int n = graph.NCells;
        double[] x = new double[n];
        double[] y = new double[n];
        for (int c = 0; c < n; c++)
        {
            x[c] = graph.Pos[c * 3];
            y[c] = graph.Pos[(c * 3) + 1];
        }
        return (graph, LocalToUnit(site, x, y, cfg.F("planet.radius_m")));
    }

    /// <summary>가운데에서 국소 방향으로 그은 반직선이 영역 가장자리와 만나는 곳 (edge_hit).</summary>
    public static EdgeHit EdgeHitOf((double X, double Y) direction, int n, double spacing)
    {
        double dx = direction.X;
        double dy = direction.Y;
        double m = NpMath.PyMax(Math.Abs(dx), Math.Abs(dy));
        if (!(double.IsFinite(m) && m > 0))
        {
            throw new ArgumentException($"방향 벡터가 0 이거나 유한하지 않습니다: ({dx}, {dy})");
        }
        double half = 0.5 * n * spacing;
        double t = half / m;
        double x = dx * t;
        double y = dy * t;
        string edge;
        int index;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            edge = dx > 0 ? "east" : "west";
            index = (int)Math.Clamp(Math.Floor((half - y) / spacing), 0, n - 1);
        }
        else
        {
            edge = dy > 0 ? "north" : "south";
            index = (int)Math.Clamp(Math.Floor((x + half) / spacing), 0, n - 1);
        }
        return new EdgeHit(edge, index, x, y);
    }

    /// <summary>가장자리 edge 를 따라 index 를 가운데로 한 연속 count 칸의 셀 번호 (edge_cells).</summary>
    public static long[] EdgeCells(int n, string edge, int index, int count = 1)
    {
        if (Array.IndexOf(Edges, edge) < 0)
        {
            throw new ArgumentException($"edge 는 ('north', 'south', 'east', 'west') 중 하나여야 합니다: '{edge}'");
        }
        if (!(count >= 1 && count <= n))
        {
            throw new ArgumentException($"count 는 1..{n} 이어야 합니다: {count}");
        }
        int start = Math.Clamp(index - (count / 2), 0, n - count);
        long[] o = new long[count];
        for (int k = 0; k < count; k++)
        {
            long v = start + k;
            o[k] = edge switch
            {
                "north" => v,
                "south" => ((long)(n - 1) * n) + v,
                "west" => v * n,
                _ => (v * n) + (n - 1),
            };
        }
        return o;
    }
}
