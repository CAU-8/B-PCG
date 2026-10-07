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

    public required double[] LakeLevel { get; init; }

    public required double[] LakeDist { get; init; }

    public required double[] RiverDist { get; init; }

    public required double[] RiverHalfWidth { get; init; }

    public required double[] RiverDepth { get; init; }

    public required double[] RiverBank { get; init; }

    public required double[] RiverLevel { get; init; }

    public required double[] WaterDist { get; init; }

    public required double[] CaveZ { get; init; }

    public required double[] CaveW { get; init; }

    public required double[] NuDist { get; init; }

    public required double[] StrataBottom { get; init; }

    public required byte[] StrataRock { get; init; }
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
    public const int CaveOctaves = 3;
    public const double CaveCoreFraction = 0.5;
    public const double CaveGradStepM = 0.25;
    public const double CaveGradFloor = 0.05;
    public const double CaveDistCapRadii = 2.0;
    public const double AlluviumClipBlendM = 1.0;
    public const double LakeWeightMin = 0.5;
    public const double EntranceSearchWavelengths = 2.0;
    public const double EntranceOutCells = 4.0;
    public const int SurfaceFixedPointIters = 10;
    public const int SurfaceBisectIters = 48;
    public const int ChunkPoints = 1_000_000;
    public const double FarM = 1.0e12;
    public const byte MaterialEmpty = 255;
    private const long StreamDetail = 7201;
    private const long StreamCave = 7202;

    private static readonly string[] Required =
    [
        "z_m", "soil_thickness_m", "alluvium_m", "water_table_m", "water_level_m", "is_lake", "is_river",
        "river_width_m", "river_depth_m", "receiver", "cave_entrance",
    ];

    private readonly double _amp;
    private readonly long _detailSeed;
    private readonly long[] _caveSeeds;
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

    public double Wavelength { get; }

    public int NLevels { get; }

    public int NLayers { get; }

    public double[] Z { get; }

    public double[] Soil { get; }

    public double[] Alluvium { get; }

    public double[] ZGwMap { get; }

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

    /// <summary>입구 캡슐 축 시작점 (E, 3).</summary>
    public double[] CapA { get; private set; } = [];

    /// <summary>입구 캡슐 축 끝점 (E, 3).</summary>
    public double[] CapB { get; private set; } = [];

    public int NCapsules => CapA.Length / 3;

    public long NUnopened { get; private set; }

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
            return (double[])FieldSet.ToFloat64(a).Clone();
        }
        bool[] ImgB(string name) => (bool[])FieldSet.CastTo(fields[name], typeof(bool));
        Z = Img("z_m");
        if (Z.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("z_m 에 NaN 이나 inf 가 있습니다");
        }
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
        _caveSeeds = Enumerable.Range(0, NLevels).Select(k => SubSeed(Seed, StreamCave, k)).ToArray();
        BuildRivers(heroState.Rivers, fields, Img, ImgB);
        BuildCapsules(fields);
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

    private void BuildRivers(List<long[]> rivers, FieldSet fields, Func<string, double[]> img, Func<string, bool[]> imgB)
    {
        int n = Ny * Nx;
        (double[] xc, double[] yc) = CellCenters();
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
                ctrlXy[k * 2] = xc[ctrl[k]];
                ctrlXy[(k * 2) + 1] = yc[ctrl[k]];
            }
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

    /// <summary>동굴 층 k 의 수평 2D 노이즈 ν_k(x, y) (cave_noise).</summary>
    public double[] CaveNoise(double[] x, double[] y, int k)
    {
        if (x.Length == 0)
        {
            return [];
        }
        double[] pts = new double[x.Length * 3];
        for (int i = 0; i < x.Length; i++)
        {
            pts[i * 3] = x[i];
            pts[(i * 3) + 1] = y[i];
        }
        return Noise.Fbm3(pts, _caveSeeds[k], CaveOctaves, frequency: 1.0 / Wavelength);
    }

    /// <summary>층 k 통로 중심선까지 수평 거리 근사 δ_ν = |ν|/|∇ν| (cave_distance).</summary>
    public double[] CaveDistance(double[] x, double[] y, int k)
    {
        const double h = CaveGradStepM;
        int m = x.Length;
        double[] xs = new double[m * 5];
        double[] ys = new double[m * 5];
        for (int i = 0; i < m; i++)
        {
            xs[i] = x[i];
            xs[m + i] = x[i] + h;
            xs[(2 * m) + i] = x[i] - h;
            xs[(3 * m) + i] = x[i];
            xs[(4 * m) + i] = x[i];
            ys[i] = y[i];
            ys[m + i] = y[i];
            ys[(2 * m) + i] = y[i];
            ys[(3 * m) + i] = y[i] + h;
            ys[(4 * m) + i] = y[i] - h;
        }
        double[] v = CaveNoise(xs, ys, k);
        double floor = CaveGradFloor * 2.0 * Math.PI / Wavelength;
        double cap = CaveDistCapRadii * RPass;
        double[] o = new double[m];
        for (int i = 0; i < m; i++)
        {
            double gx = (v[m + i] - v[(2 * m) + i]) / (2 * h);
            double gy = (v[(3 * m) + i] - v[(4 * m) + i]) / (2 * h);
            double dist = Math.Abs(v[i]) / Math.Max(double.Hypot(gx, gy), floor);
            o[i] = Math.Min(dist, cap);
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

    private void BuildCapsules(FieldSet fields)
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
        var aL = new List<double>();
        var bL = new List<double>();
        NUnopened = 0;
        for (int k = 0; k < NLevels; k++)
        {
            var cellList = new List<int>();
            for (int c = 0; c < n; c++)
            {
                if (((ent[c] >> k) & 1) != 0 && double.IsFinite(CaveLevels[(k * n) + c]))
                {
                    cellList.Add(c);
                }
            }
            if (cellList.Count == 0)
            {
                continue;
            }
            var keepCells = new List<int>();
            var dirxL = new List<double>();
            var diryL = new List<double>();
            foreach (int c in cellList)
            {
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
                    keepCells.Add(c);
                    dirxL.Add(dirx);
                    diryL.Add(diry);
                }
            }
            if (keepCells.Count == 0)
            {
                continue;
            }
            int m = keepCells.Count;
            double step = 0.5 * r;
            double tOutMax = EntranceOutCells * dx;
            double[] tsOut = Arange(0.0, tOutMax + 1e-9, step);
            double tInMax = EntranceSearchWavelengths * Wavelength;
            double[] tsIn = Arange(step, tInMax + 1e-9, step);
            double[] sxo = new double[m * tsOut.Length];
            double[] syo = new double[m * tsOut.Length];
            double[] sxi = new double[m * tsIn.Length];
            double[] syi = new double[m * tsIn.Length];
            for (int q = 0; q < m; q++)
            {
                int c = keepCells[q];
                for (int t = 0; t < tsOut.Length; t++)
                {
                    sxo[(q * tsOut.Length) + t] = xc[c] + (dirxL[q] * tsOut[t]);
                    syo[(q * tsOut.Length) + t] = yc[c] + (diryL[q] * tsOut[t]);
                }
                for (int t = 0; t < tsIn.Length; t++)
                {
                    sxi[(q * tsIn.Length) + t] = xc[c] - (dirxL[q] * tsIn[t]);
                    syi[(q * tsIn.Length) + t] = yc[c] - (diryL[q] * tsIn[t]);
                }
            }
            double[] zsOut = Bilinear(z, sxo, syo);
            double[] nu = CaveDistance(sxi, syi, k);
            double[] valid = new double[n];
            for (int c = 0; c < n; c++)
            {
                valid[c] = double.IsFinite(CaveLevels[(k * n) + c]) ? 1.0 : 0.0;
            }
            double[] wv = Bilinear(valid, sxi, syi);
            for (int q = 0; q < m; q++)
            {
                int c = keepCells[q];
                double zk = CaveLevels[(k * n) + c];
                double tOut = tOutMax;
                bool found = false;
                for (int t = 0; t < tsOut.Length; t++)
                {
                    if (zsOut[(q * tsOut.Length) + t] < zk)
                    {
                        tOut = tsOut[t] + r;
                        found = true;
                        break;
                    }
                }
                NUnopened += found ? 0 : 1;
                double tIn = 0.5 * Wavelength;
                for (int t = 0; t < tsIn.Length; t++)
                {
                    int idx = (q * tsIn.Length) + t;
                    if (nu[idx] < CaveCoreFraction * r && wv[idx] > 0.5)
                    {
                        tIn = tsIn[t] + r;
                        break;
                    }
                }
                aL.AddRange([xc[c] - (dirxL[q] * tIn), yc[c] - (diryL[q] * tIn), zk]);
                bL.AddRange([xc[c] + (dirxL[q] * tOut), yc[c] + (diryL[q] * tOut), zk]);
            }
        }
        CapA = aL.ToArray();
        CapB = bL.ToArray();
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
        double r = RPass;
        for (int e = 0; e < NCapsules; e++)
        {
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
        int kN = NLevels;
        int lN = NLayers;
        int ny = Ny;
        int nx = Nx;
        int n = ny * nx;
        double[] zMap = new double[cN];
        double[] soil = new double[cN];
        double[] alluv = new double[cN];
        double[] zgw = new double[cN];
        double[] lake = new double[cN];
        double[] lakeDist = new double[cN];
        double[] caveZ = new double[cN * kN];
        double[] caveW = new double[cN * kN];
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
            lakeDist[c] = Bl(LakeDist);
            int[] corner = [c00, c10, c01, c11];
            double[] wq = [w00, w10, w01, w11];
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
            for (int k = 0; k < kN; k++)
            {
                ws = 0.0;
                acc = 0.0;
                for (int q = 0; q < 4; q++)
                {
                    double v = CaveLevels[(k * n) + corner[q]];
                    if (wq[q] > 0.0 && !double.IsNaN(v))
                    {
                        ws += wq[q];
                        acc += wq[q] * v;
                    }
                }
                caveW[(c * kN) + k] = ws;
                caveZ[(c * kN) + k] = ws > 0.0 ? acc / ws : NpMath.NaN;
            }
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
        double[] nu = Enumerable.Repeat(FarM, cN * kN).ToArray();
        for (int k = 0; k < kN; k++)
        {
            int[] sel = Enumerable.Range(0, cN).Where(c => caveW[(c * kN) + k] > 0).ToArray();
            if (sel.Length > 0)
            {
                double[] dist = CaveDistance(sel.Select(c => x[c]).ToArray(), sel.Select(c => y[c]).ToArray(), k);
                for (int q = 0; q < sel.Length; q++)
                {
                    nu[(sel[q] * kN) + k] = dist[q];
                }
            }
        }
        return new ColumnValues
        {
            ZSurface = zSurface,
            ZMap = zMap,
            M = m,
            Soil = soil,
            Alluvium = alluv,
            ZGw = zgw,
            LakeLevel = lake,
            LakeDist = lakeDist,
            RiverDist = l,
            RiverHalfWidth = hw,
            RiverDepth = dep,
            RiverBank = zq,
            RiverLevel = lev,
            WaterDist = waterDist,
            CaveZ = caveZ,
            CaveW = caveW,
            NuDist = nu,
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

    private VolumeEval EvaluateCore(ColumnValues cols, long[] col, double[] px, double[] py, double[] up, double noiseBandM)
    {
        int mN = col.Length;
        double[] xi = new double[mN];
        if (_amp > 0)
        {
            var sel = new List<int>();
            for (int p = 0; p < mN; p++)
            {
                bool need = cols.M[col[p]] > 0;
                if (double.IsFinite(noiseBandM))
                {
                    need &= Math.Abs(up[p] - cols.ZSurface[col[p]]) <= noiseBandM;
                }
                if (need)
                {
                    sel.Add(p);
                }
            }
            if (sel.Count > 0)
            {
                double[] nz = DetailNoise(sel.Select(p => px[p]).ToArray(), sel.Select(p => py[p]).ToArray(), sel.Select(p => up[p]).ToArray());
                for (int q = 0; q < sel.Count; q++)
                {
                    xi[sel[q]] = nz[q];
                }
            }
        }
        double[] oD0 = new double[mN];
        double[] oD1 = new double[mN];
        double[] oDCave = new double[mN];
        double[] oD = new double[mN];
        double[] oHw = new double[mN];
        byte[] oMat = new byte[mN];
        byte[] oSolid = new byte[mN];
        byte[] oRock = new byte[mN];
        bool[] oWater = new bool[mN];
        int nL = NLayers;
        int nLv = NLevels;
        double amp = _amp;
        double rPass = RPass;
        double dx = Dx;
        (double bx0, double by0, double bsize, int nbx, int nby) = _bucket;
        double[] capA = CapA;
        double[] capB = CapB;
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
            double dmin = double.PositiveInfinity;
            for (int k = 0; k < nLv; k++)
            {
                double w = cols.CaveW[(c * nLv) + k];
                if (w > 0.0)
                {
                    double tK = NpMath.PyMax(
                        NpMath.PyMax(cols.NuDist[(c * nLv) + k] - rPass, Math.Abs(z - cols.CaveZ[(c * nLv) + k]) - rPass),
                        (0.5 - w) * dx);
                    dmin = NpMath.PyMin(dmin, tK);
                }
            }
            if (_bItems.Length > 0)
            {
                int ib = Math.Clamp((int)Math.Floor((px[p] - bx0) / bsize), 0, nbx - 1);
                int jb = Math.Clamp((int)Math.Floor((py[p] - by0) / bsize), 0, nby - 1);
                int bb = (jb * nbx) + ib;
                for (long q = _bStart[bb]; q < _bStart[bb + 1]; q++)
                {
                    long e = _bItems[q];
                    dmin = NpMath.PyMin(dmin, CapsuleDist(px[p], py[p], z, capA, capB, e) - rPass);
                }
            }
            double dcave = double.PositiveInfinity;
            if (dmin < double.PositiveInfinity)
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
                double blend = NpMath.PyMin(cols.Alluvium[c] / AlluviumClipBlendM, 1.0);
                double plane = surf - cols.Alluvium[c] - cols.Soil[c] + ((cols.Soil[c] + (2.0 * rPass)) * (1.0 - blend));
                dcave = NpMath.PyMax(NpMath.PyMax(dmin, dSol), z - plane);
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
            double lvl = d1 < 0.0 ? cols.ZGw[c] : hW;
            oD0[p] = d0;
            oD1[p] = d1;
            oDCave[p] = dcave;
            oD[p] = dFinal;
            oSolid[p] = solid;
            oRock[p] = rock;
            oMat[p] = dFinal <= 0.0 ? solid : MaterialEmpty;
            oWater[p] = dFinal > 0.0 && z < lvl;
            oHw[p] = hW;
        });
        double[] surface = new double[mN];
        bool[] under = new bool[mN];
        double[] zgw = new double[mN];
        for (int p = 0; p < mN; p++)
        {
            long c = col[p];
            surface[p] = cols.ZSurface[c] + (cols.M[c] * _amp * xi[p]);
            under[p] = oD1[p] < 0;
            zgw[p] = cols.ZGw[c];
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
        if (points.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("points 에 NaN 이나 inf 가 있습니다");
        }
        return points;
    }

    private static VolumeEval Concat(List<VolumeEval> parts)
    {
        T[] C<T>(Func<VolumeEval, T[]> f) => parts.SelectMany(f).ToArray();
        return new VolumeEval
        {
            D0 = C(p => p.D0),
            D1 = C(p => p.D1),
            DCave = C(p => p.DCave),
            D = C(p => p.D),
            WaterLevel = C(p => p.WaterLevel),
            Material = C(p => p.Material),
            SolidMaterial = C(p => p.SolidMaterial),
            Rock = C(p => p.Rock),
            Water = C(p => p.Water),
            Surface = C(p => p.Surface),
            Under = C(p => p.Under),
            ZGw = C(p => p.ZGw),
        };
    }

    /// <summary>점마다 계산의 모든 중간값 (evaluate). points: (M, 3) 국소 (동, 북, 위) [m].</summary>
    public VolumeEval Evaluate(double[] points, double noiseBandM = double.PositiveInfinity)
    {
        double[] pts = CheckPoints(points);
        int mN = pts.Length / 3;
        if (mN == 0)
        {
            return EvaluateCore(Columns([], []), [], [], [], [], 0.0);
        }
        var parts = new List<VolumeEval>();
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
            long[] col = Enumerable.Range(0, len).Select(i => (long)i).ToArray();
            parts.Add(EvaluateCore(cols, col, px, py, pz, noiseBandM));
        }
        return parts.Count == 1 ? parts[0] : Concat(parts);
    }

    /// <summary>3D 샘플 함수 (sample): (d float32, material uint8 (빈 곳 255), water bool).</summary>
    public (float[] D, byte[] Material, bool[] Water) Sample(double[] points)
    {
        VolumeEval ev = Evaluate(points);
        return (Array.ConvertAll(ev.D, v => (float)v), ev.Material, ev.Water);
    }

    /// <summary>
    /// 수평 기둥 (x, y) (C,) 와 기둥마다 높이 up (C, Z) 의 격자를 한꺼번에 계산합니다 (evaluate_grid). 반환: keys 의 (C, Z) 배열.
    /// </summary>
    public Dictionary<string, Array> EvaluateGrid(double[] x, double[] y, double[] up, int zCount, double noiseBandM = double.PositiveInfinity, params string[] keys)
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
        if (up.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("up 에 NaN 이나 inf 가 있습니다");
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
            for (int c = 0; c < len; c++)
            {
                for (int k = 0; k < zCount; k++)
                {
                    int p = (c * zCount) + k;
                    col[p] = c;
                    px[p] = x[s + c];
                    py[p] = y[s + c];
                    uz[p] = up[((s + c) * zCount) + k];
                }
            }
            VolumeEval ev = EvaluateCore(cols, col, px, py, uz, noiseBandM);
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
    public double[] WaterTable(double[] x, double[] y) => ByChunks((xx, yy) => Columns(xx, yy).ZGw, x, y);
}
