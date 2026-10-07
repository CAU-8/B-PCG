using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Geology;

/// <summary>지질 템플릿 하나. Layers 는 위층부터 (암석 번호, 두께 [m]), Basement 는 기반암 암석 번호.</summary>
public sealed record Template(string Name, (byte Rock, double Thickness)[] Layers, byte Basement);

/// <summary>
/// 솔버와 3D 함수가 읽는 층 기둥 묶음 (LayerColumns). Bottom (N, L), Rock·KMult·SCrit (N, L+1) 행 우선.
/// </summary>
public sealed class LayerColumns
{
    public LayerColumns(double[] bottom, byte[] rock, double[] kMult, double[] sCrit, int nLayers)
    {
        if (nLayers < 0 || bottom.Length % Math.Max(nLayers, 1) != 0)
        {
            throw new ArgumentException("strata_bottom 은 (N, L) 이어야 합니다");
        }
        int n = nLayers == 0 ? rock.Length : bottom.Length / nLayers;
        if (rock.Length != n * (nLayers + 1))
        {
            throw new ArgumentException("strata_rock 모양이 strata_bottom 모양과 맞지 않습니다 ((N, L+1) 이어야 함)");
        }
        if (kMult.Length != rock.Length || sCrit.Length != rock.Length)
        {
            throw new ArgumentException("k_mult, s_crit 는 rock 과 같은 (N, L+1) 모양이어야 합니다");
        }
        Bottom = bottom;
        Rock = rock;
        KMult = kMult;
        SCrit = sCrit;
        NLayers = nLayers;
        NCells = n;
    }

    public double[] Bottom { get; }

    public byte[] Rock { get; }

    public double[] KMult { get; }

    public double[] SCrit { get; }

    /// <summary>층 경계 수 L (암석 열은 L+1).</summary>
    public int NLayers { get; }

    public int NCells { get; }

    /// <summary>층 바닥과 암석에서 암석 표(K 배율, S_crit)를 찾아 묶습니다.</summary>
    public static LayerColumns FromColumns(double[] strataBottom, byte[] strataRock, int nLayers)
    {
        double[] k = new double[strataRock.Length];
        double[] s = new double[strataRock.Length];
        for (int i = 0; i < strataRock.Length; i++)
        {
            byte r = strataRock[i];
            if (r >= Rocks.NRocks)
            {
                throw new ArgumentException($"strata_rock 의 암석 번호는 0..{Rocks.NRocks - 1} 이어야 합니다");
            }
            k[i] = Rocks.KMult[r];
            s[i] = Rocks.SCrit[r];
        }
        return new LayerColumns(strataBottom, strataRock, k, s, nLayers);
    }

    /// <summary>고도 z ((N,) 또는 (N, M) 행 우선) 가 속한 층 번호.</summary>
    public int[] LayerIndex(double[] z) => Model.LayerIndex(Bottom, NLayers, z);

    /// <summary>고도 z 의 암석 번호.</summary>
    public byte[] RockAt(double[] z) => Model.RockAt(Bottom, Rock, NLayers, z);
}

/// <summary>
/// 3D 지질 모델: 템플릿 3개, 습곡, 층 기둥, 높이별 암석 (src/bpcg/geology/model.py, docs/pipeline.md 5.2).
/// </summary>
public static class Model
{
    public const byte Platform = 0;
    public const byte FoldThrust = 1;
    public const byte Arc = 2;

    public static readonly Template[] Templates =
    [
        new("carbonate_platform",
            [(Rocks.Sandstone, 150.0), (Rocks.Limestone, 500.0), (Rocks.Shale, 250.0), (Rocks.Limestone, 600.0),
                (Rocks.Sandstone, 300.0)],
            Rocks.Granite),
        new("fold_thrust",
            [(Rocks.Shale, 300.0), (Rocks.Limestone, 800.0), (Rocks.Shale, 400.0), (Rocks.Sandstone, 500.0),
                (Rocks.Limestone, 600.0)],
            Rocks.Granite),
        new("basement_arc", [(Rocks.Volcanic, 800.0)], Rocks.Granite),
    ];

    public static readonly int NTemplates = Templates.Length;
    public static readonly int NTemplateLayers = MaxTemplateLayers();
    public static readonly int NBasementSplits = MaxBasementSplits();
    public static readonly int NLayers = NTemplateLayers + NBasementSplits;

    public const double ArcBeltFraction = 0.4;
    public const double ArcFoldGapM = 50_000.0;
    public const double FoldNoiseWeight = 0.5;
    private const long FoldNoiseStream = 5201;

    private static readonly string[] RequiredTemplateFields =
        ["subduction_side", "convergence_kind", "dist_convergent_m", "dist_arc_m"];

    private static int MaxTemplateLayers()
    {
        int m = 0;
        foreach (Template t in Templates)
        {
            m = Math.Max(m, t.Layers.Length);
        }
        return m;
    }

    private static int MaxBasementSplits()
    {
        int m = 0;
        foreach (Template t in Templates)
        {
            if (Rocks.MetamorphicSeries.TryGetValue(t.Basement, out (double, byte)[]? s))
            {
                m = Math.Max(m, s.Length);
            }
        }
        return m;
    }

    private static Array Field(FieldSet fields, string name, int? n = null)
    {
        if (!fields.Contains(name))
        {
            throw new ArgumentException($"필드 '{name}' 가 없습니다");
        }
        Array a = fields[name];
        if (n is int nn && a.Length != nn)
        {
            throw new ArgumentException($"필드 '{name}' 의 길이 {a.Length} 가 다른 필드 길이 {nn} 과 다릅니다");
        }
        return a;
    }

    private static void CheckTemplateId(byte[] t)
    {
        foreach (byte v in t)
        {
            if (v >= NTemplates)
            {
                throw new ArgumentException($"template_id 는 0..{NTemplates - 1} 이어야 합니다");
            }
        }
    }

    /// <summary>칸마다 지질 템플릿 번호를 정합니다 (assign_template). 2 와 1 이 겹치면 2.</summary>
    public static byte[] AssignTemplate(FieldSet fields, Config cfg)
    {
        sbyte[] side = (sbyte[])Field(fields, "subduction_side");
        int n = side.Length;
        byte[] kind = (byte[])Field(fields, "convergence_kind", n);
        double[] dist = FieldSet.ToFloat64(Field(fields, "dist_convergent_m", n));
        double[] dArc = FieldSet.ToFloat64(Field(fields, "dist_arc_m", n));
        double foldW = cfg.F("geology.fold_belt_width_m");
        double arcHalf = ArcBeltFraction * cfg.F("geology.arc_belt_width_m");
        byte[] output = new byte[n];
        for (int c = 0; c < n; c++)
        {
            bool upper = side[c] == 1;
            bool isArc = upper && Math.Abs(dist[c] - dArc[c]) < arcHalf;
            bool isFold = (kind[c] == 3 && dist[c] < foldW)
                || (upper && dist[c] > dArc[c] + ArcFoldGapM && dist[c] < foldW);
            if (isFold)
            {
                output[c] = FoldThrust;
            }
            if (isArc)
            {
                output[c] = Arc;
            }
        }
        return output;
    }

    // 행성 시드에서 용도별 시드 (31비트 양수)
    private static long SubSeed(long seed, long stream) => (long)(Hashing.Hash3(seed, stream, 0) >> 33);

    private static (double[] Phase, string Backend) FoldPhase(double[] dist, Config cfg, double[]? unitPoints)
    {
        double lam = cfg.F("geology.fold_wavelength_m");
        if (!(lam > 0))
        {
            throw new ArgumentException($"fold_wavelength_m 은 0 보다 커야 합니다: {lam}");
        }
        int n = dist.Length;
        double twoPi = 2.0 * Math.PI;
        double[] phase = new double[n];
        for (int c = 0; c < n; c++)
        {
            phase[c] = double.IsFinite(dist[c]) ? twoPi * dist[c] / lam : 0.0;
        }
        if (unitPoints is null)
        {
            return (phase, "none");
        }
        if (unitPoints.Length != n * 3)
        {
            throw new ArgumentException($"unit_points 는 ({n}, 3) 모양이어야 합니다");
        }
        double scale = cfg.F("planet.radius_m") / lam;
        double[] p = new double[unitPoints.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = unitPoints[i] * scale;
        }
        double[] noise = Noise.Fbm3(p, SubSeed(cfg.I("planet.seed"), FoldNoiseStream));
        for (int c = 0; c < n; c++)
        {
            phase[c] += FoldNoiseWeight * (double.IsFinite(dist[c]) ? noise[c] : 0.0);
        }
        return (phase, "fbm3");
    }

    /// <summary>습곡 위상에서 층 변위 Δ = A·(U/U_max)·sin(φ_fold) (템플릿 1 만, 나머지 0).</summary>
    public static double[] FoldDisplacementFromPhase(
        double[] foldPhase, double[] uplift, byte[] templateId, Config cfg, double? uMax = null)
    {
        CheckTemplateId(templateId);
        int n = templateId.Length;
        if (foldPhase.Length != n || uplift.Length != n)
        {
            throw new ArgumentException($"fold_phase, uplift 는 template_id 와 같은 ({n},) 모양이어야 합니다");
        }
        double[] uPos = new double[n];
        double mx = double.NegativeInfinity;
        for (int c = 0; c < n; c++)
        {
            uPos[c] = double.IsFinite(uplift[c]) ? Math.Max(uplift[c], 0.0) : 0.0;
            mx = Math.Max(mx, uPos[c]);
        }
        double um = uMax ?? (n > 0 ? mx : 0.0);
        if (!double.IsFinite(um) || um < 0)
        {
            throw new ArgumentException($"u_max 는 0 이상의 유한한 값이어야 합니다: {um}");
        }
        double amp = cfg.F("geology.fold_amplitude_m");
        double[] disp = new double[n];
        if (um == 0.0)
        {
            return disp;
        }
        for (int c = 0; c < n; c++)
        {
            double ratio = Math.Min(uPos[c] / um, 1.0);
            double d = amp * ratio * Math.Sin(double.IsFinite(foldPhase[c]) ? foldPhase[c] : 0.0);
            disp[c] = templateId[c] == FoldThrust ? d : 0.0;
        }
        return disp;
    }

    /// <summary>습곡 위상과 층 변위 (fold_displacement). unitPoints 가 있으면 위상에 0.5·fbm3(p·R/λ) 를 더합니다.</summary>
    public static (double[] Phase, double[] Disp) FoldDisplacement(
        FieldSet fields, byte[] templateId, Config cfg, double[]? unitPoints = null, double? uMax = null)
    {
        CheckTemplateId(templateId);
        int n = templateId.Length;
        double[] dist = FieldSet.ToFloat64(Field(fields, "dist_convergent_m", n));
        double[] u = FieldSet.ToFloat64(Field(fields, "uplift_m_per_yr", n));
        (double[] phase, _) = FoldPhase(dist, cfg, unitPoints);
        return (phase, FoldDisplacementFromPhase(phase, u, templateId, cfg, uMax));
    }

    /// <summary>
    /// 템플릿별 층 바닥 깊이 (N_TEMPLATES, L) 와 변성을 적용한 암석 (N_TEMPLATES, L+1) (template_columns).
    /// </summary>
    public static (double[] Depth, byte[] Rock) TemplateColumns(Config cfg)
    {
        double tSurf = cfg.F("geology.surface_temperature_c");
        double grad = cfg.F("geology.geotherm_c_per_m");
        if (!(double.IsFinite(grad) && grad > 0))
        {
            throw new ArgumentException($"geotherm_c_per_m 은 0 보다 커야 합니다: {grad}");
        }
        int nl = NLayers;
        double[] depth = new double[NTemplates * nl];
        byte[] rock = new byte[NTemplates * (nl + 1)];
        for (int t = 0; t < NTemplates; t++)
        {
            Template tpl = Templates[t];
            var bottoms = new List<double>();
            var rocks = new List<int>();
            double top = 0.0;
            foreach ((byte r, double thickness) in tpl.Layers)
            {
                double mid = top + (0.5 * thickness);
                rocks.Add(Rocks.Metamorphose(r, Rocks.PeakTemperatureC(mid, cfg)));
                top += thickness;
                bottoms.Add(top);
            }
            while (bottoms.Count < NTemplateLayers)
            {
                bottoms.Add(top);
                rocks.Add(-1);
            }
            byte b = tpl.Basement;
            double tTop = tSurf + (grad * top);
            rocks.Add(Rocks.Metamorphose(b, tTop));
            double segTop = top;
            (double TMin, byte Product)[] series =
                Rocks.MetamorphicSeries.TryGetValue(b, out (double, byte)[]? s) ? s : [];
            foreach ((double tMin, _) in series)
            {
                segTop = NpMath.PyMax(segTop, (tMin - tSurf) / grad);
                bottoms.Add(segTop);
                rocks.Add(Rocks.Metamorphose(b, NpMath.PyMax(tMin, tTop)));
            }
            while (bottoms.Count < nl)
            {
                bottoms.Add(segTop);
                rocks.Insert(rocks.Count - 1, -1);
            }
            // 두께 0 층은 바로 아래 층의 암석을 씁니다(아래에서 위로 채움).
            double[] thick = new double[nl];
            double prev = 0.0;
            for (int i = 0; i < nl; i++)
            {
                thick[i] = bottoms[i] - prev;
                prev = bottoms[i];
            }
            for (int i = nl - 1; i >= 0; i--)
            {
                if (thick[i] <= 0.0 || rocks[i] < 0)
                {
                    rocks[i] = rocks[i + 1];
                }
            }
            for (int i = 0; i < nl; i++)
            {
                depth[(t * nl) + i] = bottoms[i];
            }
            for (int i = 0; i <= nl; i++)
            {
                rock[(t * (nl + 1)) + i] = (byte)rocks[i];
            }
        }
        return (depth, rock);
    }

    /// <summary>칸마다 층 기둥 (build_columns): z_top = exhumation + Δ, 층 i 바닥 = z_top − Σ t_j.</summary>
    public static (double[] StrataBottom, byte[] StrataRock) BuildColumns(
        byte[] templateId, double[] exhumationM, double[]? foldDispM, Config cfg)
    {
        CheckTemplateId(templateId);
        int n = templateId.Length;
        if (exhumationM.Length != n)
        {
            throw new ArgumentException($"exhumation_m 은 template_id 와 같은 ({n},) 모양이어야 합니다");
        }
        double[] zTop = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(exhumationM[c]))
            {
                throw new ArgumentException("exhumation_m 에 NaN 이나 inf 가 있습니다");
            }
            zTop[c] = exhumationM[c];
        }
        if (foldDispM is not null)
        {
            if (foldDispM.Length != n)
            {
                throw new ArgumentException($"fold_disp_m 은 template_id 와 같은 ({n},) 모양이어야 합니다");
            }
            for (int c = 0; c < n; c++)
            {
                if (!double.IsFinite(foldDispM[c]))
                {
                    throw new ArgumentException("fold_disp_m 에 NaN 이나 inf 가 있습니다");
                }
                zTop[c] += foldDispM[c];
            }
        }
        (double[] depth, byte[] rock) = TemplateColumns(cfg);
        int nl = NLayers;
        double[] bottom = new double[n * nl];
        byte[] rocksOut = new byte[n * (nl + 1)];
        for (int c = 0; c < n; c++)
        {
            int t = templateId[c];
            for (int i = 0; i < nl; i++)
            {
                bottom[(c * nl) + i] = zTop[c] - depth[(t * nl) + i];
            }
            Array.Copy(rock, t * (nl + 1), rocksOut, c * (nl + 1), nl + 1);
        }
        return (bottom, rocksOut);
    }

    /// <summary>칸 c 에서 고도 z 가 속한 층 번호 (0..L). 경계와 같은 z 는 아래층, NaN 은 L.</summary>
    public static int LayerIndexAt(double[] bottom, int nLayers, int c, double z)
    {
        int b0 = c * nLayers;
        for (int i = 0; i < nLayers; i++)
        {
            if (bottom[b0 + i] < z)
            {
                return i;
            }
        }
        return nLayers;
    }

    private static int CheckZ(int nCells, double[] z)
    {
        if (nCells == 0 || z.Length % nCells != 0)
        {
            throw new ArgumentException($"z 는 ({nCells},) 또는 ({nCells}, M) 모양이어야 합니다 (받은 길이: {z.Length})");
        }
        return z.Length / nCells;
    }

    /// <summary>고도 z ((N,) 또는 (N, M)) 가 속한 층 번호 (layer_index).</summary>
    public static int[] LayerIndex(double[] strataBottom, int nLayers, double[] z)
    {
        int n = strataBottom.Length / nLayers;
        int m = CheckZ(n, z);
        int[] output = new int[z.Length];
        Parallelism.For(0, n, c =>
        {
            for (int k = 0; k < m; k++)
            {
                output[(c * m) + k] = LayerIndexAt(strataBottom, nLayers, c, z[(c * m) + k]);
            }
        });
        return output;
    }

    /// <summary>칸마다 고도 z 의 암석 번호 (rock_at).</summary>
    public static byte[] RockAt(double[] strataBottom, byte[] strataRock, int nLayers, double[] z)
    {
        int n = strataBottom.Length / nLayers;
        if (strataRock.Length != n * (nLayers + 1))
        {
            throw new ArgumentException("strata_rock 모양이 strata_bottom 모양과 맞지 않습니다");
        }
        int m = CheckZ(n, z);
        byte[] output = new byte[z.Length];
        Parallelism.For(0, n, c =>
        {
            for (int k = 0; k < m; k++)
            {
                output[(c * m) + k] = strataRock[(c * (nLayers + 1)) + LayerIndexAt(strataBottom, nLayers, c, z[(c * m) + k])];
            }
        });
        return output;
    }

    /// <summary>임의의 점들의 암석 번호 (rock_at_points): 점 k 는 칸 cells[k] 의 기둥에서 고도 z[k].</summary>
    public static byte[] RockAtPoints(double[] strataBottom, byte[] strataRock, int nLayers, long[] cells, double[] z)
    {
        int n = strataBottom.Length / nLayers;
        if (cells.Length != z.Length)
        {
            throw new ArgumentException("cells 와 z 는 같은 (M,) 모양이어야 합니다");
        }
        foreach (long c in cells)
        {
            if (c < 0 || c >= n)
            {
                throw new ArgumentException($"cells 는 0..{n - 1} 이어야 합니다");
            }
        }
        byte[] output = new byte[z.Length];
        Parallelism.For(0, z.Length, k =>
        {
            int c = (int)cells[k];
            output[k] = strataRock[(c * (nLayers + 1)) + LayerIndexAt(strataBottom, nLayers, c, z[k])];
        });
        return output;
    }

    /// <summary>지표 고도 z 에 드러난 암석 번호 (surface_rock).</summary>
    public static byte[] SurfaceRock(LayerColumns columns, double[] z) => columns.RockAt(z);

    /// <summary>
    /// 1단계 지질 (generate_geology): 템플릿 → 습곡 → 층 기둥. 반환 fields: template_id, fold_phase,
    /// strata_bottom_m (N, L), strata_rock (N, L+1). diag: template_counts, u_max_m_per_yr, noise, seconds.
    /// </summary>
    public static (FieldSet Fields, LayerColumns Columns, OrderedDictionary<string, object?> Diag) GenerateGeology(
        FieldSet fields, Config cfg, double[]? unitPoints = null, double? uMax = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        foreach (string name in RequiredTemplateFields)
        {
            Field(fields, name);
        }
        Field(fields, "uplift_m_per_yr");
        Field(fields, "exhumation_m");
        byte[] templateId = AssignTemplate(fields, cfg);
        int n = templateId.Length;
        double[] u = FieldSet.ToFloat64(Field(fields, "uplift_m_per_yr", n));
        (double[] phase, string backend) = FoldPhase(FieldSet.ToFloat64(Field(fields, "dist_convergent_m", n)), cfg, unitPoints);
        if (uMax is null)
        {
            double mx = double.NegativeInfinity;
            bool any = false;
            foreach (double v in u)
            {
                if (double.IsFinite(v))
                {
                    any = true;
                    mx = Math.Max(mx, Math.Max(v, 0.0));
                }
            }
            uMax = any ? mx : 0.0;
        }
        double[] disp = FoldDisplacementFromPhase(phase, u, templateId, cfg, uMax);
        double[] exh = FieldSet.ToFloat64(Field(fields, "exhumation_m", n));
        (double[] bottom, byte[] rock) = BuildColumns(templateId, exh, disp, cfg);
        LayerColumns columns = LayerColumns.FromColumns(bottom, rock, NLayers);
        var output = new FieldSet { ["template_id"] = templateId, ["fold_phase"] = phase };
        output.Set2D("strata_bottom_m", columns.Bottom, NLayers);
        output.Set2D("strata_rock", columns.Rock, NLayers + 1);
        var counts = new List<object?>();
        long[] cnt = new long[NTemplates];
        foreach (byte t in templateId)
        {
            cnt[t]++;
        }
        foreach (long c in cnt)
        {
            counts.Add(c);
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["template_counts"] = counts,
            ["u_max_m_per_yr"] = uMax.Value,
            ["noise"] = backend,
            ["seconds"] = Stopwatch.GetElapsedTime(t0).TotalSeconds,
        };
        return (output, columns, diag);
    }
}
