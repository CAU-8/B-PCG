using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.IO;

namespace Bpcg.NoiseWorld;

/// <summary>
/// 노이즈 비교본 실행 (<c>bpcg noise</c>, 엔진 처음 화면의 '노이즈 비교본'): 행성 지도 → 히어로 자리 → 히어로 유역 → 회랑 굽기 → 지구본.
/// </summary>
/// <remarks>
/// 실행 폴더는 B-PCG 실행(<see cref="Runs.All"/>)과 같은 꼴입니다: corridor/ 와 globe/ 를 같은 이름·형식으로 쓰므로
/// 엔진의 지난 결과 목록과 --baked-dir 가 그대로 엽니다. planet/·hero/ 묶음은 쓰지 않고, 대신 noise/manifest.json 에
/// 설정·자리·걸린 시간을 남깁니다.
/// </remarks>
public static class NoiseRuns
{
    /// <summary>실행 폴더 안의 노이즈 목차 폴더 이름.</summary>
    public const string NoiseDir = "noise";

    /// <summary>noise/manifest.json 의 format.</summary>
    public const string Format = "bpcg-noise";

    /// <summary>노이즈 설정 파일: configs/noise/default.toml.</summary>
    public static string SettingsPath => Path.Combine(Paths.Configs, "noise", "default.toml");

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>
    /// B-PCG 설정(행성·프로필)에 노이즈 설정 절 [noise] 을 붙입니다. 이미 있으면 그대로 둡니다. 행성 반지름, 칸 크기, 히어로·회랑 크기,
    /// 시드는 B-PCG 와 같은 값을 씁니다.
    /// </summary>
    public static Config WithNoise(Config cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Contains("noise"))
        {
            return cfg;
        }
        string path = SettingsPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"노이즈 설정 파일이 없습니다: {path}. 저장소의 configs/noise/default.toml 이 있는지 확인하세요", path);
        }
        OrderedDictionary<string, object?> data = cfg.AsDict();
        data["noise"] = Toml.Load(File.ReadAllBytes(path));
        return new Config(data);
    }

    /// <summary>행성·프로필 이름으로 노이즈 비교본 설정을 읽습니다 (configs/planets + profiles + noise/default.toml).</summary>
    public static Config LoadConfig(string planet = "earth", string profile = "laptop") => WithNoise(Config.LoadConfig(planet, profile));

    // 지구본 레이어 가운데 B-PCG 계산을 전제로 쓴 기본 설명을 노이즈 비교본에 맞게 바꿉니다.
    private static Dictionary<string, (string Description, string? HowToRead)> Layers(double l0Km) => new()
    {
        ["elevation"] = (
            "해수면(0 m)을 기준으로 잰 땅의 높이입니다. 대륙·산지·언덕 노이즈를 겹친 값이고, 바다는 바닥까지의 깊이를 음수로 "
                + $"적었습니다. 행성 지도 칸(면 가운데 칸 한 변 약 {l0Km:F1} km) 가운데의 값이며, 칸 간격의 4배보다 짧은 물결은 뺐습니다. "
                + "지구본 칸 하나는 가장 가까운 행성 지도 칸 4개의 평균입니다.",
            null),
        ["precip_m_per_yr"] = (
            "1년 동안 내리는 비와 눈을 물 높이로 적은 양입니다(1 m/yr = 1000 mm/yr). 위도와 상관없이 노이즈로만 정했습니다. "
                + "B-PCG 행성의 적도 비구름 띠와 중위도 편서풍 띠가 없습니다.",
            null),
        ["surface_rock"] = (
            "지표 아래 10 m 의 암석입니다. 행성을 한 변 약 30 km 덩어리(암석 구역)로 나누고, 구역마다 퇴적암·변성암·화성암 가운데 하나를 "
                + "골라 휘고 기운 띠로 쌓았습니다. 암석은 높이·물·동굴과 상관이 없어, 석회암이 아닌 암석 속에도 노이즈 동굴이 생깁니다.",
            null),
        ["rivers"] = (
            "강 노이즈 값이 0 에 가까운 띠(강 가운데 줄)가 지나는 육지 칸입니다. 유량을 계산하지 않아 모두 1 등급이고, 큰 강과 작은 강의 "
                + "구분이 없습니다. 강이 바다로 이어진다는 보장도 없습니다.",
            "1 인 칸에 파란색을 덧칠합니다. 등급이 하나뿐이라 진하기는 모두 같습니다."),
    };

    /// <summary>
    /// 노이즈 비교본 하나를 runDir 에 만듭니다. engineDir 이 있으면 회랑과 지구본을 그곳(engine/baked)에도 씁니다. 반환: runDir.
    /// 실행 폴더에 지난 B-PCG 실행의 planet/·hero/ 가 있으면 지웁니다. 노이즈 회랑과 맞지 않는 결과라 섞이면 헷갈리기 때문입니다.
    /// </summary>
    public static string All(Config cfg, string runDir, string? engineDir, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        long tAll = Stopwatch.GetTimestamp();
        cfg = WithNoise(cfg);
        int threads = Pipeline.ApplyThreads(cfg);
        long seed = cfg.I("planet.seed");
        log($"[노이즈] 시작: 프로필 {cfg.Sec("profile").Get("name")}, 시드 {seed} (노이즈 비교본: 판·침식·물의 흐름 계산 없이 노이즈만으로 만듭니다)");
        var sec = new OrderedDictionary<string, object?>();

        long t = Stopwatch.GetTimestamp();
        var terrain = new NoiseTerrain(cfg);
        NoisePlanetResult planet = NoisePlanet.Build(terrain, cfg);
        sec["planet"] = Seconds(t);
        int nL0 = (int)cfg.I("profile.grid.l0_n_per_face");
        log($"[노이즈] 행성 지도 면당 {nL0} × {nL0}칸 (모두 {LogText.N(planet.State.Graph.NCells)}칸): "
            + $"바다 {LogText.Pct((double)planet.Info["ocean_fraction"]!)}, 해수면을 맞추려고 높이를 {terrain.SeaShiftM:F0} m 내림, "
            + $"강 칸 {LogText.N((long)planet.Info["n_river_cells"]!)}개, {(double)sec["planet"]!:F2} s");
        NoiseSite site = planet.Site;
        log($"[노이즈] 히어로 자리: 위도 {site.LatDeg:F2}°, 경도 {site.LonDeg:F2}°, 행성 지도 칸 {site.L0Cell}, 점수 {site.Score:F2} "
            + $"({planet.Info["site_how"]})");

        RunFolder.DropStages(runDir, [Runs.PlanetDir, Runs.HeroDir, Runs.CorridorDir, Runs.GlobeDir],
            "노이즈 비교본으로 실행 폴더를 새로 써, 지난 실행의 결과임", log);
        t = Stopwatch.GetTimestamp();
        NoiseHero hero = NoiseCorridor.BuildHero(terrain, site, cfg);
        sec["hero"] = Seconds(t);
        long land = hero.Z.Count(z => z > 0.0);
        log($"[노이즈] 히어로 유역 {hero.N} × {hero.N}칸 (칸 {LogText.Num(hero.SpacingM)} m, 한 변 {LogText.Num(hero.SizeM / 1000.0)} km): "
            + $"육지 {LogText.Pct((double)land / hero.Z.Length)}, 높이 {hero.Z.Min():F0}~{hero.Z.Max():F0} m, "
            + $"강 골짜기 칸 {LogText.N(hero.Valley.Count(v => v > 0.9))}개, 지표에 열린 동굴 칸 {LogText.N(hero.Open.Count(b => b))}개, "
            + $"{(double)sec["hero"]!:F2} s");

        var id = new OrderedDictionary<string, object?>
        {
            ["format"] = Format,
            ["seed"] = seed,
            ["profile"] = cfg.Sec("profile").Get("name"),
            ["config_digest"] = cfg.Digest(),
        };
        t = Stopwatch.GetTimestamp();
        OrderedDictionary<string, object?> corridor = NoiseCorridor.Bake(
            terrain, site, hero, cfg, Path.Combine(runDir, Runs.CorridorDir), engineDir, log, id);
        sec["corridor"] = Seconds(t);
        log($"[노이즈] 회랑 굽기 끝: {(double)sec["corridor"]!:F2} s");

        t = Stopwatch.GetTimestamp();
        var heroEntry = new OrderedDictionary<string, object?>
        {
            ["unit"] = site.Center.Cast<object?>().ToList(),
            ["lat_deg"] = site.LatDeg,
            ["lon_deg"] = site.LonDeg,
            ["size_m"] = hero.SizeM,
            ["seed"] = seed,
            ["config_digest"] = cfg.Digest(),
        };
        double l0Km = Globe.CellKm(terrain.RadiusM, nL0);
        var generator = new GlobeGenerator(
            NoiseCorridor.Generator,
            "노이즈 비교본 지구본",
            "B-PCG 와 비교하려고 오늘날 게임처럼 노이즈(무작위 값을 매끈하게 겹친 무늬)만으로 만든 행성입니다. 판·침식·물의 흐름을 계산하지 "
                + "않으므로 판 경계·융기·유량·지질 템플릿 레이어가 없습니다. 높이는 대륙·산지·언덕 노이즈를 겹쳐 정하고, 해수면은 바다가 "
                + $"넓이의 {LogText.Pct(cfg.F("noise.sea.ocean_fraction"))}를 덮도록 맞췄습니다. 기온은 위도·높이에 노이즈를 더했고, 강수는 노이즈만으로 "
                + $"정했습니다. 강은 강 노이즈가 0 에 가까운 띠라 유량이 없어 모두 1 등급입니다. 계산은 면당 {nL0}칸(칸 약 {l0Km:F1} km)에서 했습니다.",
            Layers(l0Km));
        Globe.BakeGlobe(planet.State, cfg, Path.Combine(runDir, Runs.GlobeDir), hero: heroEntry, log: log,
            engineDir: engineDir is null ? null : Path.Combine(engineDir, Runs.GlobeDir), parent: id, generator: generator);
        sec["globe"] = Seconds(t);
        sec["total"] = Seconds(tAll);

        string noiseDir = Path.Combine(runDir, NoiseDir);
        Directory.CreateDirectory(noiseDir);
        Bundle.WriteJson(Path.Combine(noiseDir, "manifest.json"), new OrderedDictionary<string, object?>
        {
            ["format"] = Format,
            ["generator"] = NoiseCorridor.Generator,
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = Bundle.GitCommit(),
            ["git_dirty"] = Bundle.GitDirty(),
            ["config_digest"] = cfg.Digest(),
            ["seed"] = seed,
            ["profile"] = cfg.Sec("profile").Get("name"),
            ["threads"] = (long)threads,
            ["planet"] = planet.Info,
            ["site"] = new OrderedDictionary<string, object?>
            {
                ["center_unit"] = site.Center.Cast<object?>().ToList(),
                ["lat_deg"] = site.LatDeg,
                ["lon_deg"] = site.LonDeg,
                ["l0_cell"] = site.L0Cell,
                ["score"] = site.Score,
            },
            ["hero"] = new OrderedDictionary<string, object?>
            {
                ["n"] = (long)hero.N,
                ["spacing_m"] = hero.SpacingM,
                ["land_fraction"] = (double)land / hero.Z.Length,
                ["z_min_m"] = hero.Z.Min(),
                ["z_max_m"] = hero.Z.Max(),
            },
            ["corridor"] = corridor["corridor"],
            ["caves"] = corridor["caves"],
            ["config"] = cfg.AsDict(),
            ["seconds"] = sec,
        });
        log($"[노이즈] 끝: {(double)sec["total"]!:F1} s → {runDir}");
        return runDir;
    }
}
