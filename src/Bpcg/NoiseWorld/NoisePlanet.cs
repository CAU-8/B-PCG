using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Planet;

namespace Bpcg.NoiseWorld;

/// <summary>
/// 히어로 유역의 자리와 그 둘레의 평면 좌표: 가운데 단위 벡터, 동쪽·북쪽 방향 (모두 행성 좌표 단위 벡터).
/// </summary>
/// <remarks>
/// 평면 좌표 (동 e, 북 n) [m] 는 <see cref="ToUnit"/> 으로 행성 위 자리가 됩니다: 가운데에서 e·동 + n·북 만큼 옮긴 점을 구 위로
/// 끌어내립니다. 그래서 히어로 유역과 회랑의 모든 표본이 행성 지도와 같은 세계 함수(<see cref="NoiseTerrain"/>)를 읽습니다.
/// </remarks>
public sealed class NoiseSite
{
    public NoiseSite(double[] center, double[] axis, double radiusM, long l0Cell, double score, double latDeg, double lonDeg)
    {
        Center = center;
        RadiusM = radiusM;
        L0Cell = l0Cell;
        Score = score;
        LatDeg = latDeg;
        LonDeg = lonDeg;
        double[] e =
        [
            (axis[1] * center[2]) - (axis[2] * center[1]),
            (axis[2] * center[0]) - (axis[0] * center[2]),
            (axis[0] * center[1]) - (axis[1] * center[0]),
        ];
        double ne = Math.Sqrt((e[0] * e[0]) + (e[1] * e[1]) + (e[2] * e[2]));
        East = ne > 1e-12 ? [e[0] / ne, e[1] / ne, e[2] / ne] : [1.0, 0.0, 0.0];
        North =
        [
            (center[1] * East[2]) - (center[2] * East[1]),
            (center[2] * East[0]) - (center[0] * East[2]),
            (center[0] * East[1]) - (center[1] * East[0]),
        ];
    }

    public double[] Center { get; }

    public double[] East { get; }

    public double[] North { get; }

    public double RadiusM { get; }

    public long L0Cell { get; }

    public double Score { get; }

    public double LatDeg { get; }

    public double LonDeg { get; }

    /// <summary>평면 좌표 (동 e, 북 n) [m] → 행성 위 단위 벡터.</summary>
    public (double X, double Y, double Z) ToUnit(double e, double n)
    {
        double x = Center[0] + (((e * East[0]) + (n * North[0])) / RadiusM);
        double y = Center[1] + (((e * East[1]) + (n * North[1])) / RadiusM);
        double z = Center[2] + (((e * East[2]) + (n * North[2])) / RadiusM);
        double r = Math.Sqrt((x * x) + (y * y) + (z * z));
        return (x / r, y / r, z / r);
    }
}

/// <summary>노이즈로 만든 행성 지도와 고른 히어로 자리.</summary>
public sealed record NoisePlanetResult(PlanetState State, NoiseSite Site, OrderedDictionary<string, object?> Info);

/// <summary>
/// 노이즈 비교본의 행성 지도 (B-PCG 의 L0 와 같은 큐브스피어 칸). 칸마다 세계 함수를 읽어 높이·바다·기온·강수·바이옴·지표 암석·강을
/// 정하고, 걸어 볼 히어로 유역 자리를 고릅니다.
/// </summary>
public static class NoisePlanet
{
    /// <summary>히어로 자리로 고를 수 있는 가장 높은 위도 [°] (B-PCG 히어로 찾기와 같음).</summary>
    public const double MaxLatDeg = 55.0;

    // 히어로 발자국 검사를 할 후보 수의 위 한계 (점수 순).
    private const int MaxFootprintChecks = 2_000;

    // 히어로 크기에서 점수를 다시 매길 후보 수.
    private const int SiteCandidates = 40;

    /// <summary>행성 지도를 만들고 히어로 자리를 고릅니다. terrain.SeaShiftM 을 이 행성에서 정합니다.</summary>
    public static NoisePlanetResult Build(NoiseTerrain terrain, Config cfg)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(cfg);
        int nL0 = (int)cfg.I("profile.grid.l0_n_per_face");
        long seed = cfg.I("planet.seed");
        CellGraph graph = Graph.SphereGraph(nL0, terrain.RadiusM, cfg.F("landscape.jitter"), seed);
        int n = graph.NCells;
        double[] unit = graph.Unit();
        double spacing = graph.Spacing;

        // 1. 해안 자리: 칸 넓이로 무게를 준 분포에서 바다 몫이 설정 값이 되는 대륙 노이즈 값과 높이.
        //    먼저 대륙 노이즈를 옮겨 해안선을 놓고(대륙붕·사면 모양은 그대로), 남은 작은 차이는 높이를 옮겨 맞춥니다.
        double oceanTarget = cfg.F("noise.sea.ocean_fraction");
        if (!(oceanTarget > 0.0 && oceanTarget < 1.0))
        {
            throw new ArgumentException($"noise.sea.ocean_fraction 은 0 과 1 사이여야 합니다 (받은 값 {oceanTarget}). 지구는 0.71");
        }
        double[] area = graph.Area;
        double totalArea = 0.0;
        for (int c = 0; c < n; c++)
        {
            totalArea += area[c];
        }
        double[] cont = new double[n];
        Parallelism.For(0, n, c => cont[c] = terrain.ContinentValue(unit[c * 3], unit[(c * 3) + 1], unit[(c * 3) + 2], spacing));
        terrain.ContinentShift = AreaQuantile(cont, area, oceanTarget * totalArea);
        double[] raw = new double[n];
        Parallelism.For(0, n, c => raw[c] = terrain.RawHeight(unit[c * 3], unit[(c * 3) + 1], unit[(c * 3) + 2], spacing));
        double shift = AreaQuantile(raw, area, oceanTarget * totalArea);
        terrain.SeaShiftM = shift;

        // 2. 칸마다 지표 값.
        double[] z = new double[n];
        bool[] ocean = new bool[n];
        double[] temp = new double[n];
        double[] precip = new double[n];
        byte[] rock = new byte[n];
        bool[] river = new bool[n];
        double[] mountain = new double[n];
        double[] riverDist = new double[n];
        double[] lat = new double[n];
        Parallelism.For(0, n, c =>
        {
            double ux = unit[c * 3];
            double uy = unit[(c * 3) + 1];
            double uz = unit[(c * 3) + 2];
            SurfacePoint sp = terrain.Surface(ux, uy, uz, spacing);
            z[c] = sp.HeightM;
            ocean[c] = sp.HeightM < 0.0;
            (temp[c], precip[c]) = terrain.Climate(ux, uy, uz, sp.HeightM);
            rock[c] = terrain.Bedrock(terrain.Column(ux, uy, uz), sp.HeightM - 10.0).Rock;
            // 강 골짜기(반폭 600 m)는 행성 지도 칸(약 20 km)보다 좁으므로, 강 가운데 줄이 칸 가운데에서 칸 간격의 4분의 1 안으로 지나는 칸을 강으로 칠합니다.
            river[c] = !ocean[c] && sp.RiverDistM < 0.25 * spacing;
            mountain[c] = sp.Mountain;
            riverDist[c] = sp.RiverDistM;
            lat[c] = terrain.LatitudeDeg(ux, uy, uz);
        });
        byte[] biome = Biomes.Classify(temp, precip, ocean, cfg);
        var fields = new FieldSet
        {
            ["z_m"] = z,
            ["is_ocean"] = ocean,
            ["temperature_c"] = temp,
            ["precip_m_per_yr"] = precip,
            ["biome"] = biome,
            ["surface_rock"] = rock,
            ["is_river"] = river,
        };
        double oceanArea = 0.0;
        for (int c = 0; c < n; c++)
        {
            if (ocean[c])
            {
                oceanArea += area[c];
            }
        }
        double oceanFraction = oceanArea / totalArea;

        // 3. 히어로 자리.
        NoiseSite site = ChooseSite(terrain, cfg, graph, unit, z, ocean, mountain, riverDist, lat, out string how);
        var info = new OrderedDictionary<string, object?>
        {
            ["n_per_face"] = (long)nL0,
            ["n_cells"] = (long)n,
            ["spacing_m"] = spacing,
            ["continent_shift"] = terrain.ContinentShift,
            ["sea_shift_m"] = shift,
            ["ocean_fraction"] = oceanFraction,
            ["ocean_fraction_target"] = oceanTarget,
            ["n_river_cells"] = (long)river.Count(b => b),
            ["site_how"] = how,
        };
        var state = new PlanetState(
            graph, fields, new LayerColumns([], new byte[n], new double[n], new double[n], 0), info, new OrderedDictionary<string, object?>());
        return new NoisePlanetResult(state, site, info);
    }

    // 값을 작은 것부터 쌓아 넓이가 처음 want 이상이 되는 칸의 값 (같은 값은 칸 번호 순).
    private static double AreaQuantile(double[] v, double[] area, double want)
    {
        int[] order = Enumerable.Range(0, v.Length).ToArray();
        Array.Sort(order, (a, b) => v[a] != v[b] ? v[a].CompareTo(v[b]) : a.CompareTo(b));
        double acc = 0.0;
        foreach (int c in order)
        {
            acc += area[c];
            if (acc >= want)
            {
                return v[c];
            }
        }
        return v[order[^1]];
    }

    // 히어로 자리 고르기. 설정 hero.site_lat_lon_deg 가 있으면 그 자리에서 가장 가까운 후보, 없으면 점수가 가장 높은 후보.
    // 후보는 육지이고 |위도| ≤ 55° 이고, 히어로 네모의 가운데·모서리·변 가운데 9곳이 모두 육지인 칸입니다.
    private static NoiseSite ChooseSite(
        NoiseTerrain terrain, Config cfg, CellGraph graph, double[] unit, double[] z, bool[] ocean, double[] mountain,
        double[] riverDist, double[] lat, out string how)
    {
        int n = graph.NCells;
        double size = cfg.F("profile.hero.size_m");
        double[] score = new double[n];
        var cand = new List<int>();
        for (int c = 0; c < n; c++)
        {
            if (ocean[c] || Math.Abs(lat[c]) > MaxLatDeg)
            {
                continue;
            }
            // 산지 + 가까운 강 + 높이. 걸어 볼 회랑에 산과 강이 함께 있게 합니다.
            score[c] = mountain[c] + (0.6 * (1.0 - NoiseMath.Smoothstep(0.0, 10_000.0, riverDist[c])))
                + (0.2 * NoiseMath.Smoothstep(0.0, 1_500.0, z[c]));
            cand.Add(c);
        }
        if (cand.Count == 0)
        {
            throw new RunError("노이즈 행성에 히어로 자리 후보가 없습니다 (육지이고 |위도| ≤ 55° 인 칸이 하나도 없음). "
                + "noise.sea.ocean_fraction 을 줄이거나 시드를 바꾸세요");
        }
        IReadOnlyList<object?> want = cfg.Contains("hero") && cfg.Sec("hero").Contains("site_lat_lon_deg")
            ? cfg.L("hero.site_lat_lon_deg")
            : [];
        double[]? target = null;
        if (want.Count == 2)
        {
            double la = Convert.ToDouble(want[0], System.Globalization.CultureInfo.InvariantCulture) * Math.PI / 180.0;
            double lo = Convert.ToDouble(want[1], System.Globalization.CultureInfo.InvariantCulture) * Math.PI / 180.0;
            // 자전축이 z 가 아니면 위도·경도를 행성 좌표로 돌려 놓습니다 (지구본과 같은 회전).
            double[] q = [Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la)];
            double[] rot = Globe.AxisRotation(terrain.Axis);
            target =
            [
                (rot[0] * q[0]) + (rot[3] * q[1]) + (rot[6] * q[2]),
                (rot[1] * q[0]) + (rot[4] * q[1]) + (rot[7] * q[2]),
                (rot[2] * q[0]) + (rot[5] * q[1]) + (rot[8] * q[2]),
            ];
        }
        IEnumerable<int> ordered = target is double[] t
            ? cand.OrderByDescending(c => (unit[c * 3] * t[0]) + (unit[(c * 3) + 1] * t[1]) + (unit[(c * 3) + 2] * t[2])).ThenBy(c => c)
            : cand.OrderByDescending(c => score[c]).ThenBy(c => c);
        // 자리를 정해 두었으면 그 자리에서 가장 가까운, 네모가 모두 육지인 후보. 아니면 점수 순으로 네모가 모두 육지인 후보를
        // SiteCandidates 개까지 모아, 히어로 크기에서 다시 매긴 점수(육지·기복·강 골짜기)가 가장 높은 곳.
        int tried = 0;
        NoiseSite? best = null;
        double bestScore = double.NegativeInfinity;
        int found = 0;
        foreach (int c in ordered)
        {
            if (tried++ >= MaxFootprintChecks)
            {
                break;
            }
            double[] center = [unit[c * 3], unit[(c * 3) + 1], unit[(c * 3) + 2]];
            (double la, double lo) = Globe.LatLonDeg(center, Globe.AxisRotation(terrain.Axis));
            var site = new NoiseSite(center, terrain.Axis, terrain.RadiusM, c, score[c], la, lo);
            if (!FootprintOnLand(terrain, site, size))
            {
                continue;
            }
            if (target is not null)
            {
                how = $"설정 hero.site_lat_lon_deg [{want[0]}, {want[1]}] 에서 가장 가까운 후보, {tried}번째로 본 칸";
                return site;
            }
            double s = HeroScore(terrain, site, size);
            if (s > bestScore)
            {
                bestScore = s;
                best = new NoiseSite(center, terrain.Axis, terrain.RadiusM, c, s, la, lo);
            }
            if (++found >= SiteCandidates)
            {
                break;
            }
        }
        if (best is not null)
        {
            how = $"행성 지도 점수가 높은 후보 {found}곳을 히어로 크기로 다시 매겨 가장 높은 곳, 후보 {LogText.N(cand.Count)}칸 가운데 {tried}칸을 봄";
            return best;
        }
        throw new RunError($"노이즈 행성에서 히어로 네모(한 변 {size / 1000.0:F1} km)가 모두 육지에 드는 자리를 찾지 못했습니다 "
            + $"(후보 {LogText.N(Math.Min(tried, MaxFootprintChecks))}칸을 봄). profile.hero.size_m 을 줄이거나 시드를 바꾸세요");
    }

    // 히어로 네모를 24 × 24 점으로 훑어 매긴 점수: 육지 몫 + 기복(800 m 면 가득) + 강 골짜기(점의 3% 면 가득).
    private static double HeroScore(NoiseTerrain terrain, NoiseSite site, double size)
    {
        const int k = 24;
        double step = size / k;
        var zs = new List<double>(k * k);
        int land = 0;
        int river = 0;
        for (int j = 0; j < k; j++)
        {
            for (int i = 0; i < k; i++)
            {
                (double x, double y, double zz) = site.ToUnit((-0.5 * size) + ((i + 0.5) * step), (0.5 * size) - ((j + 0.5) * step));
                SurfacePoint sp = terrain.Surface(x, y, zz, step);
                zs.Add(sp.HeightM);
                land += sp.HeightM > 0.0 ? 1 : 0;
                river += sp.Valley > 0.5 ? 1 : 0;
            }
        }
        zs.Sort();
        double relief = zs[(int)(0.95 * (zs.Count - 1))] - zs[(int)(0.05 * (zs.Count - 1))];
        return (2.0 * land / (k * k)) + Math.Min(relief / 800.0, 1.0) + Math.Min(river / (0.03 * k * k), 1.0);
    }

    private static bool FootprintOnLand(NoiseTerrain terrain, NoiseSite site, double size)
    {
        double half = 0.5 * size;
        double spacing = 0.25 * size;
        foreach (double e in new[] { -half, 0.0, half })
        {
            foreach (double nn in new[] { -half, 0.0, half })
            {
                (double x, double y, double zz) = site.ToUnit(e, nn);
                if (terrain.Surface(x, y, zz, spacing).HeightM <= 2.0)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
