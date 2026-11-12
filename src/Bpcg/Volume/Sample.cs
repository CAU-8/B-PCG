using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;
using Bpcg.Subsurface;

namespace Bpcg.Volume;

/// <summary>기둥(수평 위치)마다 높이와 상관없는 값 (HeroVolume.columns 의 반환). (C, K)·(C, L) 은 행 우선.</summary>
public sealed class ColumnValues
{
    public required double[] ZSurface { get; init; }

    public required double[] ZMap { get; init; }

    public required double[] M { get; init; }

    public required double[] Soil { get; init; }

    public required double[] Alluvium { get; init; }

    public required double[] ZGw { get; init; }

    /// <summary>아래 지하수면 (막는 층 아래 녹는 암석의 물 높이) [m]. 동굴 안 물은 이 높이 아래입니다.</summary>
    public required double[] ZGwLower { get; init; }

    public required double[] LakeLevel { get; init; }

    public required double[] LakeDist { get; init; }

    public required double[] RiverDist { get; init; }

    public required double[] RiverHalfWidth { get; init; }

    public required double[] RiverDepth { get; init; }

    public required double[] RiverBank { get; init; }

    public required double[] RiverLevel { get; init; }

    public required double[] WaterDist { get; init; }

    public required double[] StrataBottom { get; init; }

    public required byte[] StrataRock { get; init; }
}

/// <summary>
/// evaluate 가 배열로 돌려줄 값. 요청하지 않은 값은 빈 배열로 둡니다. 요청한 값의 식과 연산 순서는 모두 계산할 때와 같습니다.
/// </summary>
[Flags]
public enum VolumeField
{
    None = 0,
    D0 = 1 << 0,
    D1 = 1 << 1,
    DCave = 1 << 2,
    D = 1 << 3,
    WaterLevel = 1 << 4,
    Material = 1 << 5,
    SolidMaterial = 1 << 6,
    Rock = 1 << 7,
    Water = 1 << 8,
    Surface = 1 << 9,
    Under = 1 << 10,
    ZGw = 1 << 11,
    All = (1 << 12) - 1,
}

/// <summary>점마다 계산의 모든 중간값 (HeroVolume.evaluate 의 반환).</summary>
public sealed class VolumeEval
{
    public required double[] D0 { get; init; }

    public required double[] D1 { get; init; }

    public required double[] DCave { get; init; }

    public required double[] D { get; init; }

    public required double[] WaterLevel { get; init; }

    public required byte[] Material { get; init; }

    public required byte[] SolidMaterial { get; init; }

    public required byte[] Rock { get; init; }

    public required bool[] Water { get; init; }

    public required double[] Surface { get; init; }

    public required bool[] Under { get; init; }

    public required double[] ZGw { get; init; }

    /// <summary>키 이름으로 배열을 꺼냅니다 (d0, d1, d_cave, d, water_level, material, solid_material, rock, water, surface, under, z_gw).</summary>
    public Array Get(string key) => key switch
    {
        "d0" => D0,
        "d1" => D1,
        "d_cave" => DCave,
        "d" => D,
        "water_level" => WaterLevel,
        "material" => Material,
        "solid_material" => SolidMaterial,
        "rock" => Rock,
        "water" => Water,
        "surface" => Surface,
        "under" => Under,
        "z_gw" => ZGw,
        _ => throw new KeyNotFoundException($"evaluate 에 '{key}' 가 없습니다"),
    };

    /// <summary><see cref="Get"/> 의 키 이름에 해당하는 <see cref="VolumeField"/>.</summary>
    public static VolumeField FieldOf(string key) => key switch
    {
        "d0" => VolumeField.D0,
        "d1" => VolumeField.D1,
        "d_cave" => VolumeField.DCave,
        "d" => VolumeField.D,
        "water_level" => VolumeField.WaterLevel,
        "material" => VolumeField.Material,
        "solid_material" => VolumeField.SolidMaterial,
        "rock" => VolumeField.Rock,
        "water" => VolumeField.Water,
        "surface" => VolumeField.Surface,
        "under" => VolumeField.Under,
        "z_gw" => VolumeField.ZGw,
        _ => throw new KeyNotFoundException($"evaluate 에 '{key}' 가 없습니다"),
    };
}

/// <summary>
/// 3D 샘플 함수 HeroVolume: 점 하나에 '땅인가, 무슨 돌인가, 물인가'를 답합니다 (src/bpcg/volume/sample.py, docs/pipeline.md 10장).
/// </summary>
/// <remarks>
/// 지도는 (ny, nx) 행 우선 1차원 배열로 둡니다. math.hypot·np.hypot 은 double.Hypot 으로 옮겼습니다.
/// TODO(port): double.Hypot 은 libm hypot 과 마지막 비트가 다를 수 있습니다.
/// </remarks>
public sealed class HeroVolume
{
    public const double DetailAmplitudeM = 2.0;
    public const double DetailWavelengthM = 40.0;
    public const double WaterCalmM = 30.0;
    public const double RiverDensifyM = 1.0;
    public const double RiverSmoothKM = 0.5;
    public const double ConformCells = 0.5;
    // 상류 면적이 이보다 좁은 칸을 산비탈로 봅니다 [m²]. 1만 m² 는 25 m 칸 16개입니다. 이보다 넓으면 물길 칸으로 보고 높이를 섞지 않습니다.
    public const double HillslopeMaxAreaM2 = 1.0e4;
    public const double AlluviumClipBlendM = 1.0;
    public const double LakeWeightMin = 0.5;
    public const double EntranceOutCells = 4.0;
    // 동굴 조각을 구역에 등록할 때 조각 둘레로 잡는 여유 [m].
    public const double BucketPadM = 6.0;
    public const int SurfaceFixedPointIters = 10;
    public const int SurfaceBisectIters = 48;
    public const int ChunkPoints = 1_000_000;

    // 점을 고르거나 확인하는 반복에서 덩어리 하나의 점 수.
    private const int SelectChunkPoints = 65_536;
    public const double FarM = 1.0e12;
    public const byte MaterialEmpty = 255;
    private const long StreamDetail = 7201;

    private static readonly string[] Required =
    [
        "z_m", "soil_thickness_m", "alluvium_m", "water_table_m", "water_level_m", "is_lake", "is_river",
        "river_width_m", "river_depth_m", "receiver", "cave_entrance", "drainage_area_m2", "surface_rock",
    ];

    private readonly double _amp;
    private readonly long _detailSeed;
    private KdTree? _tree;
    private double _riverReach;
    private (double X0, double Y0, double Size, int Nbx, int Nby) _bucket;
    private long[] _bStart = [];
    private long[] _bItems = [];

    public int Ny { get; }

    public int Nx { get; }

    public double Dx { get; }

    public double X0 { get; }

    public double Y0 { get; }

    public long Seed { get; }

    public double RPass { get; }

    /// <summary>물이 잘 통하는 자리와 덜 통하는 자리가 번갈아 놓이는 간격 [m] (caves.noise_wavelength_m). 통로가 굽이치는 크기를 정합니다.</summary>
    public double Wavelength { get; }

    /// <summary>통로가 가장 굵어지는, 싱크홀로 모인 빗물 넓이 [m²] (caves.full_size_area_m2).</summary>
    public double FullSizeAreaM2 { get; }

    public int NLevels { get; }

    public int NLayers { get; }

    /// <summary>칸 중심의 지표 높이 (ny·nx) [m]. 히어로 필드 z_m(흔든 격자점의 높이)을 칸 중심으로 옮긴 값입니다.</summary>
    public double[] Z { get; }

    public double[] Soil { get; }

    public double[] Alluvium { get; }

    public double[] ZGwMap { get; }

    /// <summary>아래 지하수면 지도 (ny·nx). 필드가 없는 옛 묶음은 지하수면과 같습니다.</summary>
    public double[] ZGwLowerMap { get; }

    public double[] LakeLevel { get; private set; } = [];

    public double[] LakeDist { get; private set; } = [];

    /// <summary>동굴 층 높이 (K, ny, nx) 행 우선 (없으면 NaN).</summary>
    public double[] CaveLevels { get; }

    public double[] StrataBottom { get; }

    public byte[] StrataRock { get; }

    public double[] RiverPts { get; private set; } = [];

    public double[] RiverHw { get; private set; } = [];

    public double[] RiverDepthArr { get; private set; } = [];

    public double[] RiverZq { get; private set; } = [];

    public double[] RiverLevelArr { get; private set; } = [];

    public bool[] RiverConn { get; private set; } = [];

    /// <summary>동굴 통로 그물 (싱크홀에서 샘까지의 물길).</summary>
    public CaveNetwork Network { get; private set; } = null!;

    /// <summary>동굴 조각(층 통로, 수직굴, 입구 굴)의 한쪽 끝 (E, 3) [m].</summary>
    public double[] CapA { get; private set; } = [];

    /// <summary>동굴 조각의 다른 쪽 끝 (E, 3) [m].</summary>
    public double[] CapB { get; private set; } = [];

    /// <summary>동굴 조각의 반지름 (E,) [m].</summary>
    public double[] CapR { get; private set; } = [];

    /// <summary>조각이 지표로 열릴 수 있으면 참 (E,): 수직굴과 입구 굴. 층 통로는 지표 아래에만 둡니다.</summary>
    public bool[] CapOpen { get; private set; } = [];

    /// <summary>조각이 45° 보다 가파르게 서 있으면 참 (E,). 수직굴이 여기에 들어갑니다.</summary>
    public bool[] CapSteep { get; private set; } = [];

    /// <summary>입구 굴인 조각의 번호. 조각의 A 끝이 샘 칸(통로가 시작하는 자리), B 끝이 산비탈 밖입니다.</summary>
    public int[] EntranceCapsules { get; private set; } = [];

    public int NCapsules => CapR.Length;

    /// <summary>입구 굴을 판 칸 (N,). 산비탈 밖으로 열리고 싱크홀에서 온 통로가 닿는 입구 칸만 참입니다.</summary>
    public bool[] EntranceOpen { get; private set; } = [];

    /// <summary>싱크홀 칸 (N,). 지표에서 동굴 층까지 수직굴이 열려 있습니다.</summary>
    public bool[] SinkOpen { get; private set; } = [];

    /// <summary>(N,) 칸마다 그 칸에서 하류로 나가는 층 통로의 길이 [m].</summary>
    public double[] PassageM { get; private set; } = [];

    /// <summary>히어로 묶음의 입구 후보 수 (동굴 층마다 따로 셈).</summary>
    public long NEntranceCandidates { get; private set; }

    /// <summary>산비탈 밖으로 열리지 않아 파지 않은 입구 후보 수.</summary>
    public long NUnopened { get; private set; }

    /// <summary>산비탈 밖으로는 열리지만 싱크홀에서 온 통로가 닿지 않아 파지 않은 입구 후보 수.</summary>
    public long NNoPassage { get; private set; }

    private static long SubSeed(long seed, long stream, long k = 0) => (long)(Hashing.Hash3(seed, stream, k) >> 1);

    /// <summary>[0, 1] 로 자른 smoothstep.</summary>
    public static double Smoothstep01(double t)
    {
        t = NpMath.Clip(t, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static double Smax(double a, double b, double k)
    {
        double h = NpMath.PyMax(k - Math.Abs(a - b), 0.0) / k;
        return NpMath.PyMax(a, b) + (h * h * k * 0.25);
    }

    /// <summary>히어로 지도로 3D 샘플 함수를 만듭니다. detailAmplitudeM: 지표 노이즈 진폭 A_d [m].</summary>
    public HeroVolume(HeroState heroState, Config cfg, double detailAmplitudeM = DetailAmplitudeM)
    {
        CellGraph graph = heroState.Graph;
        FieldSet fields = heroState.Fields;
        LayerColumns columns = heroState.Columns;
        if (graph.Kind != "flat")
        {
            throw new ArgumentException("HeroVolume 은 평면 히어로 그래프에서만 만듭니다");
        }
        var missing = Required.Where(k => !fields.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"hero_state.fields 에 필요한 필드가 없습니다: {Pipeline.PyListRepr(missing)}");
        }
        if (!(double.IsFinite(detailAmplitudeM) && detailAmplitudeM >= 0))
        {
            throw new ArgumentException($"detail_amplitude_m 은 0 이상이어야 합니다: {detailAmplitudeM}");
        }
        Ny = (int)graph.Shape[0];
        Nx = (int)graph.Shape[1];
        int n = Ny * Nx;
        Dx = graph.Spacing;
        (X0, Y0) = graph.Origin;
        Seed = cfg.I("planet.seed");
        _amp = detailAmplitudeM;
        RPass = cfg.F("caves.passage_radius_m");
        Wavelength = cfg.F("caves.noise_wavelength_m");
        FullSizeAreaM2 = cfg.F("caves.full_size_area_m2");
        if (!(RPass > 0.0 && Wavelength > 0.0 && FullSizeAreaM2 > 0.0))
        {
            throw new ArgumentException(
                "caves 설정은 passage_radius_m > 0, noise_wavelength_m > 0, full_size_area_m2 > 0 이어야 합니다. "
                + $"받은 값: {RPass}, {Wavelength}, {FullSizeAreaM2}");
        }
        NLevels = (int)cfg.I("caves.levels");
        if (NLevels < 1)
        {
            throw new ArgumentException($"caves.levels 는 1 이상이어야 합니다: {NLevels}");
        }
        double[] Img(string name)
        {
            Array a = fields[name];
            if (a.Length != n)
            {
                throw new ArgumentException($"fields['{name}'] 의 길이가 칸 수 {n} 와 다릅니다");
            }
            // float[] 는 ToFloat64 가 이미 새 배열로 넓히므로 한 번만 복사합니다.
            return a is double[] d ? (double[])d.Clone() : FieldSet.ToFloat64(a);
        }
        bool[] ImgB(string name) => (bool[])FieldSet.CastTo(fields[name], typeof(bool));
        double[] zNode = Img("z_m");
        if (zNode.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("z_m 에 NaN 이나 inf 가 있습니다");
        }
        // z_m 은 흔들어 둔 격자점의 높이입니다. 아래 계산은 값이 칸 정중앙에 있다고 보고 그 사이를 이으므로,
        // 먼저 칸 중심의 높이로 옮깁니다 (NodeResample). 물 칸은 수면·강바닥이 그 칸의 z 를 기준으로 하므로 그대로 둡니다.
        // 물 칸 = 강 + 호수 + 바다. 강 칸 배열에 바로 더하면 히어로 필드가 바뀌므로 새 배열에 담습니다.
        bool[] riverCell = ImgB("is_river");
        bool[] water = new bool[n];
        bool[] lakeCell = ImgB("is_lake");
        bool[] oceanCell = fields.Contains("is_ocean") ? ImgB("is_ocean") : new bool[n];
        for (int c = 0; c < n; c++)
        {
            water[c] = riverCell[c] || lakeCell[c] || oceanCell[c];
        }
        double[] zCenter = NodeResample.ToCellCenters(graph, zNode, water);
        // 산비탈 칸의 울퉁불퉁함을 줄입니다 (보기용, 설정 detail.hillslope_smooth_passes). 물 칸과, 상류 면적이
        // HillslopeMaxAreaM2 이상인 물길 칸은 그대로 둡니다. 키가 없는 설정(이 키가 생기기 전의 히어로 묶음)은 0 번으로 봅니다.
        int smoothPasses = cfg.Contains("detail") && cfg.Sec("detail").Contains("hillslope_smooth_passes")
            ? (int)cfg.I("detail.hillslope_smooth_passes")
            : 0;
        if (smoothPasses > 0 && fields.Contains("drainage_area_m2"))
        {
            double[] area = Img("drainage_area_m2");
            bool[] fixedCell = new bool[n];
            for (int c = 0; c < n; c++)
            {
                fixedCell[c] = water[c] || !(area[c] < HillslopeMaxAreaM2);
            }
            zCenter = NodeResample.SmoothHillslopes(Ny, Nx, zCenter, fixedCell, smoothPasses);
        }
        Z = zCenter;
        Soil = Array.ConvertAll(Img("soil_thickness_m"), NpMath.NanToNum);
        Alluvium = Array.ConvertAll(Img("alluvium_m"), NpMath.NanToNum);
        double[] zgw = Img("water_table_m");
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(zgw[c]))
            {
                zgw[c] = Z[c];
            }
        }
        ZGwMap = zgw;
        double[] zgwLower = fields.Contains("water_table_lower_m") ? Img("water_table_lower_m") : (double[])zgw.Clone();
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(zgwLower[c]))
            {
                zgwLower[c] = zgw[c];
            }
        }
        ZGwLowerMap = zgwLower;
        double[] level = Img("water_level_m");
        bool[] isLake = ImgB("is_lake");
        bool[] isOcean = fields.Contains("is_ocean") ? ImgB("is_ocean") : new bool[n];
        bool[] body = new bool[n];
        for (int c = 0; c < n; c++)
        {
            body[c] = isLake[c] || isOcean[c];
        }
        BuildLakes(level, body);
        CaveLevels = new double[NLevels * n];
        for (int k = 0; k < NLevels; k++)
        {
            string name = $"cave_level_{k}_m";
            double[] lv = fields.Contains(name) ? Img(name) : Enumerable.Repeat(NpMath.NaN, n).ToArray();
            Array.Copy(lv, 0, CaveLevels, k * n, n);
        }
        if (columns.NCells != n)
        {
            throw new ArgumentException($"columns 의 칸 수 {columns.NCells} 가 {n} 와 다릅니다");
        }
        NLayers = columns.NLayers;
        StrataBottom = columns.Bottom;
        StrataRock = columns.Rock;
        _detailSeed = SubSeed(Seed, StreamDetail);
        BuildRivers(heroState.Rivers, fields, Img, ImgB, graph.Pos);
        BuildCaveNetwork(fields, graph, cfg);
    }

    /// <summary>히어로 영역 (x_min, x_max, y_min, y_max) [m].</summary>
    public (double XMin, double XMax, double YMin, double YMax) Extent => (X0, X0 + (Nx * Dx), Y0 - (Ny * Dx), Y0);

    /// <summary>칸 중심 (흔들지 않은) x, y [m] (cell_centers).</summary>
    public (double[] X, double[] Y) CellCenters()
    {
        int n = Ny * Nx;
        double[] x = new double[n];
        double[] y = new double[n];
        for (int c = 0; c < n; c++)
        {
            int jj = c / Nx;
            int ii = c % Nx;
            x[c] = X0 + ((ii + 0.5) * Dx);
            y[c] = Y0 - ((jj + 0.5) * Dx);
        }
        return (x, y);
    }

    private static double Fmax(double a, double b) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    private void BuildLakes(double[] level, bool[] isBody)
    {
        int ny = Ny;
        int nx = Nx;
        int n = ny * nx;
        double[] lake = new double[n];
        for (int c = 0; c < n; c++)
        {
            lake[c] = isBody[c] && double.IsFinite(level[c]) ? level[c] : NpMath.NaN;
        }
        double[] dil = (double[])lake.Clone();
        for (int dj = -1; dj <= 1; dj++)
        {
            for (int di = -1; di <= 1; di++)
            {
                if (dj == 0 && di == 0)
                {
                    continue;
                }
                for (int j = 0; j < ny; j++)
                {
                    for (int i = 0; i < nx; i++)
                    {
                        int c = (j * nx) + i;
                        if (!double.IsNaN(lake[c]))
                        {
                            continue;
                        }
                        int j2 = j + dj;
                        int i2 = i + di;
                        double nb = j2 >= 0 && j2 < ny && i2 >= 0 && i2 < nx ? lake[(j2 * nx) + i2] : NpMath.NaN;
                        dil[c] = Fmax(dil[c], nb);
                    }
                }
            }
        }
        LakeLevel = dil;
        if (Array.IndexOf(isBody, true) >= 0)
        {
            bool[] notBody = Array.ConvertAll(isBody, b => !b);
            double[] dist = SciPy.DistanceTransformEdt(notBody, ny, nx);
            LakeDist = Array.ConvertAll(dist, d => Math.Max((d * Dx) - (0.5 * Dx), 0.0));
        }
        else
        {
            LakeDist = Enumerable.Repeat(FarM, n).ToArray();
        }
    }

    /// <summary>centripetal Catmull-Rom (α = 0.5) 으로 제어점을 지나는 곡선 (catmull_rom_centripetal). ctrl·points 는 (n, 2).</summary>
    public static (double[] Points, double[] Span) CatmullRomCentripetal(double[] ctrl, double stepM)
    {
        int n = ctrl.Length / 2;
        if (ctrl.Length % 2 != 0 || n < 1)
        {
            throw new ArgumentException("ctrl 은 (n ≥ 1, 2) 배열이어야 합니다");
        }
        if (!(stepM > 0))
        {
            throw new ArgumentException($"step_m 은 0 보다 커야 합니다: {stepM}");
        }
        if (n == 1)
        {
            return ((double[])ctrl.Clone(), [0.0]);
        }
        double[] ext = new double[(n + 2) * 2];
        ext[0] = (2.0 * ctrl[0]) - ctrl[2];
        ext[1] = (2.0 * ctrl[1]) - ctrl[3];
        Array.Copy(ctrl, 0, ext, 2, n * 2);
        ext[(n + 1) * 2] = (2.0 * ctrl[(n - 1) * 2]) - ctrl[(n - 2) * 2];
        ext[((n + 1) * 2) + 1] = (2.0 * ctrl[((n - 1) * 2) + 1]) - ctrl[((n - 2) * 2) + 1];
        int ns = n - 1;
        const double eps = 1e-9;
        double Norm(int a, int b)
        {
            double dx = ext[b * 2] - ext[a * 2];
            double dy = ext[(b * 2) + 1] - ext[(a * 2) + 1];
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
        double Knot(int a, int b) => Math.Max(NpMath.Power(Norm(a, b), 0.5), eps);
        var pts = new List<double>();
        var span = new List<double>();
        for (int sIdx = 0; sIdx < ns; sIdx++)
        {
            // p0 = ext[s], p1 = ext[s+1], p2 = ext[s+2], p3 = ext[s+3]
            double segLen = Norm(sIdx + 1, sIdx + 2);
            long count = Math.Max((long)Math.Ceiling(segLen / stepM), 1);
            double t0 = 0.0;
            double t1 = t0 + Knot(sIdx, sIdx + 1);
            double t2 = t1 + Knot(sIdx + 1, sIdx + 2);
            double t3 = t2 + Knot(sIdx + 2, sIdx + 3);
            for (long kk = 0; kk < count; kk++)
            {
                double local = (double)kk / count;
                double tt = t1 + (local * (t2 - t1));
                for (int ax = 0; ax < 2; ax++)
                {
                    double p0 = ext[(sIdx * 2) + ax];
                    double p1 = ext[((sIdx + 1) * 2) + ax];
                    double p2 = ext[((sIdx + 2) * 2) + ax];
                    double p3 = ext[((sIdx + 3) * 2) + ax];
                    double a1 = ((t1 - tt) / (t1 - t0) * p0) + ((tt - t0) / (t1 - t0) * p1);
                    double a2 = ((t2 - tt) / (t2 - t1) * p1) + ((tt - t1) / (t2 - t1) * p2);
                    double a3 = ((t3 - tt) / (t3 - t2) * p2) + ((tt - t2) / (t3 - t2) * p3);
                    double b1 = ((t2 - tt) / (t2 - t0) * a1) + ((tt - t0) / (t2 - t0) * a2);
                    double b2 = ((t3 - tt) / (t3 - t1) * a2) + ((tt - t1) / (t3 - t1) * a3);
                    double cc = ((t2 - tt) / (t2 - t1) * b1) + ((tt - t1) / (t2 - t1) * b2);
                    pts.Add(cc);
                }
                span.Add(sIdx + local);
            }
        }
        pts.Add(ctrl[(n - 1) * 2]);
        pts.Add(ctrl[((n - 1) * 2) + 1]);
        span.Add(n - 1.0);
        return (pts.ToArray(), span.ToArray());
    }

    private static double[] InterpCtrl(double[] values, double[] span)
    {
        int n = values.Length;
        double[] o = new double[span.Length];
        for (int k = 0; k < span.Length; k++)
        {
            long i = Math.Min((long)Math.Floor(span[k]), n - 1);
            long j = Math.Min(i + 1, n - 1);
            double t = span[k] - i;
            o[k] = values[i] + (t * (values[j] - values[i]));
        }
        return o;
    }

    // 강 선의 조절점을 이웃 조절점과 섞는 횟수. 한 번에 (앞 + 2·자기 + 뒤)/4 로 바꿉니다.
    // 0 이면 격자점을 그대로 잇습니다. 2 번이면 한 칸씩 꺾이는 계단과 흔들기의 잔떨림이 약 절반으로 줄어듭니다.
    public const int RiverSmoothPasses = 2;

    /// <summary>
    /// 꺾은선 조절점 (M, 2) 을 제자리에서 부드럽게 합니다. 양 끝 점은 그대로 둡니다 (합류점에서 강 구간끼리 만나야 함).
    /// passes 번 되풀이하며, 한 번에 안쪽 점을 (앞 + 2·자기 + 뒤)/4 로 바꿉니다.
    /// 예: 동쪽으로 25 m, 남쪽으로 25 m 꺾이는 모서리 점은 한 번에 모서리 안쪽으로 약 8.8 m 들어옵니다.
    /// </summary>
    public static void SmoothLine(double[] xy, int passes)
    {
        int m = xy.Length / 2;
        if (m < 3 || passes < 1)
        {
            return;
        }
        double[] prev = new double[xy.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            Array.Copy(xy, prev, xy.Length);
            for (int k = 1; k < m - 1; k++)
            {
                xy[k * 2] = (prev[(k - 1) * 2] + (2.0 * prev[k * 2]) + prev[(k + 1) * 2]) * 0.25;
                xy[(k * 2) + 1] = (prev[((k - 1) * 2) + 1] + (2.0 * prev[(k * 2) + 1]) + prev[((k + 1) * 2) + 1]) * 0.25;
            }
        }
    }

    // nodePos: (N, 3) 그래프의 대표점(흔들어 둔 격자점). 강 선은 솔버가 높이를 푼 바로 그 점들을 지나게 합니다.
    // 칸 정중앙을 이으면 D8 물길이 가로·세로·대각선의 곧은 토막으로만 이어져, 자로 그은 수로처럼 보입니다.
    private void BuildRivers(List<long[]> rivers, FieldSet fields, Func<string, double[]> img, Func<string, bool[]> imgB,
        double[] nodePos)
    {
        int n = Ny * Nx;
        if (nodePos.Length != n * 3)
        {
            throw new ArgumentException($"그래프 대표점은 (N, 3) 이어야 합니다. 받은 길이 {nodePos.Length}, 칸 수 {n}");
        }
        double[] z = img("z_m");
        double[] w = Array.ConvertAll(img("river_width_m"), NpMath.NanToNum);
        double[] d = Array.ConvertAll(img("river_depth_m"), NpMath.NanToNum);
        double[] lev = img("water_level_m");
        bool[] isRiver = imgB("is_river");
        long[] rcv = (long[])FieldSet.CastTo(fields["receiver"], typeof(long));
        if (rcv.Length != n || rcv.Any(r => r < 0 || r >= n))
        {
            throw new ArgumentException("fields['receiver'] 는 0..N-1 범위의 (N,) 배열이어야 합니다");
        }
        const double fS = Water.RiverSurfaceDepthFraction;
        var ptsL = new List<double>();
        var hwL = new List<double>();
        var dL = new List<double>();
        var zqL = new List<double>();
        var levL = new List<double>();
        var connL = new List<bool>();
        foreach (long[] seg in rivers ?? [])
        {
            if (seg.Length == 0)
            {
                continue;
            }
            if (seg.Any(c => c < 0 || c >= n))
            {
                throw new ArgumentException("hero_state.rivers 에 범위를 벗어난 칸 번호가 있습니다");
            }
            long last = seg[^1];
            var ctrl = new List<long>(seg);
            var cHw = new List<double>();
            var cD = new List<double>();
            var cZq = new List<double>();
            var cLev = new List<double>();
            foreach (long c in seg)
            {
                double h = double.IsFinite(lev[c]) ? lev[c] : z[c] - (fS * d[c]);
                cHw.Add(0.5 * w[c]);
                cD.Add(d[c]);
                cZq.Add(z[c]);
                cLev.Add(h);
            }
            long r = rcv[last];
            if (r != last)
            {
                ctrl.Add(r);
                double hLast = cLev[^1];
                if (isRiver[r])
                {
                    double rLev = double.IsFinite(lev[r]) ? lev[r] : z[r] - (fS * d[r]);
                    cHw.Add(0.5 * w[r]);
                    cD.Add(d[r]);
                    cZq.Add(z[r]);
                    cLev.Add(rLev);
                }
                else
                {
                    double rLev = double.IsFinite(lev[r]) ? lev[r] : hLast;
                    rLev = NpMath.PyMin(rLev, hLast);
                    double hwLast = cHw[^1];
                    double dLast = cD[^1];
                    cHw.Add(hwLast);
                    cD.Add(dLast);
                    cZq.Add(NpMath.PyMin(z[last], rLev + (fS * dLast)));
                    cLev.Add(rLev);
                }
            }
            double[] ctrlXy = new double[ctrl.Count * 2];
            for (int k = 0; k < ctrl.Count; k++)
            {
                ctrlXy[k * 2] = nodePos[ctrl[k] * 3];
                ctrlXy[(k * 2) + 1] = nodePos[(ctrl[k] * 3) + 1];
            }
            SmoothLine(ctrlXy, RiverSmoothPasses);
            (double[] pts, double[] span) = CatmullRomCentripetal(ctrlXy, RiverDensifyM);
            ptsL.AddRange(pts);
            hwL.AddRange(InterpCtrl(cHw.ToArray(), span));
            dL.AddRange(InterpCtrl(cD.ToArray(), span));
            zqL.AddRange(InterpCtrl(cZq.ToArray(), span));
            levL.AddRange(InterpCtrl(cLev.ToArray(), span));
            for (int k = 0; k < span.Length; k++)
            {
                connL.Add(k < span.Length - 1);
            }
        }
        double hwMax = 0.0;
        if (ptsL.Count > 0)
        {
            RiverPts = ptsL.ToArray();
            RiverHw = hwL.ToArray();
            RiverDepthArr = dL.ToArray();
            RiverZq = zqL.ToArray();
            RiverLevelArr = levL.ToArray();
            RiverConn = connL.ToArray();
            _tree = new KdTree(RiverPts, 2);
            hwMax = RiverHw.Max();
        }
        _riverReach = hwMax + NpMath.PyMax(NpMath.PyMax(ConformCells * Dx, 2.0 * WaterCalmM), RiverSmoothKM) + 1.0;
    }

    /// <summary>(ny, nx) 지도를 국소 (x, y) [m] 에서 쌍선형 보간 (밖은 가장자리 값) (_bilinear).</summary>
    public double[] Bilinear(double[] image, double[] x, double[] y)
    {
        double[] o = new double[x.Length];
        for (int k = 0; k < x.Length; k++)
        {
            double fi = NpMath.Clip(((x[k] - X0) / Dx) - 0.5, 0.0, Nx - 1.0);
            double fj = NpMath.Clip(((Y0 - y[k]) / Dx) - 0.5, 0.0, Ny - 1.0);
            long i0 = Math.Min((long)Math.Floor(fi), Nx - 2);
            long j0 = Math.Min((long)Math.Floor(fj), Ny - 2);
            double tx = fi - i0;
            double ty = fj - j0;
            o[k] = (image[(j0 * Nx) + i0] * (1 - tx) * (1 - ty))
                + (image[(j0 * Nx) + i0 + 1] * tx * (1 - ty))
                + (image[((j0 + 1) * Nx) + i0] * (1 - tx) * ty)
                + (image[((j0 + 1) * Nx) + i0 + 1] * tx * ty);
        }
        return o;
    }

    // numpy arange(start, stop, step) (float): start + i·delta, delta = (start + step) − start
    private static double[] Arange(double start, double stop, double step)
    {
        long len = Math.Max((long)Math.Ceiling((stop - start) / step), 0);
        double delta = (start + step) - start;
        double[] o = new double[len];
        for (long i = 0; i < len; i++)
        {
            o[i] = i == 0 ? start : (i == 1 ? start + delta : start + (i * delta));
        }
        return o;
    }

    /// <summary>
    /// 동굴 조각을 만듭니다. 입구 칸마다 산 밖으로 열리는지 확인해 샘으로 삼고, 싱크홀에서 샘까지의 통로 그물
    /// (<see cref="CaveNetwork"/>)을 만든 뒤, 층 통로·수직굴·입구 굴을 캡슐(양 끝과 반지름) 목록으로 모읍니다.
    /// </summary>
    private void BuildCaveNetwork(FieldSet fields, CellGraph graph, Config cfg)
    {
        long[] ent = (long[])FieldSet.CastTo(fields["cave_entrance"], typeof(long));
        (double[] xc, double[] yc) = CellCenters();
        long[] rcv = (long[])FieldSet.CastTo(fields["receiver"], typeof(long));
        double[] z = Z;
        int ny = Ny;
        int nx = Nx;
        int n = ny * nx;
        double dx = Dx;
        double r = RPass;
        NUnopened = 0;
        NNoPassage = 0;
        NEntranceCandidates = 0;
        EntranceOpen = new bool[n];
        SinkOpen = new bool[n];

        // --- 1. 입구 칸마다 산 밖으로 나가는 방향과, 그 방향으로 지표가 층 높이보다 낮아지는 거리
        bool[] outlet = new bool[NLevels * n];
        bool[] candidate = new bool[NLevels * n];
        double[] outDirX = new double[NLevels * n];
        double[] outDirY = new double[NLevels * n];
        double[] outDist = new double[NLevels * n];
        for (int k = 0; k < NLevels; k++)
        {
            var cells = new List<int>();
            var dirxL = new List<double>();
            var diryL = new List<double>();
            for (int c = 0; c < n; c++)
            {
                if (((ent[c] >> k) & 1) == 0 || !double.IsFinite(CaveLevels[(k * n) + c]))
                {
                    continue;
                }
                NEntranceCandidates++;
                candidate[(k * n) + c] = true;
                int jj = c / nx;
                int ii = c % nx;
                double best = 0.0;
                double dirx = 0.0;
                double diry = 0.0;
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        if (dj == 0 && di == 0)
                        {
                            continue;
                        }
                        int j2 = jj + dj;
                        int i2 = ii + di;
                        bool ok = j2 >= 0 && j2 < ny && i2 >= 0 && i2 < nx;
                        int nb = ok ? (j2 * nx) + i2 : c;
                        double dist = dx * Math.Sqrt((dj * dj) + (di * di));
                        double drop = ok ? (z[c] - z[nb]) / dist : double.NegativeInfinity;
                        if (drop > best)
                        {
                            best = drop;
                            double norm = Math.Sqrt((di * di) + (dj * dj));
                            dirx = di / norm;
                            diry = -dj / norm;
                        }
                    }
                }
                if (best <= 0)
                {
                    long rr = rcv[c];
                    double vx = xc[rr] - xc[c];
                    double vy = yc[rr] - yc[c];
                    double nv = double.Hypot(vx, vy);
                    dirx = nv > 0 ? vx / nv : 0.0;
                    diry = nv > 0 ? vy / nv : 0.0;
                }
                if (double.Hypot(dirx, diry) > 0)
                {
                    cells.Add(c);
                    dirxL.Add(dirx);
                    diryL.Add(diry);
                }
            }
            if (cells.Count == 0)
            {
                continue;
            }
            int m = cells.Count;
            double step = 0.5 * r;
            double tOutMax = EntranceOutCells * dx;
            double[] tsOut = Arange(0.0, tOutMax + 1e-9, step);
            double[] sxo = new double[m * tsOut.Length];
            double[] syo = new double[m * tsOut.Length];
            for (int q = 0; q < m; q++)
            {
                int c = cells[q];
                for (int t = 0; t < tsOut.Length; t++)
                {
                    sxo[(q * tsOut.Length) + t] = xc[c] + (dirxL[q] * tsOut[t]);
                    syo[(q * tsOut.Length) + t] = yc[c] + (diryL[q] * tsOut[t]);
                }
            }
            double[] zsOut = Bilinear(z, sxo, syo);
            for (int q = 0; q < m; q++)
            {
                int c = cells[q];
                double zk = CaveLevels[(k * n) + c];
                for (int t = 0; t < tsOut.Length; t++)
                {
                    if (zsOut[(q * tsOut.Length) + t] < zk)
                    {
                        outlet[(k * n) + c] = true;
                        outDirX[(k * n) + c] = dirxL[q];
                        outDirY[(k * n) + c] = diryL[q];
                        outDist[(k * n) + c] = tsOut[t] + r;
                        break;
                    }
                }
            }
        }

        // --- 2. 싱크홀에서 샘까지의 통로 그물
        // 싱크홀은 지표 암석이 물에 녹는 암석이고 물에 잠기지 않은 육지 칸에만 둡니다.
        byte[] surfaceRock = (byte[])FieldSet.CastTo(fields["surface_rock"], typeof(byte));
        double[] area = fields.GetFloat64("drainage_area_m2");
        bool[] isRiver = (bool[])FieldSet.CastTo(fields["is_river"], typeof(bool));
        bool[] isLake = (bool[])FieldSet.CastTo(fields["is_lake"], typeof(bool));
        bool[] isOcean = fields.Contains("is_ocean") ? (bool[])FieldSet.CastTo(fields["is_ocean"], typeof(bool)) : new bool[n];
        bool[] sinkAllowed = new bool[n];
        for (int c = 0; c < n; c++)
        {
            sinkAllowed[c] = Rocks.Soluble[surfaceRock[c]] && !isRiver[c] && !isLake[c] && !isOcean[c];
        }
        Network = CaveNetwork.Build(graph, z, CaveLevels, outlet, sinkAllowed, area, new CaveNetworkParams(
            RadiusMaxM: r, FullSizeAreaM2: FullSizeAreaM2, SinkSpacingM: cfg.F("caves.sink_spacing_m"),
            SinkMinAreaM2: cfg.F("caves.sink_min_area_m2"), PathWavelengthM: Wavelength, Seed: Seed));

        // --- 3. 캡슐 목록: 층 통로와 수직굴(그물), 그 뒤에 입구 굴
        var aL = new List<double>(Network.SegA);
        var bL = new List<double>(Network.SegB);
        var rL = new List<double>(Network.SegRadius);
        var openL = new List<bool>(Network.SegIsShaft);
        var entranceIdx = new List<int>();
        for (int k = 0; k < NLevels; k++)
        {
            for (int c = 0; c < n; c++)
            {
                int i = (k * n) + c;
                if (!candidate[i])
                {
                    continue;
                }
                if (!outlet[i])
                {
                    NUnopened++;
                    continue;
                }
                // 싱크홀에서 온 물이 이 샘으로 나오지 않으면 통로가 없으므로 입구 굴도 파지 않습니다.
                if (!(Network.Flow[i] > 0.0))
                {
                    NNoPassage++;
                    continue;
                }
                double rr = CaveNetwork.Radius(Network.Flow[i], r, FullSizeAreaM2);
                double zk = CaveLevels[i] + (k == 0 ? CaveNetwork.StreamLift * rr : 0.0);
                entranceIdx.Add(rL.Count);
                aL.AddRange([xc[c], yc[c], zk]);
                bL.AddRange([xc[c] + (outDirX[i] * outDist[i]), yc[c] + (outDirY[i] * outDist[i]), zk]);
                rL.Add(rr);
                openL.Add(true);
                EntranceOpen[c] = true;
            }
        }
        foreach (int s in Network.Sinks)
        {
            SinkOpen[s] = true;
        }
        CapA = aL.ToArray();
        CapB = bL.ToArray();
        CapR = rL.ToArray();
        CapOpen = openL.ToArray();
        CapSteep = new bool[CapR.Length];
        for (int e = 0; e < CapSteep.Length; e++)
        {
            double hx = CapB[e * 3] - CapA[e * 3];
            double hy = CapB[(e * 3) + 1] - CapA[(e * 3) + 1];
            double vz = CapB[(e * 3) + 2] - CapA[(e * 3) + 2];
            CapSteep[e] = vz * vz > (hx * hx) + (hy * hy);
        }
        EntranceCapsules = entranceIdx.ToArray();
        PassageM = Network.PassageM;
        BuildBuckets();
    }

    private void BuildBuckets()
    {
        (double xMin, double xMax, double yMin, double yMax) = Extent;
        double size = 2.0 * Dx;
        int nbx = Math.Max((int)Math.Ceiling((xMax - xMin) / size), 1);
        int nby = Math.Max((int)Math.Ceiling((yMax - yMin) / size), 1);
        _bucket = (xMin, yMin, size, nbx, nby);
        var lists = new List<long>[nbx * nby];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i] = [];
        }
        for (int e = 0; e < NCapsules; e++)
        {
            // 굵은 격자에 칠할 때는 조각을 제 반지름보다 굵게 보므로(EvaluateGridCoarse), 그 굵기까지 덮도록 넉넉히 등록합니다.
            double r = Math.Max(CapR[e], BucketPadM);
            double loX = Math.Min(CapA[e * 3], CapB[e * 3]) - r;
            double hiX = Math.Max(CapA[e * 3], CapB[e * 3]) + r;
            double loY = Math.Min(CapA[(e * 3) + 1], CapB[(e * 3) + 1]) - r;
            double hiY = Math.Max(CapA[(e * 3) + 1], CapB[(e * 3) + 1]) + r;
            int i0 = Math.Clamp((int)Math.Floor((loX - xMin) / size), 0, nbx - 1);
            int i1 = Math.Clamp((int)Math.Floor((hiX - xMin) / size), 0, nbx - 1);
            int j0 = Math.Clamp((int)Math.Floor((loY - yMin) / size), 0, nby - 1);
            int j1 = Math.Clamp((int)Math.Floor((hiY - yMin) / size), 0, nby - 1);
            for (int jb = j0; jb <= j1; jb++)
            {
                for (int ib = i0; ib <= i1; ib++)
                {
                    lists[(jb * nbx) + ib].Add(e);
                }
            }
        }
        _bStart = new long[lists.Length + 1];
        for (int i = 0; i < lists.Length; i++)
        {
            _bStart[i + 1] = _bStart[i] + lists[i].Count;
        }
        _bItems = lists.SelectMany(l => l).ToArray();
    }

    private (double[] L, double[] Hw, double[] D, double[] Zq, double[] Lev) RiverQuery(double[] x, double[] y)
    {
        int c = x.Length;
        double[] oL = new double[c];
        double[] oHw = new double[c];
        double[] oD = new double[c];
        double[] oZq = new double[c];
        double[] oLev = new double[c];
        if (_tree is null || c == 0)
        {
            Array.Fill(oL, FarM);
            return (oL, oHw, oD, oZq, oLev);
        }
        int nPts = RiverPts.Length / 2;
        double[] pts = RiverPts;
        Parallelism.For(0, c, k =>
        {
            (_, int[] idxArr) = _tree.Query([x[k], y[k]], 1, _riverReach);
            int i = idxArr[0] >= nPts ? -1 : idxArr[0];
            if (i < 0)
            {
                oL[k] = FarM;
                return;
            }
            double qx = x[k];
            double qy = y[k];
            double best = double.Hypot(qx - pts[i * 2], qy - pts[(i * 2) + 1]);
            int a = i;
            double tBest = 0.0;
            for (int s = 0; s < 2; s++)
            {
                int j = s == 0 ? i - 1 : i;
                if (j < 0 || j + 1 >= nPts || !RiverConn[j])
                {
                    continue;
                }
                double ax = pts[j * 2];
                double ay = pts[(j * 2) + 1];
                double bx = pts[(j + 1) * 2] - ax;
                double by = pts[((j + 1) * 2) + 1] - ay;
                double l2 = (bx * bx) + (by * by);
                double t = l2 <= 0.0 ? 0.0 : (((qx - ax) * bx) + ((qy - ay) * by)) / l2;
                t = NpMath.PyMin(NpMath.PyMax(t, 0.0), 1.0);
                double d = double.Hypot(qx - ax - (t * bx), qy - ay - (t * by));
                if (d < best)
                {
                    best = d;
                    a = j;
                    tBest = t;
                }
            }
            int b = Math.Min(a + 1, nPts - 1);
            oL[k] = best;
            oHw[k] = RiverHw[a] + (tBest * (RiverHw[b] - RiverHw[a]));
            oD[k] = RiverDepthArr[a] + (tBest * (RiverDepthArr[b] - RiverDepthArr[a]));
            oZq[k] = RiverZq[a] + (tBest * (RiverZq[b] - RiverZq[a]));
            oLev[k] = RiverLevelArr[a] + (tBest * (RiverLevelArr[b] - RiverLevelArr[a]));
        });
        return (oL, oHw, oD, oZq, oLev);
    }

    /// <summary>수평 위치 (x, y) 마다 높이와 상관없는 값들 (columns).</summary>
    public ColumnValues Columns(double[] x, double[] y)
    {
        if (x.Length != y.Length)
        {
            throw new ArgumentException($"x, y 는 같은 (C,) 모양이어야 합니다: ({x.Length},) ({y.Length},)");
        }
        for (int i = 0; i < x.Length; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                throw new ArgumentException("x, y 에 NaN 이나 inf 가 있습니다");
            }
        }
        int cN = x.Length;
        int lN = NLayers;
        int ny = Ny;
        int nx = Nx;
        int n = ny * nx;
        double[] zMap = new double[cN];
        double[] soil = new double[cN];
        double[] alluv = new double[cN];
        double[] zgw = new double[cN];
        double[] zgwLower = new double[cN];
        double[] lake = new double[cN];
        double[] lakeDist = new double[cN];
        double[] sb = new double[cN * lN];
        byte[] sr = new byte[cN * (lN + 1)];
        Parallelism.For(0, cN, c =>
        {
            double fi = ((x[c] - X0) / Dx) - 0.5;
            double fj = ((Y0 - y[c]) / Dx) - 0.5;
            fi = NpMath.PyMin(NpMath.PyMax(fi, 0.0), nx - 1.0);
            fj = NpMath.PyMin(NpMath.PyMax(fj, 0.0), ny - 1.0);
            int i0 = Math.Min((int)Math.Floor(fi), nx - 2);
            int j0 = Math.Min((int)Math.Floor(fj), ny - 2);
            double tx = fi - i0;
            double ty = fj - j0;
            double w00 = (1.0 - tx) * (1.0 - ty);
            double w10 = tx * (1.0 - ty);
            double w01 = (1.0 - tx) * ty;
            double w11 = tx * ty;
            int i1 = i0 + 1;
            int j1 = j0 + 1;
            int c00 = (j0 * nx) + i0;
            int c10 = (j0 * nx) + i1;
            int c01 = (j1 * nx) + i0;
            int c11 = (j1 * nx) + i1;
            double Bl(double[] a) => (w00 * a[c00]) + (w10 * a[c10]) + (w01 * a[c01]) + (w11 * a[c11]);
            zMap[c] = Bl(Z);
            soil[c] = Bl(Soil);
            alluv[c] = Bl(Alluvium);
            zgw[c] = Bl(ZGwMap);
            zgwLower[c] = Bl(ZGwLowerMap);
            lakeDist[c] = Bl(LakeDist);
            Span<int> corner = [c00, c10, c01, c11];
            Span<double> wq = [w00, w10, w01, w11];
            double ws = 0.0;
            double acc = 0.0;
            for (int q = 0; q < 4; q++)
            {
                double v = LakeLevel[corner[q]];
                if (wq[q] > 0.0 && !double.IsNaN(v))
                {
                    ws += wq[q];
                    acc += wq[q] * v;
                }
            }
            lake[c] = ws >= LakeWeightMin ? acc / ws : NpMath.NaN;
            bool same = true;
            for (int q = 1; q < 4 && same; q++)
            {
                for (int li = 0; li <= lN; li++)
                {
                    if (StrataRock[(corner[q] * (lN + 1)) + li] != StrataRock[(c00 * (lN + 1)) + li])
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (same)
            {
                for (int li = 0; li < lN; li++)
                {
                    sb[(c * lN) + li] = (w00 * StrataBottom[(c00 * lN) + li]) + (w10 * StrataBottom[(c10 * lN) + li])
                        + (w01 * StrataBottom[(c01 * lN) + li]) + (w11 * StrataBottom[(c11 * lN) + li]);
                }
                Array.Copy(StrataRock, c00 * (lN + 1), sr, c * (lN + 1), lN + 1);
            }
            else
            {
                int jn = j0 + (ty >= 0.5 ? 1 : 0);
                int iN = i0 + (tx >= 0.5 ? 1 : 0);
                int cn = (jn * nx) + iN;
                Array.Copy(StrataBottom, cn * lN, sb, c * lN, lN);
                Array.Copy(StrataRock, cn * (lN + 1), sr, c * (lN + 1), lN + 1);
            }
        });
        (double[] l, double[] hw, double[] dep, double[] zq, double[] lev) = RiverQuery(x, y);
        double wc = ConformCells * Dx;
        double[] zSurface = new double[cN];
        double[] waterDist = new double[cN];
        double[] m = new double[cN];
        for (int c = 0; c < cN; c++)
        {
            bool has = hw[c] > 0 && l[c] < FarM;
            double t = Smoothstep01(Math.Max(l[c] - hw[c], 0.0) / wc);
            double excess = Math.Max(zMap[c] - zq[c], 0.0);
            zSurface[c] = has ? zMap[c] - (excess * (1.0 - t)) : zMap[c];
            double riverGap = has ? Math.Max(l[c] - hw[c], 0.0) : FarM;
            waterDist[c] = Math.Min(lakeDist[c], riverGap);
            m[c] = Smoothstep01((waterDist[c] - WaterCalmM) / WaterCalmM);
        }
        return new ColumnValues
        {
            ZSurface = zSurface,
            ZMap = zMap,
            M = m,
            Soil = soil,
            Alluvium = alluv,
            ZGw = zgw,
            ZGwLower = zgwLower,
            LakeLevel = lake,
            LakeDist = lakeDist,
            RiverDist = l,
            RiverHalfWidth = hw,
            RiverDepth = dep,
            RiverBank = zq,
            RiverLevel = lev,
            WaterDist = waterDist,
            StrataBottom = sb,
            StrataRock = sr,
        };
    }

    private double[] DetailNoise(double[] px, double[] py, double[] pz)
    {
        double[] pts = new double[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            pts[i * 3] = px[i];
            pts[(i * 3) + 1] = py[i];
            pts[(i * 3) + 2] = pz[i];
        }
        return Noise.Fbm3(pts, _detailSeed, frequency: 1.0 / DetailWavelengthM);
    }

    private static double CapsuleDist(double px, double py, double pz, double[] capA, double[] capB, long e)
    {
        double ax = capA[e * 3];
        double ay = capA[(e * 3) + 1];
        double az = capA[(e * 3) + 2];
        double bx = capB[e * 3] - ax;
        double by = capB[(e * 3) + 1] - ay;
        double bz = capB[(e * 3) + 2] - az;
        double qx = px - ax;
        double qy = py - ay;
        double qz = pz - az;
        double l2 = (bx * bx) + (by * by) + (bz * bz);
        double t = l2 <= 0.0 ? 0.0 : ((qx * bx) + (qy * by) + (qz * bz)) / l2;
        t = NpMath.PyMin(NpMath.PyMax(t, 0.0), 1.0);
        double ex = qx - (t * bx);
        double ey = qy - (t * by);
        double ez = qz - (t * bz);
        return Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
    }

    private VolumeEval EvaluateCore(ColumnValues cols, long[] col, double[] px, double[] py, double[] up, double noiseBandM,
        VolumeField need = VolumeField.All, double caveMinRadiusFlat = 0.0, double caveMinRadiusSteep = 0.0)
    {
        int mN = col.Length;
        double[] xi = new double[mN];
        if (_amp > 0)
        {
            // 잡음을 더할 점을 번호 순서대로 모읍니다. 덩어리마다 수를 세고 앞 덩어리 수만큼 띄워 적으므로 한 줄로 모은 것과 같습니다.
            bool finiteBand = double.IsFinite(noiseBandM);
            bool InBand(int p) => cols.M[col[p]] > 0
                && (!finiteBand || Math.Abs(up[p] - cols.ZSurface[col[p]]) <= noiseBandM);
            int nChunks = (mN + SelectChunkPoints - 1) / SelectChunkPoints;
            int[] selStart = new int[nChunks + 1];
            Parallelism.ForChunks(0, mN, SelectChunkPoints, (lo, hi) =>
            {
                int k = 0;
                for (int p = lo; p < hi; p++)
                {
                    k += InBand(p) ? 1 : 0;
                }
                selStart[(lo / SelectChunkPoints) + 1] = k;
            });
            for (int j = 0; j < nChunks; j++)
            {
                selStart[j + 1] += selStart[j];
            }
            int nSel = selStart[nChunks];
            if (nSel > 0)
            {
                int[] sel = new int[nSel];
                Parallelism.ForChunks(0, mN, SelectChunkPoints, (lo, hi) =>
                {
                    int q = selStart[lo / SelectChunkPoints];
                    for (int p = lo; p < hi; p++)
                    {
                        if (InBand(p))
                        {
                            sel[q++] = p;
                        }
                    }
                });
                double[] sx = new double[nSel];
                double[] sy = new double[nSel];
                double[] sz = new double[nSel];
                Parallelism.For(0, nSel, q =>
                {
                    sx[q] = px[sel[q]];
                    sy[q] = py[sel[q]];
                    sz[q] = up[sel[q]];
                });
                double[] nz = DetailNoise(sx, sy, sz);
                Parallelism.For(0, nSel, q => xi[sel[q]] = nz[q]);
            }
        }
        bool wD0 = (need & VolumeField.D0) != 0;
        bool wD1 = (need & VolumeField.D1) != 0;
        bool wDCave = (need & VolumeField.DCave) != 0;
        bool wD = (need & VolumeField.D) != 0;
        bool wHw = (need & VolumeField.WaterLevel) != 0;
        bool wMat = (need & VolumeField.Material) != 0;
        bool wSolid = (need & VolumeField.SolidMaterial) != 0;
        bool wRock = (need & VolumeField.Rock) != 0;
        bool wWater = (need & VolumeField.Water) != 0;
        // 동굴 거리(동굴 조각·녹는 층)는 d_cave 와, d_cave 로 정하는 d·material·water 에만 필요합니다.
        bool needCave = wDCave || wD || wMat || wWater;
        double[] oD0 = wD0 ? new double[mN] : [];
        double[] oD1 = wD1 ? new double[mN] : [];
        double[] oDCave = wDCave ? new double[mN] : [];
        double[] oD = wD ? new double[mN] : [];
        double[] oHw = wHw ? new double[mN] : [];
        byte[] oMat = wMat ? new byte[mN] : [];
        byte[] oSolid = wSolid ? new byte[mN] : [];
        byte[] oRock = wRock ? new byte[mN] : [];
        bool[] oWater = wWater ? new bool[mN] : [];
        int nL = NLayers;
        double amp = _amp;
        double rPass = RPass;
        double dx = Dx;
        (double bx0, double by0, double bsize, int nbx, int nby) = _bucket;
        double[] capA = CapA;
        double[] capB = CapB;
        double[] capR = CapR;
        bool[] capOpen = CapOpen;
        bool[] capSteep = CapSteep;
        Parallelism.For(0, mN, p =>
        {
            long c = col[p];
            double z = up[p];
            double surf = cols.ZSurface[c] + (cols.M[c] * amp * xi[p]);
            double d0 = z - surf;
            double hw = cols.RiverHalfWidth[c];
            double dd = cols.RiverDepth[c];
            double d1 = d0;
            if (hw > 0.0 && dd > 0.0 && cols.RiverDist[c] < FarM)
            {
                double a = cols.RiverDist[c] / hw;
                double v = (z - cols.RiverBank[c]) / dd;
                double dRiv = NpMath.PyMin(hw, dd) * (Math.Sqrt((a * a) + (v * v)) - 1.0);
                d1 = Smax(d0, -dRiv, RiverSmoothKM);
            }
            int li = nL;
            for (int i = 0; i < nL; i++)
            {
                if (cols.StrataBottom[(c * nL) + i] < z)
                {
                    li = i;
                    break;
                }
            }
            byte rock = cols.StrataRock[(c * (nL + 1)) + li];
            double depth = NpMath.PyMax(surf - z, 0.0);
            byte solid = depth < cols.Soil[c] ? Rocks.Soil : (depth < cols.Soil[c] + cols.Alluvium[c] ? Rocks.Alluvium : rock);
            double dcave = double.PositiveInfinity;
            if (needCave)
            {
                // 동굴 조각은 둥근 관입니다: 조각의 축(선분)까지 거리에서 그 조각의 반지름을 뺍니다.
                // dLevel 은 층 통로까지, dCapsule 은 지표로 열릴 수 있는 조각(수직굴·입구 굴)까지의 거리입니다.
                double dLevel = double.PositiveInfinity;
                double dCapsule = double.PositiveInfinity;
                if (_bItems.Length > 0)
                {
                    int ib = Math.Clamp((int)Math.Floor((px[p] - bx0) / bsize), 0, nbx - 1);
                    int jb = Math.Clamp((int)Math.Floor((py[p] - by0) / bsize), 0, nby - 1);
                    int bb = (jb * nbx) + ib;
                    for (long q = _bStart[bb]; q < _bStart[bb + 1]; q++)
                    {
                        long e = _bItems[q];
                        double re = NpMath.PyMax(capR[e], capSteep[e] ? caveMinRadiusSteep : caveMinRadiusFlat);
                        double de = CapsuleDist(px[p], py[p], z, capA, capB, e) - re;
                        if (capOpen[e])
                        {
                            dCapsule = NpMath.PyMin(dCapsule, de);
                        }
                        else
                        {
                            dLevel = NpMath.PyMin(dLevel, de);
                        }
                    }
                }
                if (NpMath.PyMin(dLevel, dCapsule) < double.PositiveInfinity)
                {
                    double dSol = double.PositiveInfinity;
                    for (int i = 0; i <= nL; i++)
                    {
                        if (Rocks.Soluble[cols.StrataRock[(c * (nL + 1)) + i]])
                        {
                            double top = i == 0 ? double.PositiveInfinity : cols.StrataBottom[(c * nL) + i - 1];
                            double bot = i == nL ? double.NegativeInfinity : cols.StrataBottom[(c * nL) + i];
                            dSol = NpMath.PyMin(dSol, NpMath.PyMax(z - top, bot - z));
                        }
                    }
                    // 수직굴과 입구 굴만 흙을 지나 지표로 열립니다. 층 통로는 흙·충적층 아래로 통로 반지름만큼 덮인
                    // 곳에만 두어, 입구가 아닌 곳에서 동굴이 지표를 뚫지 않습니다.
                    double blend = NpMath.PyMin(cols.Alluvium[c] / AlluviumClipBlendM, 1.0);
                    double plane = surf - cols.Alluvium[c] - cols.Soil[c] + ((cols.Soil[c] + (2.0 * rPass)) * (1.0 - blend));
                    double planeLevel = surf - cols.Alluvium[c] - cols.Soil[c] - rPass;
                    double dOpen = NpMath.PyMin(NpMath.PyMax(dLevel, z - planeLevel), NpMath.PyMax(dCapsule, z - plane));
                    dcave = NpMath.PyMax(dOpen, dSol);
                }
            }
            double dFinal = NpMath.PyMax(d1, -dcave);
            double hW = cols.LakeLevel[c];
            if (double.IsNaN(hW))
            {
                hW = double.NegativeInfinity;
            }
            if (hw > 0.0 && cols.RiverDist[c] <= hw)
            {
                hW = NpMath.PyMax(hW, cols.RiverLevel[c]);
            }
            // 땅 아래 빈 곳(동굴)은 녹는 암석 안의 물 높이(아래 지하수면) 아래가 물입니다. 땅 위는 수면 아래가 물입니다.
            double lvl = d1 < 0.0 ? cols.ZGwLower[c] : hW;
            if (wD0)
            {
                oD0[p] = d0;
            }
            if (wD1)
            {
                oD1[p] = d1;
            }
            if (wDCave)
            {
                oDCave[p] = dcave;
            }
            if (wD)
            {
                oD[p] = dFinal;
            }
            if (wSolid)
            {
                oSolid[p] = solid;
            }
            if (wRock)
            {
                oRock[p] = rock;
            }
            if (wMat)
            {
                oMat[p] = dFinal <= 0.0 ? solid : MaterialEmpty;
            }
            if (wWater)
            {
                oWater[p] = dFinal > 0.0 && z < lvl;
            }
            if (wHw)
            {
                oHw[p] = hW;
            }
        });
        bool wSurface = (need & VolumeField.Surface) != 0;
        bool wUnder = (need & VolumeField.Under) != 0;
        bool wZgw = (need & VolumeField.ZGw) != 0;
        double[] surface = wSurface ? new double[mN] : [];
        bool[] under = wUnder ? new bool[mN] : [];
        double[] zgw = wZgw ? new double[mN] : [];
        if (wSurface || wUnder || wZgw)
        {
            // under 는 d1 로 정하므로, d1 을 요청하지 않았으면 같은 식으로 다시 구합니다.
            double[] d1Under = wUnder && !wD1 ? EvaluateCore(cols, col, px, py, up, noiseBandM, VolumeField.D1).D1 : oD1;
            for (int p = 0; p < mN; p++)
            {
                long c = col[p];
                if (wSurface)
                {
                    surface[p] = cols.ZSurface[c] + (cols.M[c] * _amp * xi[p]);
                }
                if (wUnder)
                {
                    under[p] = d1Under[p] < 0;
                }
                if (wZgw)
                {
                    zgw[p] = cols.ZGw[c];
                }
            }
        }
        return new VolumeEval
        {
            D0 = oD0,
            D1 = oD1,
            DCave = oDCave,
            D = oD,
            WaterLevel = oHw,
            Material = oMat,
            SolidMaterial = oSolid,
            Rock = oRock,
            Water = oWater,
            Surface = surface,
            Under = under,
            ZGw = zgw,
        };
    }

    private static double[] CheckPoints(double[] points)
    {
        if (points.Length % 3 != 0)
        {
            throw new ArgumentException($"points 는 (M, 3) 배열이어야 합니다: 받은 길이 {points.Length}");
        }
        foreach (double v in points)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("points 에 NaN 이나 inf 가 있습니다");
            }
        }
        return points;
    }

    /// <summary>점마다 계산의 모든 중간값 (evaluate). points: (M, 3) 국소 (동, 북, 위) [m].</summary>
    public VolumeEval Evaluate(double[] points, double noiseBandM = double.PositiveInfinity)
        => EvaluateFields(points, VolumeField.All, noiseBandM);

    /// <summary>
    /// <see cref="Evaluate"/> 에서 need 로 고른 값만 배열로 돌려줍니다 (나머지는 빈 배열). 점마다 값은 Evaluate 와 같습니다.
    /// 덩어리(ChunkPoints 점)마다 결과를 바로 제자리에 옮겨, 모든 값을 덩어리째 들고 있다가 이어 붙이지 않습니다.
    /// </summary>
    public VolumeEval EvaluateFields(double[] points, VolumeField need, double noiseBandM = double.PositiveInfinity)
    {
        double[] pts = CheckPoints(points);
        int mN = pts.Length / 3;
        if (mN == 0)
        {
            return EvaluateCore(Columns([], []), [], [], [], [], 0.0, need);
        }
        VolumeEval? whole = null;
        for (int s = 0; s < mN; s += ChunkPoints)
        {
            int e = Math.Min(s + ChunkPoints, mN);
            int len = e - s;
            double[] px = new double[len];
            double[] py = new double[len];
            double[] pz = new double[len];
            for (int i = 0; i < len; i++)
            {
                px[i] = pts[(s + i) * 3];
                py[i] = pts[((s + i) * 3) + 1];
                pz[i] = pts[((s + i) * 3) + 2];
            }
            ColumnValues cols = Columns(px, py);
            long[] col = new long[len];
            for (int i = 0; i < len; i++)
            {
                col[i] = i;
            }
            VolumeEval part = EvaluateCore(cols, col, px, py, pz, noiseBandM, need);
            if (len == mN)
            {
                return part;
            }
            whole ??= Allocate(need, mN);
            CopyInto(part, whole, s);
        }
        return whole!;
    }

    private static VolumeEval Allocate(VolumeField need, int n)
    {
        T[] A<T>(VolumeField f) => (need & f) != 0 ? new T[n] : [];
        return new VolumeEval
        {
            D0 = A<double>(VolumeField.D0),
            D1 = A<double>(VolumeField.D1),
            DCave = A<double>(VolumeField.DCave),
            D = A<double>(VolumeField.D),
            WaterLevel = A<double>(VolumeField.WaterLevel),
            Material = A<byte>(VolumeField.Material),
            SolidMaterial = A<byte>(VolumeField.SolidMaterial),
            Rock = A<byte>(VolumeField.Rock),
            Water = A<bool>(VolumeField.Water),
            Surface = A<double>(VolumeField.Surface),
            Under = A<bool>(VolumeField.Under),
            ZGw = A<double>(VolumeField.ZGw),
        };
    }

    // 요청하지 않은 값은 빈 배열이라 길이 0 을 옮깁니다.
    private static void CopyInto(VolumeEval part, VolumeEval whole, int at)
    {
        Array.Copy(part.D0, 0, whole.D0, part.D0.Length == 0 ? 0 : at, part.D0.Length);
        Array.Copy(part.D1, 0, whole.D1, part.D1.Length == 0 ? 0 : at, part.D1.Length);
        Array.Copy(part.DCave, 0, whole.DCave, part.DCave.Length == 0 ? 0 : at, part.DCave.Length);
        Array.Copy(part.D, 0, whole.D, part.D.Length == 0 ? 0 : at, part.D.Length);
        Array.Copy(part.WaterLevel, 0, whole.WaterLevel, part.WaterLevel.Length == 0 ? 0 : at, part.WaterLevel.Length);
        Array.Copy(part.Material, 0, whole.Material, part.Material.Length == 0 ? 0 : at, part.Material.Length);
        Array.Copy(part.SolidMaterial, 0, whole.SolidMaterial, part.SolidMaterial.Length == 0 ? 0 : at, part.SolidMaterial.Length);
        Array.Copy(part.Rock, 0, whole.Rock, part.Rock.Length == 0 ? 0 : at, part.Rock.Length);
        Array.Copy(part.Water, 0, whole.Water, part.Water.Length == 0 ? 0 : at, part.Water.Length);
        Array.Copy(part.Surface, 0, whole.Surface, part.Surface.Length == 0 ? 0 : at, part.Surface.Length);
        Array.Copy(part.Under, 0, whole.Under, part.Under.Length == 0 ? 0 : at, part.Under.Length);
        Array.Copy(part.ZGw, 0, whole.ZGw, part.ZGw.Length == 0 ? 0 : at, part.ZGw.Length);
    }

    /// <summary>3D 샘플 함수 (sample): (d float32, material uint8 (빈 곳 255), water bool).</summary>
    public (float[] D, byte[] Material, bool[] Water) Sample(double[] points)
    {
        VolumeEval ev = EvaluateFields(points, VolumeField.D | VolumeField.Material | VolumeField.Water);
        return (Array.ConvertAll(ev.D, v => (float)v), ev.Material, ev.Water);
    }

    /// <summary>
    /// 수평 기둥 (x, y) (C,) 와 기둥마다 높이 up (C, Z) 의 격자를 한꺼번에 계산합니다 (evaluate_grid). 반환: keys 의 (C, Z) 배열.
    /// </summary>
    public Dictionary<string, Array> EvaluateGrid(double[] x, double[] y, double[] up, int zCount, double noiseBandM = double.PositiveInfinity, params string[] keys)
        => EvaluateGridCore(x, y, up, zCount, noiseBandM, 0.0, 0.0, keys);

    /// <summary>
    /// 칸이 동굴 통로보다 큰 격자(재질 부피)에 칠할 때 쓰는 evaluate_grid. spacingH, spacingV 는 격자의 가로·세로 간격 [m] 입니다.
    /// </summary>
    /// <remarks>
    /// 왜 필요한가: 격자점에서만 '빈 곳인가'를 물으면, 칸보다 가는 통로는 격자점 사이로 빠져 토막 납니다.
    /// 그래서 통로를 '어느 방향으로 놓여도 격자점 하나는 안에 드는 굵기'로 보고 칠합니다. 누운 통로는 가로·세로 간격으로 만든
    /// 직사각형의 대각선 절반, 서 있는 통로(수직굴)는 가로 간격 정사각형의 대각선 절반이 그 굵기입니다.
    /// 예: 가로 4 m, 세로 2 m 격자에서 반지름 2 m 인 누운 통로는 반지름 √(2² + 1²) ≈ 2.24 m 로 칠합니다.
    /// 이보다 굵은 통로는 제 굵기 그대로입니다. 동굴 메시와 입구 구멍은 이 함수를 쓰지 않습니다.
    /// </remarks>
    public Dictionary<string, Array> EvaluateGridCoarse(
        double[] x, double[] y, double[] up, int zCount, double spacingH, double spacingV, params string[] keys)
    {
        if (!(spacingH > 0.0 && spacingV > 0.0))
        {
            throw new ArgumentException($"격자 간격은 0 보다 커야 합니다. 받은 값: 가로 {spacingH}, 세로 {spacingV}");
        }
        double flat = 0.5 * Math.Sqrt((spacingH * spacingH) + (spacingV * spacingV));
        double steep = 0.5 * Math.Sqrt(2.0) * spacingH;
        return EvaluateGridCore(x, y, up, zCount, double.PositiveInfinity, flat, steep, keys);
    }

    private Dictionary<string, Array> EvaluateGridCore(
        double[] x, double[] y, double[] up, int zCount, double noiseBandM, double caveMinRadiusFlat, double caveMinRadiusSteep, string[] keys)
    {
        if (keys.Length == 0)
        {
            keys = ["d", "material", "water"];
        }
        int cN = x.Length;
        if (up.Length != cN * zCount)
        {
            throw new ArgumentException($"up 은 (C, Z) 모양이어야 합니다: 길이 {up.Length}, C = {cN}");
        }
        int notFinite = 0;
        Parallelism.ForChunks(0, up.Length, SelectChunkPoints, (lo, hi) =>
        {
            for (int p = lo; p < hi; p++)
            {
                if (!double.IsFinite(up[p]))
                {
                    System.Threading.Volatile.Write(ref notFinite, 1);
                    return;
                }
            }
        });
        if (notFinite != 0)
        {
            throw new ArgumentException("up 에 NaN 이나 inf 가 있습니다");
        }
        VolumeField need = VolumeField.None;
        foreach (string key in keys)
        {
            need |= VolumeEval.FieldOf(key);
        }
        var output = new Dictionary<string, Array>();
        int step = Math.Max(ChunkPoints / Math.Max(zCount, 1), 1);
        for (int s = 0; s < cN; s += step)
        {
            int e = Math.Min(s + step, cN);
            int len = e - s;
            ColumnValues cols = Columns(x[s..e], y[s..e]);
            long[] col = new long[len * zCount];
            double[] px = new double[len * zCount];
            double[] py = new double[len * zCount];
            double[] uz = new double[len * zCount];
            int s0 = s;
            Parallelism.For(0, len, c =>
            {
                for (int k = 0; k < zCount; k++)
                {
                    int p = (c * zCount) + k;
                    col[p] = c;
                    px[p] = x[s0 + c];
                    py[p] = y[s0 + c];
                    uz[p] = up[((s0 + c) * zCount) + k];
                }
            });
            VolumeEval ev = EvaluateCore(cols, col, px, py, uz, noiseBandM, need, caveMinRadiusFlat, caveMinRadiusSteep);
            foreach (string key in keys)
            {
                Array src = ev.Get(key);
                if (!output.TryGetValue(key, out Array? dst))
                {
                    dst = Array.CreateInstance(src.GetType().GetElementType()!, cN * zCount);
                    output[key] = dst;
                }
                Array.Copy(src, 0, dst, (long)s * zCount, src.Length);
            }
        }
        return output;
    }

    private double[] ByChunks(Func<double[], double[], double[]> fn, double[] x, double[] y)
    {
        if (x.Length != y.Length)
        {
            throw new ArgumentException($"x, y 는 같은 (C,) 모양이어야 합니다: ({x.Length},) ({y.Length},)");
        }
        if (x.Length <= ChunkPoints)
        {
            return fn(x, y);
        }
        var parts = new List<double>();
        for (int s = 0; s < x.Length; s += ChunkPoints)
        {
            int e = Math.Min(s + ChunkPoints, x.Length);
            parts.AddRange(fn(x[s..e], y[s..e]));
        }
        return parts.ToArray();
    }

    /// <summary>동굴을 빼고 강바닥을 깎은 지표 높이 (surface_height, d₁ = 0 의 근).</summary>
    public double[] SurfaceHeight(double[] x, double[] y) => ByChunks(SurfaceHeightCore, x, y);

    private double[] SurfaceHeightCore(double[] x, double[] y)
    {
        ColumnValues cols = Columns(x, y);
        double[] h = (double[])cols.ZSurface.Clone();
        int[] noisy = Enumerable.Range(0, x.Length).Where(c => cols.M[c] > 0).ToArray();
        if (_amp > 0 && noisy.Length > 0)
        {
            double[] xs = noisy.Select(c => x[c]).ToArray();
            double[] ys = noisy.Select(c => y[c]).ToArray();
            double[] b = noisy.Select(c => cols.ZSurface[c]).ToArray();
            double[] amp = noisy.Select(c => cols.M[c] * _amp).ToArray();
            double[] hh = (double[])b.Clone();
            for (int it = 0; it < SurfaceFixedPointIters; it++)
            {
                double[] nz = DetailNoise(xs, ys, hh);
                for (int q = 0; q < hh.Length; q++)
                {
                    hh[q] = b[q] + (amp[q] * nz[q]);
                }
            }
            for (int q = 0; q < noisy.Length; q++)
            {
                h[noisy[q]] = hh[q];
            }
        }
        var todo = new List<int>();
        for (int c = 0; c < x.Length; c++)
        {
            double hw = cols.RiverHalfWidth[c];
            double dd = cols.RiverDepth[c];
            double ell = cols.RiverDist[c];
            double dRiv = double.PositiveInfinity;
            if (hw > 0 && dd > 0 && ell < FarM)
            {
                double a = ell / hw;
                double v = (cols.ZSurface[c] - cols.RiverBank[c]) / dd;
                dRiv = Math.Min(hw, dd) * (Math.Sqrt((a * a) + (v * v)) - 1.0);
            }
            if (dRiv < RiverSmoothKM && cols.M[c] <= 0)
            {
                todo.Add(c);
            }
        }
        Parallelism.ForEach(todo, c =>
        {
            double hw = cols.RiverHalfWidth[c];
            double dd = cols.RiverDepth[c];
            double a = cols.RiverDist[c] / hw;
            double zs = cols.ZSurface[c];
            double zq = cols.RiverBank[c];
            double lo = NpMath.PyMin(zs, zq - dd) - (4.0 * (RiverSmoothKM + dd + 1.0));
            double hi = zs + RiverSmoothKM + 1.0;
            for (int it = 0; it < SurfaceBisectIters; it++)
            {
                double mid = 0.5 * (lo + hi);
                double v = (mid - zq) / dd;
                double dRiv = NpMath.PyMin(hw, dd) * (Math.Sqrt((a * a) + (v * v)) - 1.0);
                double f = Smax(mid - zs, -dRiv, RiverSmoothKM);
                if (f > 0.0)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
            h[c] = 0.5 * (lo + hi);
        });
        return h;
    }

    /// <summary>물 규칙에 쓰는 지상 수면 h_w (water_surface). 없으면 NaN.</summary>
    public double[] WaterSurface(double[] x, double[] y) => ByChunks((xx, yy) =>
    {
        ColumnValues cols = Columns(xx, yy);
        double[] o = new double[xx.Length];
        for (int c = 0; c < o.Length; c++)
        {
            bool inside = cols.RiverHalfWidth[c] > 0 && cols.RiverDist[c] <= cols.RiverHalfWidth[c];
            o[c] = inside ? Fmax(cols.LakeLevel[c], cols.RiverLevel[c]) : cols.LakeLevel[c];
        }
        return o;
    }, x, y);

    /// <summary>지하수면 z_gw (water_table, 쌍선형 보간).</summary>
    public double[] WaterTable(double[] x, double[] y) => MapBilinear(ZGwMap, x, y);

    /// <summary>아래 지하수면 (water_table_lower, 쌍선형 보간). 동굴 안 물 높이입니다.</summary>
    public double[] WaterTableLower(double[] x, double[] y) => MapBilinear(ZGwLowerMap, x, y);

    // 칸 지도 map 을 (x, y) 에서 쌍선형으로 읽습니다 (Columns 의 zgw 와 같은 식).
    private double[] MapBilinear(double[] map, double[] x, double[] y)
    {
        if (x.Length != y.Length)
        {
            throw new ArgumentException($"x, y 는 같은 (C,) 모양이어야 합니다: ({x.Length},) ({y.Length},)");
        }
        for (int i = 0; i < x.Length; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                throw new ArgumentException("x, y 에 NaN 이나 inf 가 있습니다");
            }
        }
        // 강·동굴·층 값은 필요 없어 계산하지 않습니다.
        int nx = Nx;
        int ny = Ny;
        double[] o = new double[x.Length];
        Parallelism.For(0, x.Length, c =>
        {
            double fi = ((x[c] - X0) / Dx) - 0.5;
            double fj = ((Y0 - y[c]) / Dx) - 0.5;
            fi = NpMath.PyMin(NpMath.PyMax(fi, 0.0), nx - 1.0);
            fj = NpMath.PyMin(NpMath.PyMax(fj, 0.0), ny - 1.0);
            int i0 = Math.Min((int)Math.Floor(fi), nx - 2);
            int j0 = Math.Min((int)Math.Floor(fj), ny - 2);
            double tx = fi - i0;
            double ty = fj - j0;
            double w00 = (1.0 - tx) * (1.0 - ty);
            double w10 = tx * (1.0 - ty);
            double w01 = (1.0 - tx) * ty;
            double w11 = tx * ty;
            int c00 = (j0 * nx) + i0;
            int c10 = (j0 * nx) + i0 + 1;
            int c01 = ((j0 + 1) * nx) + i0;
            int c11 = ((j0 + 1) * nx) + i0 + 1;
            o[c] = (w00 * map[c00]) + (w10 * map[c10]) + (w01 * map[c01]) + (w11 * map[c11]);
        });
        return o;
    }

}
