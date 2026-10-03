using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hero;
using Bpcg.IO;

namespace Bpcg.Bake;

/// <summary>
/// 생성 결과 묶음(bundle) 쓰기·읽기 (src/bpcg/bake/bundle.py, docs/pipeline.md 11장). read_bundle 의 결과이기도 합니다.
/// </summary>
public sealed class Bundle(CellGraph graph, FieldSet fields, OrderedDictionary<string, object?> manifest,
    OrderedDictionary<string, object?> meta, List<long[]>? rivers)
{
    public const string Format = "bpcg-bundle";
    public const int FormatVersion = 1;
    public const string ManifestFile = "manifest.json";
    public const string GraphFile = "graph.npz";
    public const string RiversFile = "rivers.npz";
    public const int JsonArrayMax = 1_024;

    public CellGraph Graph { get; } = graph;

    public FieldSet Fields { get; } = fields;

    public OrderedDictionary<string, object?> Manifest { get; } = manifest;

    public OrderedDictionary<string, object?> Meta { get; } = meta;

    public List<long[]>? Rivers { get; } = rivers;

    /// <summary>저장소의 현재 git 커밋 해시. git 이 없거나 저장소가 아니면 null (git_commit).</summary>
    public static string? GitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = Paths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            string output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(10_000))
            {
                p.Kill();
                return null;
            }
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>원소 형식의 numpy dtype 이름.</summary>
    public static string DtypeName(Type t) => t switch
    {
        Type x when x == typeof(double) => "float64",
        Type x when x == typeof(float) => "float32",
        Type x when x == typeof(long) => "int64",
        Type x when x == typeof(int) => "int32",
        Type x when x == typeof(short) => "int16",
        Type x when x == typeof(sbyte) => "int8",
        Type x when x == typeof(byte) => "uint8",
        Type x when x == typeof(bool) => "bool",
        Type x when x == typeof(ulong) => "uint64",
        _ => t.Name,
    };

    private static object? NestedList(Array data, long[] shape, int dim, long offset, int? maxArray)
    {
        if (dim == shape.Length - 1 || shape.Length == 0)
        {
            long len = shape.Length == 0 ? 1 : shape[dim];
            var l = new List<object?>();
            for (long i = 0; i < len; i++)
            {
                l.Add(Jsonable(data.GetValue(offset + i), maxArray));
            }
            return shape.Length == 0 ? l[0] : l;
        }
        long stride = 1;
        for (int k = dim + 1; k < shape.Length; k++)
        {
            stride *= shape[k];
        }
        var o = new List<object?>();
        for (long i = 0; i < shape[dim]; i++)
        {
            o.Add(NestedList(data, shape, dim + 1, offset + (i * stride), maxArray));
        }
        return o;
    }

    /// <summary>
    /// JSON 으로 쓸 수 있는 값으로 바꿉니다 (jsonable). NaN·inf 는 null, 원소가 maxArray 보다 많은 배열은
    /// {"__array__": 모양, "dtype"}, 긴 목록은 {"__list__": 길이}.
    /// </summary>
    public static object? Jsonable(object? obj, int? maxArray = JsonArrayMax)
    {
        switch (obj)
        {
            case null or bool or string:
                return obj;
            case long or int or short or sbyte or byte or ushort or uint:
                return Convert.ToInt64(obj, System.Globalization.CultureInfo.InvariantCulture);
            case ulong ul:
                return ul;
            case double d:
                return double.IsFinite(d) ? d : null;
            case float f:
                return float.IsFinite(f) ? (double)f : null;
            case NpyArray na:
                if (maxArray is int ma && na.Count > ma)
                {
                    return new OrderedDictionary<string, object?>
                    {
                        ["__array__"] = na.Shape.Select(s => (object?)s).ToList(),
                        ["dtype"] = DtypeName(na.Data.GetType().GetElementType()!),
                    };
                }
                return NestedList(na.Data, na.Shape, 0, 0, maxArray);
            case Array arr:
                if (maxArray is int mb && arr.Length > mb)
                {
                    return new OrderedDictionary<string, object?>
                    {
                        ["__array__"] = new List<object?> { (long)arr.Length },
                        ["dtype"] = DtypeName(arr.GetType().GetElementType()!),
                    };
                }
                var la = new List<object?>(arr.Length);
                foreach (object? x in arr)
                {
                    la.Add(Jsonable(x, maxArray));
                }
                return la;
            case IEnumerable<KeyValuePair<string, object?>> dict:
                var od = new OrderedDictionary<string, object?>();
                foreach (KeyValuePair<string, object?> kv in dict)
                {
                    od[kv.Key] = Jsonable(kv.Value, maxArray);
                }
                return od;
            case IDictionary gd:
                var od2 = new OrderedDictionary<string, object?>();
                foreach (DictionaryEntry kv in gd)
                {
                    od2[Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture)!] = Jsonable(kv.Value, maxArray);
                }
                return od2;
            case System.Runtime.CompilerServices.ITuple tup:
                var lt = new List<object?>();
                for (int i = 0; i < tup.Length; i++)
                {
                    lt.Add(Jsonable(tup[i], maxArray));
                }
                return lt;
            case IEnumerable seq:
                var items = seq.Cast<object?>().ToList();
                if (maxArray is int mc && items.Count > mc)
                {
                    return new OrderedDictionary<string, object?> { ["__list__"] = (long)items.Count };
                }
                return items.Select(x => Jsonable(x, maxArray)).ToList();
            default:
                return obj.ToString();
        }
    }

    private static void WriteTextAtomic(string path, string text)
    {
        string tmp = path + ".part";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>obj 를 jsonable 로 바꿔 들여쓰기 2 의 UTF-8 JSON 으로 씁니다 (write_json).</summary>
    public static void WriteJson(string path, object? obj, int? maxArray = JsonArrayMax)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        object? data = Jsonable(obj, maxArray);
        WriteTextAtomic(path, PyJson.DumpsManifest(data) + "\n");
    }

    private static OrderedDictionary<string, object?> GraphInfo(CellGraph graph) => new()
    {
        ["kind"] = graph.Kind,
        ["shape"] = graph.Shape.Select(v => (object?)v).ToList(),
        ["n_cells"] = (long)graph.NCells,
        ["radius_m"] = graph.R,
        ["spacing_m"] = graph.Spacing,
        ["origin"] = new List<object?> { graph.Origin.East, graph.Origin.North },
        ["file"] = GraphFile,
    };

    private static List<object?> Nested(double[] flat, int cols)
    {
        var o = new List<object?>();
        for (int r = 0; r < flat.Length / cols; r++)
        {
            o.Add(flat.Skip(r * cols).Take(cols).Select(v => (object?)v).ToList());
        }
        return o;
    }

    private static (Array Data, long[] Shape) CastField(string name, Array value, int n, int cols)
    {
        FieldInfo info = Core.Fields.All[name];
        if (value.Length / cols != n || value.Length % cols != 0)
        {
            throw new ArgumentException($"필드 '{name}' 의 첫 차원이 칸 수 {n} 와 다릅니다");
        }
        Type dt = Core.Fields.ElementType(info.Dtype);
        bool dtInt = dt != typeof(double) && dt != typeof(float) && dt != typeof(bool);
        if (dtInt && value is double[] or float[])
        {
            foreach (double v in FieldSet.ToFloat64(value))
            {
                if (!double.IsFinite(v) || v != Math.Round(v))
                {
                    throw new ArgumentException($"필드 '{name}' 은 정수여야 하는데 정수가 아닌 값이 있습니다");
                }
            }
        }
        // TODO(port): numpy 의 정수 범위 검사(np.iinfo)는 옮기지 않았습니다.
        Array cast = FieldSet.CastTo(value, dt);
        long[] shape = cols == 1 ? [n] : [n, cols];
        return (cast, shape);
    }

    /// <summary>
    /// 묶음 하나를 path 폴더에 씁니다 (write_bundle). 반환: 쓴 manifest.
    /// </summary>
    public static OrderedDictionary<string, object?> WriteBundle(
        string path, CellGraph graph, FieldSet fields, OrderedDictionary<string, object?>? meta = null, Config? cfg = null,
        List<long[]>? rivers = null, string? kind = null)
    {
        Core.Fields.CheckFields(fields.Names);
        Directory.CreateDirectory(path);
        int n = graph.NCells;
        var entries = new List<object?>();
        foreach (string name in fields.Names.OrderBy(k => k, PyJson.CodePointComparer.Instance))
        {
            int cols = fields.Columns.TryGetValue(name, out int c) ? c : 1;
            (Array arr, long[] shape) = CastField(name, fields[name], n, cols);
            FieldInfo info = Core.Fields.All[name];
            string rel = $"{info.Group}/{name}.npy";
            Directory.CreateDirectory(Path.Combine(path, info.Group));
            Npy.WriteFile(Path.Combine(path, rel), arr, shape);
            entries.Add(new OrderedDictionary<string, object?>
            {
                ["name"] = name,
                ["file"] = rel,
                ["dtype"] = DtypeName(arr.GetType().GetElementType()!),
                ["unit"] = info.Unit,
                ["group"] = info.Group,
                ["shape"] = shape.Select(v => (object?)v).ToList(),
                ["description"] = info.Description,
            });
        }
        Npz.WriteFile(Path.Combine(path, GraphFile),
        [
            new NpzEntry("pos", graph.Pos, [n, 3]),
            new NpzEntry("nbr", graph.Nbr, [n, CellGraph.NSlots]),
            new NpzEntry("dist", graph.Dist, [n, CellGraph.NSlots]),
            new NpzEntry("area", graph.Area, [n]),
        ]);
        object? riversInfo = null;
        if (rivers is not null)
        {
            long[] cells = rivers.SelectMany(s => s).ToArray();
            if (cells.Any(cc => cc < 0 || cc >= n))
            {
                throw new ArgumentException("rivers 에 범위를 벗어난 칸 번호가 있습니다");
            }
            long[] offsets = new long[rivers.Count + 1];
            for (int i = 0; i < rivers.Count; i++)
            {
                offsets[i + 1] = offsets[i] + rivers[i].Length;
            }
            Npz.WriteFile(Path.Combine(path, RiversFile), [new NpzEntry("cells", cells), new NpzEntry("offsets", offsets)]);
            riversInfo = new OrderedDictionary<string, object?>
            {
                ["file"] = RiversFile,
                ["n_segments"] = (long)rivers.Count,
                ["n_cells"] = (long)cells.Length,
            };
        }
        object? faceBasis = graph.Kind == "sphere"
            ? new OrderedDictionary<string, object?>
            {
                ["u"] = Nested(Cubesphere.FaceU, 3),
                ["v"] = Nested(Cubesphere.FaceV, 3),
                ["n"] = Nested(Cubesphere.FaceN, 3),
            }
            : null;
        var manifest = new OrderedDictionary<string, object?>
        {
            ["format"] = Format,
            ["format_version"] = (long)FormatVersion,
            ["kind"] = kind,
            ["bpcg_version"] = Package.Version,
            ["git_commit"] = GitCommit(),
            ["config_digest"] = cfg?.Digest(),
            ["seed"] = cfg is null ? null : cfg.I("planet.seed"),
            ["profile"] = cfg?.Sec("profile").Get("name"),
            ["graph"] = GraphInfo(graph),
            ["face_basis"] = faceBasis,
            ["fields"] = entries,
            ["rivers"] = riversInfo,
            ["config"] = cfg?.AsDict(),
            ["meta"] = Jsonable(meta ?? []),
        };
        WriteJson(Path.Combine(path, ManifestFile), manifest);
        return manifest;
    }

    /// <summary>묶음 폴더의 manifest.json 을 읽습니다 (read_manifest).</summary>
    public static OrderedDictionary<string, object?> ReadManifest(string path)
    {
        string p = Path.Combine(path, ManifestFile);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException($"묶음 manifest 가 없습니다: {p}");
        }
        var man = (OrderedDictionary<string, object?>)PyJson.Loads(File.ReadAllText(p, System.Text.Encoding.UTF8))!;
        if (!Equals(man.GetValueOrDefault("format"), Format))
        {
            throw new ArgumentException($"{p} 는 bpcg 묶음이 아닙니다 (format = {man.GetValueOrDefault("format")})");
        }
        long ver = man.TryGetValue("format_version", out object? fv) && fv is not null
            ? Convert.ToInt64(fv, System.Globalization.CultureInfo.InvariantCulture)
            : -1;
        if (ver > FormatVersion)
        {
            throw new ArgumentException($"{p} 의 형식 버전 {ver} 을 읽을 수 없습니다");
        }
        return man;
    }

    private static double D(object? v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>write_bundle 로 쓴 묶음을 읽습니다 (read_bundle).</summary>
    public static Bundle ReadBundle(string path)
    {
        OrderedDictionary<string, object?> man = ReadManifest(path);
        var g = (OrderedDictionary<string, object?>)man["graph"]!;
        OrderedDictionary<string, NpyArray> npz = Npz.ReadFile(Path.Combine(path, (string)g["file"]!));
        var shapeList = (List<object?>)g["shape"]!;
        var origin = (List<object?>)g["origin"]!;
        var graph = new CellGraph(
            (string)g["kind"]!, shapeList.Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            npz["pos"].AsDouble(), npz["nbr"].AsInt(), npz["dist"].AsDouble(), npz["area"].AsDouble(), D(g["spacing_m"]),
            g["radius_m"] is null ? null : D(g["radius_m"]), (D(origin[0]), D(origin[1])));
        var fields = new FieldSet();
        foreach (object? eo in (List<object?>)man["fields"]!)
        {
            var e = (OrderedDictionary<string, object?>)eo!;
            NpyArray arr = Npy.ReadFile(Path.Combine(path, (string)e["file"]!));
            long[] shape = ((List<object?>)e["shape"]!).Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (!arr.Shape.SequenceEqual(shape) || DtypeName(arr.Data.GetType().GetElementType()!) != (string)e["dtype"]!)
            {
                throw new ArgumentException($"묶음 필드 '{e["name"]}' 의 모양·dtype 이 manifest 와 다릅니다");
            }
            string name = (string)e["name"]!;
            if (shape.Length == 2)
            {
                fields.Set2D(name, arr.Data, (int)shape[1]);
            }
            else
            {
                fields[name] = arr.Data;
            }
        }
        List<long[]>? rivers = null;
        if (man.TryGetValue("rivers", out object? rv) && rv is OrderedDictionary<string, object?> ri)
        {
            OrderedDictionary<string, NpyArray> rz = Npz.ReadFile(Path.Combine(path, (string)ri["file"]!));
            long[] cells = rz["cells"].AsLong();
            long[] offsets = rz["offsets"].AsLong();
            rivers = [];
            for (int i = 0; i + 1 < offsets.Length; i++)
            {
                rivers.Add(cells[(int)offsets[i]..(int)offsets[i + 1]]);
            }
        }
        var meta = man.TryGetValue("meta", out object? m) && m is OrderedDictionary<string, object?> md ? md : [];
        return new Bundle(graph, fields, man, meta, rivers);
    }

    /// <summary>묶음에 기록한 설정 내용으로 Config 를 다시 만듭니다 (config_from_manifest).</summary>
    public static Config ConfigFromManifest(OrderedDictionary<string, object?> manifest)
    {
        if (!manifest.TryGetValue("config", out object? data) || data is not OrderedDictionary<string, object?> d)
        {
            throw new ArgumentException("묶음 manifest 에 설정(config)이 없습니다");
        }
        return new Config(d);
    }

    private static FieldSet FloatFields(FieldSet fields)
    {
        var o = new FieldSet();
        foreach (KeyValuePair<string, Array> kv in fields)
        {
            Array v = kv.Value is float[] f ? Array.ConvertAll(f, x => (double)x) : kv.Value;
            if (fields.Columns.TryGetValue(kv.Key, out int c))
            {
                o.Set2D(kv.Key, v, c);
            }
            else
            {
                o[kv.Key] = v;
            }
        }
        return o;
    }

    private static FieldSet KnownFields(FieldSet fields)
    {
        var o = new FieldSet();
        foreach (KeyValuePair<string, Array> kv in fields)
        {
            if (!Core.Fields.All.ContainsKey(kv.Key))
            {
                continue;
            }
            if (fields.Columns.TryGetValue(kv.Key, out int c))
            {
                o.Set2D(kv.Key, kv.Value, c);
            }
            else
            {
                o[kv.Key] = kv.Value;
            }
        }
        return o;
    }

    /// <summary>PlanetState 를 묶음으로 씁니다 (save_planet_state).</summary>
    public static OrderedDictionary<string, object?> SavePlanetState(string path, PlanetState planet, Config cfg)
    {
        var meta = new OrderedDictionary<string, object?> { ["info"] = planet.Info, ["diag"] = planet.Diag };
        return WriteBundle(path, planet.Graph, KnownFields(planet.Fields), meta, cfg, kind: "planet");
    }

    private static LayerColumns ColumnsOf(FieldSet fields, string what)
    {
        foreach (string k in new[] { "strata_bottom_m", "strata_rock" })
        {
            if (!fields.Contains(k))
            {
                throw new ArgumentException($"{what} 묶음에 '{k}' 가 없습니다");
            }
        }
        int nl = fields.Columns.TryGetValue("strata_bottom_m", out int c) ? c : 1;
        return LayerColumns.FromColumns(fields.GetFloat64("strata_bottom_m"), (byte[])FieldSet.CastTo(fields["strata_rock"], typeof(byte)), nl);
    }

    /// <summary>묶음에서 PlanetState 를 다시 만듭니다 (load_planet_state, 실수 필드는 float64).</summary>
    public static PlanetState LoadPlanetState(string path)
    {
        Bundle b = ReadBundle(path);
        if (b.Graph.Kind != "sphere")
        {
            throw new ArgumentException($"{path} 는 행성(구면) 묶음이 아닙니다");
        }
        FieldSet fields = FloatFields(b.Fields);
        LayerColumns columns = ColumnsOf(fields, "행성");
        var info = b.Meta.TryGetValue("info", out object? i) && i is OrderedDictionary<string, object?> id ? id : [];
        var diag = b.Meta.TryGetValue("diag", out object? d) && d is OrderedDictionary<string, object?> dd ? dd : [];
        return new PlanetState(b.Graph, fields, columns, info, diag);
    }

    private static OrderedDictionary<string, object?>? SiteMeta(HeroSite? site)
    {
        if (site is null)
        {
            return null;
        }
        return new OrderedDictionary<string, object?>
        {
            ["center_unit"] = site.CenterUnit.Select(v => (object?)v).ToList(),
            ["east"] = site.East.Select(v => (object?)v).ToList(),
            ["north"] = site.North.Select(v => (object?)v).ToList(),
            ["l0_cell"] = site.L0Cell,
            ["score"] = site.Score,
            ["parts"] = new OrderedDictionary<string, object?>(site.Parts.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))),
            ["lat_deg"] = site.LatDeg,
            ["lon_deg"] = site.LonDeg,
        };
    }

    /// <summary>HeroState 를 묶음으로 씁니다 (save_hero_state).</summary>
    public static OrderedDictionary<string, object?> SaveHeroState(string path, HeroState hero, Config cfg)
    {
        var meta = new OrderedDictionary<string, object?>
        {
            ["site"] = SiteMeta(hero.Site),
            ["fan_apexes"] = hero.FanApexes,
            ["diag"] = hero.Diag,
        };
        return WriteBundle(path, hero.Graph, KnownFields(hero.Fields), meta, cfg, [.. hero.Rivers], "hero");
    }

    private static double[] Vec(object? v) => ((List<object?>)v!).Select(D).ToArray();

    /// <summary>묶음에서 HeroState 를 다시 만듭니다 (load_hero_state, 실수 필드는 float64).</summary>
    public static HeroState LoadHeroState(string path)
    {
        Bundle b = ReadBundle(path);
        if (b.Graph.Kind != "flat")
        {
            throw new ArgumentException($"{path} 는 히어로(평면) 묶음이 아닙니다");
        }
        FieldSet fields = FloatFields(b.Fields);
        LayerColumns columns = ColumnsOf(fields, "히어로");
        HeroSite? site = null;
        if (b.Meta.TryGetValue("site", out object? so) && so is OrderedDictionary<string, object?> s && s.Count > 0)
        {
            var parts = new OrderedDictionary<string, double>();
            if (s.GetValueOrDefault("parts") is OrderedDictionary<string, object?> pd)
            {
                foreach (KeyValuePair<string, object?> kv in pd)
                {
                    parts[kv.Key] = D(kv.Value);
                }
            }
            site = new HeroSite(
                Vec(s["center_unit"]), Vec(s["east"]), Vec(s["north"]),
                Convert.ToInt64(s["l0_cell"], System.Globalization.CultureInfo.InvariantCulture), D(s["score"]), parts,
                s.GetValueOrDefault("lat_deg") is null ? double.NaN : D(s["lat_deg"]),
                s.GetValueOrDefault("lon_deg") is null ? double.NaN : D(s["lon_deg"]));
        }
        var fans = b.Meta.GetValueOrDefault("fan_apexes") is List<object?> fl ? fl : [];
        var diag = b.Meta.GetValueOrDefault("diag") is OrderedDictionary<string, object?> dd ? dd : [];
        return new HeroState(b.Graph, fields, columns, site, b.Rivers ?? [], fans, diag);
    }
}
