using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bpcg.Core;
using Bpcg.IO;

namespace Bpcg.Compare;

/// <summary>설정의 방법 인스턴스: 같은 방법을 매개변수만 바꿔 여러 개 둘 수 있습니다.</summary>
public sealed record MethodInstance(string Id, string Method, OrderedDictionary<string, object?> Params)
{
    public (CompareMethod Method, ParamValues Values) Resolve()
    {
        CompareMethod m = MethodRegistry.Get(Method);
        return (m, m.Resolve(Params, $"methods.{Id}.params"));
    }
}

/// <summary>비교 설정 (configs/compare/*.toml).</summary>
public sealed class CompareConfig
{
    public required string Name { get; init; }

    public required CompareGrid Grid { get; set; }

    public required List<long> Seeds { get; init; }

    public required List<MethodInstance> Instances { get; init; }

    public bool Snapshots { get; set; } = true;

    public string Source { get; init; } = "";

    public OrderedDictionary<string, object?> AsDict() => new()
    {
        ["name"] = Name,
        ["grid"] = Grid.AsDict(),
        ["seeds"] = Seeds.Cast<object?>().ToList(),
        ["snapshots"] = Snapshots,
        ["methods"] = Instances.Select(i =>
        {
            (CompareMethod m, ParamValues p) = i.Resolve();
            return (object?)new OrderedDictionary<string, object?> { ["id"] = i.Id, ["method"] = m.Name, ["params"] = p.AsDict() };
        }).ToList(),
        ["source"] = Source,
    };
}

/// <summary>
/// 비교 실행: 설정 TOML → 시드별 폴더에 방법마다 고도·지표를 저장하고, 시드끼리 묶어 읽습니다 (docs/compare.md 3장).
/// </summary>
/// <remarks>
/// 폴더: out/compare/&lt;이름&gt;/compare.json(설정·지표 설명), seed_NNNN/&lt;id&gt;/z.npy (n, n) float32 [m], meta.json, metrics.json,
/// layers/*.npy, stages/NN.npy + stages.json(연산 과정), trace.json(곡선·막대), *.png(그림, 필요할 때 한 장씩).
/// 시드가 바깥 반복이라 중간에 멈춰도 끝난 시드는 모든 방법이 들어 있습니다. 매개변수·격자·시드·기록 여부·코드가 같으면 고도를
/// 다시 만들지 않습니다. 코드 판은 Bpcg 어셈블리의 모듈 ID 입니다(결정적 빌드라 소스가 같으면 같음). 시간(seconds)은 생성 함수
/// 시간에서 과정 기록만을 위한 시간을 뺀 값이고, 프로세스에서 방법을 처음 쓸 때는 32×32 로 한 번 미리 돌려 첫 실행 비용
/// (JIT)이 섞이지 않게 합니다.
/// </remarks>
public static partial class CompareRunner
{
    public const int WarmupN = 32;
    private static readonly HashSet<string> Warmed = [];
    private static readonly PyJson.Options JsonOut = new(Indent: 1, EnsureAscii: false, AllowNan: false);

    [GeneratedRegex("^[a-z0-9][a-z0-9_\\-]{0,47}$")]
    private static partial Regex IdPattern();

    public static string CompareOut => Path.Combine(Paths.Out, "compare");

    /// <summary>코드 판 (캐시 키): Bpcg 어셈블리의 모듈 ID.</summary>
    public static string CodeVersion => typeof(CompareRunner).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    // ------------------------------------------------------------ 설정
    public static CompareConfig LoadConfigFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"비교 설정 파일이 없습니다: {path}");
        }
        OrderedDictionary<string, object?> data = Toml.Load(File.ReadAllBytes(path));
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Paths.Root) + Path.DirectorySeparatorChar;
        string source = full.StartsWith(root, StringComparison.Ordinal) ? Path.GetRelativePath(Paths.Root, full).Replace('\\', '/') : full;
        return ConfigFromDict(data, Path.GetFileNameWithoutExtension(path), source);
    }

    /// <summary>TOML 을 읽은 dict → 비교 설정. 모르는 키·방법·매개변수는 ArgumentException.</summary>
    public static CompareConfig ConfigFromDict(OrderedDictionary<string, object?> data, string name, string source = "")
    {
        foreach (string key in data.Keys)
        {
            if (key is not ("grid" or "run" or "methods"))
            {
                throw new ArgumentException($"비교 설정에 모르는 절이 있습니다: {key}");
            }
        }
        CompareGrid grid = CompareGrid.FromDict(data.GetValueOrDefault("grid") as OrderedDictionary<string, object?>);
        var run = data.GetValueOrDefault("run") as OrderedDictionary<string, object?> ?? [];
        foreach (string key in run.Keys)
        {
            if (key is not ("seeds" or "snapshots"))
            {
                throw new ArgumentException($"[run] 에 모르는 키가 있습니다: {key}");
            }
        }
        List<long> seeds = SeedList(run.GetValueOrDefault("seeds") ?? new List<object?> { 0L }, "run.seeds");
        bool snapshots = run.GetValueOrDefault("snapshots") switch
        {
            null => true,
            bool b => b,
            object v => throw new ArgumentException($"run.snapshots 는 true/false 여야 합니다: {Param.Show(v)}"),
        };
        var instances = new List<MethodInstance>();
        var methods = data.GetValueOrDefault("methods") as List<object?> ?? [];
        for (int k = 0; k < methods.Count; k++)
        {
            string where = $"methods[{k}]";
            if (methods[k] is not OrderedDictionary<string, object?> item || !item.ContainsKey("id"))
            {
                throw new ArgumentException($"{where}: id 가 필요합니다");
            }
            foreach (string key in item.Keys)
            {
                if (key is not ("id" or "method" or "params"))
                {
                    throw new ArgumentException($"{where}: 모르는 키 {key}");
                }
            }
            string id = item["id"] as string ?? throw new ArgumentException($"{where}: id 는 글자여야 합니다");
            if (!IsValidId(id))
            {
                throw new ArgumentException($"{where}: id 는 소문자·숫자·_·- 로 48자 이하여야 합니다: '{id}'");
            }
            string method = item.GetValueOrDefault("method") as string ?? id;
            var prm = item.GetValueOrDefault("params") as OrderedDictionary<string, object?> ?? [];
            var inst = new MethodInstance(id, method, new OrderedDictionary<string, object?>(prm));
            inst.Resolve(); // 방법·매개변수 검사
            instances.Add(inst);
        }
        if (instances.Count == 0)
        {
            throw new ArgumentException("비교 설정에 [[methods]] 가 하나도 없습니다");
        }
        if (instances.Select(i => i.Id).Distinct().Count() != instances.Count)
        {
            throw new ArgumentException($"methods 의 id 가 겹칩니다: {string.Join(", ", instances.Select(i => i.Id))}");
        }
        return new CompareConfig { Name = name, Grid = grid, Seeds = seeds, Instances = instances, Snapshots = snapshots, Source = source };
    }

    private static List<long> SeedList(object? v, string where)
    {
        if (v is not List<object?> list || list.Count == 0)
        {
            throw new ArgumentException($"{where} 는 정수 목록이어야 합니다");
        }
        var o = new List<long>();
        foreach (object? s in list)
        {
            if (s is not long l || l < 0 || l >= (1L << 31))
            {
                throw new ArgumentException($"{where}: 시드는 0 ~ 2^31−1 정수여야 합니다: {Param.Show(s)}");
            }
            o.Add(l);
        }
        if (o.Distinct().Count() != o.Count)
        {
            throw new ArgumentException($"{where}: 시드가 겹칩니다");
        }
        return o;
    }

    /// <summary>{"인스턴스.매개변수": 값}, {"grid.n": 값}, {"run.snapshots": 값} 을 얹습니다 (--param).</summary>
    public static void ApplyParamOverrides(CompareConfig cfg, IEnumerable<KeyValuePair<string, object?>> items)
    {
        OrderedDictionary<string, object?> grid = cfg.Grid.AsDict();
        foreach ((string key, object? value) in items)
        {
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0)
            {
                throw new ArgumentException($"--param 은 '인스턴스.매개변수=값' 꼴이어야 합니다: {key}");
            }
            string head = key[..dot];
            string tail = key[(dot + 1)..];
            if (head == "grid")
            {
                if (!grid.ContainsKey(tail))
                {
                    throw new ArgumentException($"grid 에 없는 키입니다: {tail}");
                }
                grid[tail] = value;
            }
            else if (key == "run.snapshots")
            {
                cfg.Snapshots = value as bool? ?? throw new ArgumentException($"run.snapshots 는 true/false 여야 합니다: {Param.Show(value)}");
            }
            else
            {
                MethodInstance inst = cfg.Instances.FirstOrDefault(i => i.Id == head)
                    ?? throw new ArgumentException($"설정에 없는 인스턴스입니다: {head} (있는 것: {string.Join(", ", cfg.Instances.Select(i => i.Id))})");
                inst.Params[tail] = value;
                inst.Resolve();
            }
        }
        cfg.Grid = CompareGrid.FromDict(grid);
    }

    // ------------------------------------------------------------ 예제 지형
    /// <summary>예제 지형: 이름(lope, rabi) 이나 .npz 경로(키 z, dx) → (고도 (ny·nx,) [m], ny, nx, 칸 간격 [m]).</summary>
    public static (double[] Z, int Ny, int Nx, double Dx) LoadExemplar(string name)
    {
        string path = name is "lope" or "rabi"
            ? Path.Combine(Paths.Root, "tests", "fixtures", $"{name}_tile512_30m.npz")
            : (Path.IsPathRooted(name) ? name : Path.Combine(Paths.Root, name));
        if (!File.Exists(path))
        {
            throw new ArgumentException($"예제 지형 파일이 없습니다: {path}");
        }
        OrderedDictionary<string, NpyArray> d = Npz.ReadFile(path, ["z", "dx"]);
        if (!d.TryGetValue("z", out NpyArray? za) || !d.TryGetValue("dx", out NpyArray? dxa))
        {
            throw new ArgumentException($"예제 .npz 에는 z 와 dx 가 있어야 합니다: {path}");
        }
        if (za.Shape.Length != 2)
        {
            throw new ArgumentException($"예제 고도는 2차원 배열이어야 합니다: {path}");
        }
        double[] z = za.Data switch
        {
            float[] f => Array.ConvertAll(f, v => (double)v),
            double[] x => x,
            _ => throw new ArgumentException($"예제 고도는 실수 배열이어야 합니다: {path}"),
        };
        if (z.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException($"예제 고도에 빈칸(NaN)이 있습니다: {path}");
        }
        return (z, (int)za.Shape[0], (int)za.Shape[1], dxa.ScalarDouble());
    }

    // ------------------------------------------------------------ 실행
    private static string Sha(object? obj) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(PyJson.DumpsSorted(obj))));

    public static string SeedDir(string setDir, long seed) => Path.Combine(setDir, $"seed_{seed:0000}");

    private static OrderedDictionary<string, object?>? ReadJson(string path)
    {
        try
        {
            return PyJson.Loads(File.ReadAllText(path, Encoding.UTF8)) as OrderedDictionary<string, object?>;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteJson(string path, object? obj)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, PyJson.Dumps(Clean(obj), JsonOut), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>JSON 으로 쓸 수 있게: NaN·inf 는 null, 배열은 목록으로.</summary>
    private static object? Clean(object? v) => v switch
    {
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? (double)f : null,
        int i => (long)i,
        OrderedDictionary<string, object?> d => new OrderedDictionary<string, object?>(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, Clean(kv.Value)))),
        System.Collections.IDictionary d => new OrderedDictionary<string, object?>(
            d.Keys.Cast<object>().Select(k => new KeyValuePair<string, object?>(k.ToString()!, Clean(d[k])))),
        string s => s,
        long[] la => la.Select(x => (object?)x).ToList(),
        double[] da => da.Select(x => Clean(x)).ToList(),
        System.Collections.IEnumerable e => e.Cast<object?>().Select(Clean).ToList(),
        _ => v,
    };

    private static void WarmUp(CompareMethod m, ParamValues p, CompareGrid grid)
    {
        if (Warmed.Contains(m.Name))
        {
            return;
        }
        var small = new CompareGrid(WarmupN, grid.Dx, grid.ReliefM);
        m.Generate(small, 0, p, new CompareContext { ExemplarLoader = LoadExemplar });
        Warmed.Add(m.Name);
    }

    /// <summary>과정 기록을 stages/ 와 trace.json 으로 씁니다. 반환: 단계 수.</summary>
    private static int SaveProcess(string dir, CompareContext ctx, double[] z, int n)
    {
        string stDir = Path.Combine(dir, "stages");
        if (Directory.Exists(stDir))
        {
            Directory.Delete(stDir, recursive: true);
        }
        File.Delete(Path.Combine(dir, "trace.json"));
        if (ctx.Stages.Count == 0 && ctx.Series.Count == 0 && ctx.Bars.Count == 0)
        {
            return 0;
        }
        Directory.CreateDirectory(stDir);
        (double zlo, double zhi) = CompareGrid.MinMax(z);
        var listing = new List<object?>();
        for (int i = 0; i < ctx.Stages.Count; i++)
        {
            StageSnapshot s = ctx.Stages[i];
            Npy.WriteFile(Path.Combine(stDir, $"{i:00}.npy"), s.Data, [n, n]);
            (double lo, double hi) = CompareGrid.MinMax(Array.ConvertAll(s.Data, v => (double)v));
            if (s.Kind == "height" && s.Scale == "final")
            {
                (lo, hi) = (zlo, zhi);
            }
            listing.Add(new OrderedDictionary<string, object?>
            {
                ["i"] = (long)i,
                ["label"] = s.Label,
                ["kind"] = s.Kind,
                ["kind_label"] = CompareContext.StageKinds[s.Kind],
                ["note"] = s.Note,
                ["unit"] = s.Unit,
                ["vmin"] = lo,
                ["vmax"] = hi,
                ["legend"] = CompareRender.Legend(s.Kind, lo, hi),
            });
        }
        WriteJson(Path.Combine(stDir, "stages.json"), listing);
        WriteJson(Path.Combine(dir, "trace.json"), new OrderedDictionary<string, object?> { ["series"] = ctx.Series, ["bars"] = ctx.Bars });
        return listing.Count;
    }

    /// <summary>인스턴스 하나 × 시드 하나. 반환: meta (지표 포함).</summary>
    public static OrderedDictionary<string, object?> RunOne(CompareConfig cfg, MethodInstance inst, long seed, string setDir, bool force, Action<string>? log)
    {
        (CompareMethod m, ParamValues p) = inst.Resolve();
        string dir = Path.Combine(SeedDir(setDir, seed), inst.Id);
        Directory.CreateDirectory(dir);
        string zKey = Sha(new OrderedDictionary<string, object?>
        {
            ["method"] = m.Name,
            ["params"] = p.AsDict(),
            ["grid"] = cfg.Grid.AsDict(),
            ["seed"] = seed,
            ["code"] = CodeVersion,
            ["snapshots"] = cfg.Snapshots,
        });
        string zPath = Path.Combine(dir, "z.npy");
        OrderedDictionary<string, object?>? meta = ReadJson(Path.Combine(dir, "meta.json"));
        double[] z;
        int n = cfg.Grid.N;
        if (!force && meta is not null && meta.GetValueOrDefault("z_key") as string == zKey && File.Exists(zPath))
        {
            z = Array.ConvertAll(Npy.ReadFile(zPath).AsFloat(), v => (double)v);
            log?.Invoke($"  [{inst.Id}] 저장해 둔 결과를 씁니다 (캐시)");
        }
        else
        {
            log?.Invoke($"  [{inst.Id}] {m.Label} 만드는 중");
            WarmUp(m, p, cfg.Grid);
            var ctx = new CompareContext { Log = log, ExemplarLoader = LoadExemplar, Recording = cfg.Snapshots };
            long t = Stopwatch.GetTimestamp();
            MethodOutput output = m.Generate(cfg.Grid, seed, p, ctx);
            double wall = Stopwatch.GetElapsedTime(t).TotalSeconds;
            double seconds = Math.Max(wall - ctx.ExtraSeconds, 0.0);
            z = output.Z;
            if (z.Length != n * n || z.Any(v => !double.IsFinite(v)))
            {
                throw new InvalidDataException($"[{inst.Id}] 결과 고도는 ({n}, {n}) 유한한 값이어야 합니다: 원소 {z.Length}");
            }
            float[] stored = Array.ConvertAll(z, v => (float)v);
            Npy.WriteFile(zPath, stored, [n, n]);
            z = Array.ConvertAll(stored, v => (double)v); // 지표는 저장한 float32 로 잽니다 (캐시로 읽을 때와 같게)
            string layDir = Path.Combine(dir, "layers");
            if (Directory.Exists(layDir))
            {
                Directory.Delete(layDir, recursive: true);
            }
            if (output.Layers.Count > 0)
            {
                Directory.CreateDirectory(layDir);
                foreach ((string name, double[] arr) in output.Layers)
                {
                    Npy.WriteFile(Path.Combine(layDir, name + ".npy"), Array.ConvertAll(arr, v => (float)v), [n, n]);
                }
            }
            foreach (string old in Directory.GetFiles(dir, "elevation_*.png"))
            {
                File.Delete(old);
            }
            int nStages = SaveProcess(dir, ctx, z, n);
            (double lo, double hi) = CompareGrid.MinMax(z);
            meta = new OrderedDictionary<string, object?>
            {
                ["id"] = inst.Id,
                ["method"] = m.Name,
                ["label"] = m.Label,
                ["family"] = m.Family,
                ["ref"] = m.Ref,
                ["params"] = p.AsDict(),
                ["seed"] = seed,
                ["grid"] = cfg.Grid.AsDict(),
                ["seconds"] = seconds,
                ["wall_seconds"] = wall,
                ["stages"] = (long)nStages,
                ["z_key"] = zKey,
                ["z_min"] = lo,
                ["z_max"] = hi,
                ["layers"] = output.Layers.Keys.Order(StringComparer.Ordinal).Cast<object?>().ToList(),
                ["info"] = output.Info,
                ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["version"] = Package.Version,
            };
            WriteJson(Path.Combine(dir, "meta.json"), meta);
            log?.Invoke($"  [{inst.Id}] 끝: {seconds:F2} s");
        }
        string mKey = Sha(new OrderedDictionary<string, object?> { ["z"] = zKey, ["metrics"] = CodeVersion });
        OrderedDictionary<string, object?>? mj = ReadJson(Path.Combine(dir, "metrics.json"));
        if (force || mj is null || mj.GetValueOrDefault("key") as string != mKey)
        {
            long t = Stopwatch.GetTimestamp();
            OrderedDictionary<string, object?> values = CompareMetrics.Compute(z, n, cfg.Grid.Dx);
            mj = new OrderedDictionary<string, object?> { ["key"] = mKey, ["values"] = values, ["seconds"] = Stopwatch.GetElapsedTime(t).TotalSeconds };
            WriteJson(Path.Combine(dir, "metrics.json"), mj);
        }
        var result = new OrderedDictionary<string, object?>(meta) { ["metrics"] = mj["values"] };
        return result;
    }

    /// <summary>비교를 돌려 setDir (기본 out/compare/&lt;이름&gt;) 에 씁니다. 반환: setDir.</summary>
    public static string Run(CompareConfig cfg, string? setDir = null, IReadOnlyList<long>? seeds = null, IReadOnlyList<string>? only = null, bool force = false, Action<string>? log = null)
    {
        setDir ??= Path.Combine(CompareOut, cfg.Name);
        Directory.CreateDirectory(setDir);
        List<long> runSeeds = seeds is { Count: > 0 } ? [.. seeds] : cfg.Seeds;
        List<MethodInstance> insts = cfg.Instances;
        if (only is { Count: > 0 })
        {
            string[] bad = only.Where(o => !insts.Any(i => i.Id == o)).ToArray();
            if (bad.Length > 0)
            {
                throw new ArgumentException($"설정에 없는 인스턴스입니다: {string.Join(", ", bad)} (있는 것: {string.Join(", ", insts.Select(i => i.Id))})");
            }
            insts = [.. insts.Where(i => only.Contains(i.Id))];
        }
        string recPath = Path.Combine(setDir, "compare.json");
        OrderedDictionary<string, object?> record = ReadJson(recPath) ?? [];
        if (record.TryGetValue("grid", out object? g) && g is OrderedDictionary<string, object?> og
            && PyJson.DumpsSorted(og) != PyJson.DumpsSorted(Clean(cfg.Grid.AsDict())))
        {
            throw new ArgumentException($"{setDir} 는 다른 격자 {PyJson.DumpsSorted(og)} 로 만든 비교입니다. --out 을 바꾸세요");
        }
        OrderedDictionary<string, object?> snapshot = cfg.AsDict();
        var methods = new OrderedDictionary<string, object?>();
        if (record.GetValueOrDefault("methods") is List<object?> oldList)
        {
            foreach (OrderedDictionary<string, object?> om in oldList.OfType<OrderedDictionary<string, object?>>())
            {
                methods[(string)om["id"]!] = om;
            }
        }
        foreach (OrderedDictionary<string, object?> nm in ((List<object?>)snapshot["methods"]!).Cast<OrderedDictionary<string, object?>>())
        {
            methods[(string)nm["id"]!] = nm;
        }
        snapshot["methods"] = methods.Values.ToList();
        var seen = new SortedSet<long>(runSeeds);
        if (record.GetValueOrDefault("seeds_run") is List<object?> oldSeeds)
        {
            foreach (object? s in oldSeeds)
            {
                seen.Add(Convert.ToInt64(s, CultureInfo.InvariantCulture));
            }
        }
        snapshot["seeds_run"] = seen.Cast<object?>().ToList();
        snapshot["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        snapshot["code"] = CodeVersion;
        snapshot["metric_defs"] = CompareMetrics.DescribeAll();
        snapshot["elevation_colors"] = CompareRender.TerrainStops.Cast<object?>().ToList();
        WriteJson(recPath, snapshot);
        long t0 = Stopwatch.GetTimestamp();
        int total = runSeeds.Count * insts.Count;
        int k = 0;
        foreach (long s in runSeeds)
        {
            log?.Invoke($"[비교] 시드 {s}: {cfg.Grid.N} × {cfg.Grid.N}칸, 칸 간격 {cfg.Grid.Dx:G} m");
            foreach (MethodInstance inst in insts)
            {
                k++;
                log?.Invoke($"[진행] {k}/{total}");
                RunOne(cfg, inst, s, setDir, force, log);
            }
        }
        log?.Invoke($"[비교] 끝: {Stopwatch.GetElapsedTime(t0).TotalSeconds:F1} s → {setDir}");
        return setDir;
    }

    // ------------------------------------------------------------ 읽기 (시드끼리 묶기)
    /// <summary>비교 묶음을 시드별로 묶어 읽습니다: {config, groups: [{seed, entries: [...]}], metrics}.</summary>
    public static OrderedDictionary<string, object?> LoadSet(string setDir)
    {
        OrderedDictionary<string, object?> rec = ReadJson(Path.Combine(setDir, "compare.json"))
            ?? throw new ArgumentException($"비교 묶음이 아닙니다 (compare.json 없음): {setDir}");
        List<string> order = (rec.GetValueOrDefault("methods") as List<object?> ?? [])
            .OfType<OrderedDictionary<string, object?>>().Select(m => (string)m["id"]!).ToList();
        var groups = new List<object?>();
        foreach (string sd in Directory.GetDirectories(setDir, "seed_*").Order(StringComparer.Ordinal))
        {
            if (!long.TryParse(Path.GetFileName(sd)[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed))
            {
                continue;
            }
            var entries = new List<OrderedDictionary<string, object?>>();
            foreach (string d in Directory.GetDirectories(sd))
            {
                OrderedDictionary<string, object?>? meta = ReadJson(Path.Combine(d, "meta.json"));
                if (meta is null || !File.Exists(Path.Combine(d, "z.npy")))
                {
                    continue;
                }
                meta["metrics"] = ReadJson(Path.Combine(d, "metrics.json"))?.GetValueOrDefault("values") ?? new OrderedDictionary<string, object?>();
                entries.Add(meta);
            }
            entries = [.. entries.OrderBy(e => order.IndexOf((string)e["id"]!) is int i && i >= 0 ? i : order.Count).ThenBy(e => (string)e["id"]!, StringComparer.Ordinal)];
            if (entries.Count > 0)
            {
                groups.Add(new OrderedDictionary<string, object?> { ["seed"] = seed, ["entries"] = entries.Cast<object?>().ToList() });
            }
        }
        return new OrderedDictionary<string, object?>
        {
            ["config"] = rec,
            ["groups"] = groups,
            ["metrics"] = rec.GetValueOrDefault("metric_defs") ?? CompareMetrics.DescribeAll(),
        };
    }

    /// <summary>결과 폴더 (없으면 ArgumentException).</summary>
    public static string EntryDir(string setDir, long seed, string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"인스턴스 이름이 올바르지 않습니다: '{id}'");
        }
        string d = Path.Combine(SeedDir(setDir, seed), id);
        return File.Exists(Path.Combine(d, "z.npy")) ? d : throw new ArgumentException($"결과가 없습니다: 시드 {seed}, {id}");
    }

    /// <summary>연산 과정: {stages, series, bars} (없으면 빈 목록).</summary>
    public static OrderedDictionary<string, object?> LoadProcess(string entryDir)
    {
        string listPath = Path.Combine(entryDir, "stages", "stages.json");
        object? stages = File.Exists(listPath) ? PyJson.Loads(File.ReadAllText(listPath, Encoding.UTF8)) : new List<object?>();
        OrderedDictionary<string, object?> tr = ReadJson(Path.Combine(entryDir, "trace.json")) ?? [];
        return new OrderedDictionary<string, object?>
        {
            ["stages"] = stages,
            ["series"] = tr.GetValueOrDefault("series") ?? new List<object?>(),
            ["bars"] = tr.GetValueOrDefault("bars") ?? new List<object?>(),
        };
    }

    /// <summary>JSON 글자 (화면·스튜디오용).</summary>
    public static string ToJson(object? obj) => PyJson.Dumps(Clean(obj), new PyJson.Options(EnsureAscii: false, AllowNan: false));
}
