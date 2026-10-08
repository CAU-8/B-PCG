using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.IO;
using Bpcg.Numerics;

namespace Bpcg.Figures;

/// <summary>
/// 생성 결과를 그림으로 그립니다 (analysis/figures/render_results.py 를 옮김): 행성 지도·지구본·지구와 고도 분포 비교·
/// 히어로 지도·땅속 지도·단면·3D 조감, 그리고 summary.json.
/// </summary>
/// <remarks>
/// 입력은 <c>bpcg all</c> 이 쓴 폴더(planet/, hero/, corridor/)입니다. ETOPO 비교 곡선은 data/pilot 에 ETOPO 파일이 있을 때만 그립니다.
/// Python 판과 다른 점: 단면의 3D 샘플 설정은 'earth' + 프로필 대신 히어로 묶음에 적힌 설정을 쓰고,
/// 제목의 L0 칸 수(512)·히어로 칸 크기(25 m)는 결과에서 읽습니다.
/// </remarks>
public static class ResultFigures
{
    /// <summary>그리는 파일 이름 (그리는 순서).</summary>
    public static readonly string[] Files =
    [
        "planet_elevation_plain.png", "planet_elevation_faces.png", "planet_globes.png", "planet_layers.png",
        "hypsometry_vs_earth.png", "hero_map.png", "hero_subsurface.png", "cross_section.png", "hero_3d.png", "summary.json",
    ];

    /// <summary>
    /// run 폴더의 결과를 그려 outDir (없으면 run/figures) 에 씁니다. etopoPath 를 주지 않으면 data/pilot 의 기본 경로.
    /// log 는 그림 한 장을 쓸 때마다 부릅니다. 반환은 출력 폴더.
    /// </summary>
    public static string Render(string runDir, string? outDir = null, string? etopoPath = null, Action<string>? log = null)
    {
        string planetDir = Path.Combine(runDir, "planet"), heroDir = Path.Combine(runDir, "hero");
        string corridorManifest = Path.Combine(runDir, "corridor", "manifest.json");
        foreach (string need in new[] { Path.Combine(planetDir, "manifest.json"), Path.Combine(heroDir, "manifest.json"), corridorManifest })
        {
            if (!File.Exists(need))
            {
                throw new FileNotFoundException($"결과 그림에 필요한 파일이 없습니다: {need} (평면 히어로 실행은 행성 묶음이 없어 그리지 않습니다)");
            }
        }
        string outD = outDir ?? Path.Combine(runDir, "figures");
        Directory.CreateDirectory(outD);
        FigureKit.UseKoreanFont();
        void Wrote(string name) => log?.Invoke(Path.Combine(outD, name));

        PlanetState planet = Bundle.LoadPlanetState(planetDir);
        HeroState hero = Bundle.LoadHeroState(heroDir);
        FieldSet f = planet.Fields;
        int n = (int)planet.Graph.Shape[1];
        double[] zm = f.GetFloat64Any("z_mean_m");
        bool[] ocean = (bool[])FieldSet.CastTo(f["is_ocean"], typeof(bool));
        var summary = new OrderedDictionary<string, object?>();

        // 1~4. 행성
        (long[] Cells, int H, int W) eq = Projections.Equirect(n);
        PlanetFigures.Elevation(zm, n, eq, outD);
        Wrote("planet_elevation_plain.png");
        Wrote("planet_elevation_faces.png");
        PlanetFigures.Globes(zm, n, hero.Site?.CenterUnit, outD);
        Wrote("planet_globes.png");
        PlanetFigures.Layers(
            f.GetFloat64Any("plate_id"), (byte[])FieldSet.CastTo(f["boundary_type"], typeof(byte)), f.GetFloat64Any("ocean_age_myr"),
            f.GetFloat64Any("precip_m_per_yr"), f.GetFloat64Any("uplift_m_per_yr"), ocean, eq, outD);
        Wrote("planet_layers.png");
        PlanetFigures.Hypsometry(zm, planet.Graph.Area, etopoPath ?? Etopo.DefaultPath, outD, summary);
        Wrote("hypsometry_vs_earth.png");
        double[] area = planet.Graph.Area;
        double[] oceanArea = new double[area.Length];
        for (int c = 0; c < area.Length; c++)
        {
            oceanArea[c] = ocean[c] ? area[c] : 0.0;
        }
        summary["bpcg_ocean_fraction"] = NpReduce.Sum(oceanArea) / NpReduce.Sum(area);

        // 5~8. 히어로
        var man = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(corridorManifest, Encoding.UTF8))!;
        var corridor = (OrderedDictionary<string, object?>)man["corridor"]!;
        var rect = new OrderedDictionary<string, double>();
        foreach (KeyValuePair<string, object?> kv in (OrderedDictionary<string, object?>)corridor["rect_local_m"]!)
        {
            rect[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
        }
        var hf = new HeroFigures(hero);
        hf.Map(rect, outD);
        Wrote("hero_map.png");
        hf.Subsurface(outD);
        Wrote("hero_subsurface.png");
        Config cfg = Bundle.ConfigFromManifest(Bundle.ReadManifest(heroDir));
        summary["cross_section"] = hf.CrossSection(rect, cfg, outD);
        Wrote("cross_section.png");
        hf.Bird3D(outD);
        Wrote("hero_3d.png");

        summary["planet_iterations"] = SolverIterations(planet.Diag);
        summary["hero_iterations"] = SolverIterations(hero.Diag);
        summary["hero_z_range_m"] = new List<object?> { hf.ZMin, hf.ZMax };
        summary["hero_site"] = new OrderedDictionary<string, object?>
        {
            ["lat"] = hero.Site is null || double.IsNaN(hero.Site.LatDeg) ? null : hero.Site.LatDeg,
            ["lon"] = hero.Site is null || double.IsNaN(hero.Site.LonDeg) ? null : hero.Site.LonDeg,
        };
        string json = PyJson.Dumps(summary, new PyJson.Options(Indent: 1, EnsureAscii: false));
        File.WriteAllText(Path.Combine(outD, "summary.json"), json, new UTF8Encoding(false));
        Wrote("summary.json");
        return outD;
    }

    private static object? SolverIterations(OrderedDictionary<string, object?> diag) =>
        diag.GetValueOrDefault("solver") is OrderedDictionary<string, object?> s ? s.GetValueOrDefault("iterations") : null;
}
