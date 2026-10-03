using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 지구본 자료(&lt;굽기 폴더&gt;/globe/globe.json 과 .bin 파일)를 읽습니다. 굽기의 Bpcg.Bake.Globe 가 씁니다.
/// </summary>
/// <remarks>
/// 좌표 약속 (globe.json 의 frame): Godot 좌표 = (행성 x, 행성 z, -행성 y). 행성 자전축 z(북쪽)가 Godot +Y 입니다.
/// 반대로 행성 (x, y, z) = (gx, -gz, gy). 위도 = asin(gy), 경도 = atan2(-gz, gx).
/// 방향은 모두 단위 구 위의 단위 벡터이고, 반지름 1 이 해수면 반지름 radius_m 입니다.
/// 격자 약속 (globe.json 의 mapping, 등각 큐브스피어): 면 f 의 기저 n, u, v (Godot 좌표).
/// 방향 ∝ n + tan(a·π/4)·u + tan(b·π/4)·v, a, b ∈ [-1, 1].
/// 칸 (row r, col c) 중심: a = -1 + 2(c + 0.5)/N, b = -1 + 2(r + 0.5)/N. 열이 u, 행이 v 쪽입니다.
/// 모서리 (r, c): a = -1 + 2c/N, b = -1 + 2r/N. 모서리는 (N+1)² 개입니다.
/// 배열 순서는 [면, 행, 열] 이고 인덱스 = f·N² + r·N + c (모서리는 f·(N+1)² + r·(N+1) + c).
/// .bin 은 머리글 없는 리틀 엔디언 float32 또는 uint8 입니다. NaN 은 '값 없음' 입니다.
/// 필드 텍스처의 알파는 투명도가 아니라 텍셀의 '종류'입니다: TexelValue(255) = 값,
/// TexelBelowBreak(128) = color_break 아래, TexelNoData(0) = 값 없음.
/// </remarks>
public sealed class GlobeData
{
    public const string FormatName = "bpcg-globe";
    public const int FormatVersion = 1;

    /// <summary>굽기 폴더 안의 지구본 폴더 이름과 설명 파일 이름.</summary>
    public const string Subdir = "globe";
    public const string ManifestName = "globe.json";

    /// <summary>고도 필드 이름. 마우스 자리 읽기에서 늘 함께 보여 줍니다.</summary>
    public const string ElevationField = "elevation";

    /// <summary>값이 없는 칸(NaN)의 색과 글.</summary>
    public static readonly Color NoDataColor = Color.Color8(128, 128, 128);
    public const string NoDataText = "값 없음";
    public const int FaceCount = 6;
    public const double QuarterPi = Math.PI / 4.0;

    /// <summary>필드 텍스처 알파 = 텍셀의 종류 (셰이더 globe.gdshader 의 field_rgb 가 읽음).</summary>
    public const int TexelValue = 255;
    public const int TexelBelowBreak = 128;
    public const int TexelNoData = 0;

    /// <summary>색 지도에서 구간 시작 위치를 빨리 찾으려고 값 범위를 나누는 칸 수.</summary>
    private const int LutBins = 1024;

    /// <summary>읽은 폴더 (끝에 / 없음).</summary>
    public string Folder { get; private set; } = "";

    /// <summary>globe.json 내용 전체.</summary>
    public OrderedDictionary<string, object?> Meta { get; private set; } = new();

    /// <summary>실패 이유. 비어 있으면 읽기에 성공한 것입니다.</summary>
    public string ErrorMessage { get; private set; } = "";

    /// <summary>실패했을 때 자료가 아예 없었는지(globe.json 이 없음, true) 아니면 있는데 잘못됐는지(false).</summary>
    public bool Missing { get; private set; }

    /// <summary>면 한 변의 칸 수 N.</summary>
    public int FaceRes { get; private set; }

    /// <summary>해수면 반지름 (m). 지구본에서는 1 로 줄여 그립니다.</summary>
    public double RadiusM { get; private set; } = 6371000.0;

    /// <summary>해수면 고도 (m). 이보다 낮은 곳(바다)은 해수면 높이로 그립니다.</summary>
    public double SeaLevelM { get; private set; }

    /// <summary>면 기저 (Godot 좌표, 면 번호 순서).</summary>
    public Vector3[] FaceN { get; } = new Vector3[FaceCount];

    public Vector3[] FaceU { get; } = new Vector3[FaceCount];

    public Vector3[] FaceV { get; } = new Vector3[FaceCount];

    /// <summary>필드 설명 (globe.json 의 fields 순서).</summary>
    public List<OrderedDictionary<string, object?>> Fields { get; } = [];

    /// <summary>겹쳐 보기 설명 (강, 호수).</summary>
    public List<OrderedDictionary<string, object?>> Overlays { get; } = [];

    /// <summary>히어로 유역 (globe.json 의 hero). 없으면 빈 사전.</summary>
    public OrderedDictionary<string, object?> Hero { get; private set; } = new();

    /// <summary>값이 없는 칸의 색 (globe.json 의 nan_rgb, 없으면 NoDataColor).</summary>
    public Color NoDataColorValue { get; private set; } = NoDataColor;

    /// <summary>모서리 고도 (m), 6·(N+1)² 개. NaN 은 해수면으로 바꿔 둡니다.</summary>
    public float[] CornerElevationM { get; private set; } = [];

    public double CornerMinM { get; private set; }

    public double CornerMaxM { get; private set; }

    private readonly Dictionary<string, OrderedDictionary<string, object?>> _fieldByName = [];

    /// <summary>필드 이름 → float[] (float32) 또는 byte[] (uint8), 6·N² 개.</summary>
    private readonly Dictionary<string, Array> _values = [];

    /// <summary>겹쳐 보기 이름 → byte[], 6·N² 개.</summary>
    private readonly Dictionary<string, byte[]> _overlayValues = [];

    /// <summary>필드 이름 → 면 6장 텍스처. 처음 고를 때 만듭니다.</summary>
    private readonly Dictionary<string, ImageTexture[]> _textures = [];
    private ImageTexture[] _cornerTextures = [];
    private ImageTexture[] _overlayTextures = [];

    private sealed class Colormap
    {
        public double[] Values { get; init; } = [];

        public Color[] Colors { get; init; } = [];

        public int[] Starts { get; set; } = [];

        public double Lo { get; set; }

        public double InvWidth { get; set; }
    }

    private sealed record Category(string Label, Color Color, bool NoData);

    private readonly Dictionary<string, Colormap> _colormaps = [];
    private readonly Dictionary<string, Dictionary<int, Category>> _categories = [];
    private readonly Dictionary<string, int[]> _markerCells = [];

    /// <summary>
    /// 기본 지구본 폴더: &lt;BakedPaths.Dir()&gt;/globe. 그곳에 globe.json 이 없고 굽기 폴더가 실행 폴더의 회랑 폴더면
    /// (실행/corridor, 실행/globe) 실행 폴더의 globe 를 씁니다.
    /// </summary>
    public static string DefaultDir()
    {
        string dir = BakedPaths.Dir();
        string inside = dir.PathJoin(Subdir);
        if (Files.Exists(inside.PathJoin(ManifestName)))
        {
            return inside;
        }
        string sibling = dir.GetBaseDir().PathJoin(Subdir);
        if (!dir.StartsWith("res://", StringComparison.Ordinal) && Files.Exists(sibling.PathJoin(ManifestName)))
        {
            return sibling;
        }
        return inside;
    }

    /// <summary>지구본 자료를 읽습니다. 실패해도 null 이 아니라 ErrorMessage 가 채워진 객체를 돌려줍니다.</summary>
    public static GlobeData LoadDir(string dir = "")
    {
        var data = new GlobeData();
        data.Read(dir.Length == 0 ? DefaultDir() : dir.TrimEnd('/'));
        return data;
    }

    /// <summary>위도·경도 (도). X = 위도(북 +), Y = 경도(동 +, -180 ~ 180).</summary>
    public static Vector2 LatLon(Vector3 dir)
    {
        Vector3 d = dir.Normalized();
        return new Vector2(
            (float)Mathf.RadToDeg(Math.Asin(Math.Clamp(d.Y, -1.0, 1.0))),
            (float)Mathf.RadToDeg(Math.Atan2(-d.Z, d.X)));
    }

    /// <summary>위도·경도 (도) → Godot 좌표의 단위 방향.</summary>
    public static Vector3 DirectionFromLatLon(double latDeg, double lonDeg)
    {
        double lat = Mathf.DegToRad(latDeg);
        double lon = Mathf.DegToRad(lonDeg);
        // 행성 (cos·cos, cos·sin, sin) → Godot (px, pz, -py)
        return new Vector3(
            (float)(Math.Cos(lat) * Math.Cos(lon)), (float)Math.Sin(lat), (float)(-Math.Cos(lat) * Math.Sin(lon)));
    }

    /// <summary>"북위 37.52°, 동경 127.03°" 꼴의 글.</summary>
    public static string FormatLatLon(Vector2 ll, int digits = 2)
    {
        string fmt = "F" + digits.ToString(CultureInfo.InvariantCulture);
        string ns = ll.X >= 0.0f ? "북위 " : "남위 ";
        string ew = ll.Y >= 0.0f ? "동경 " : "서경 ";
        return ns + Math.Abs(ll.X).ToString(fmt, CultureInfo.InvariantCulture) + "°, "
            + ew + Math.Abs(ll.Y).ToString(fmt, CultureInfo.InvariantCulture) + "°";
    }

    /// <summary>사람이 읽기 좋은 수. 1000 이상은 세 자리마다 쉼표, 아주 작은 수는 a×10ⁿ.</summary>
    public static string FormatNumber(double v)
    {
        if (double.IsNaN(v))
        {
            return NoDataText;
        }
        if (double.IsInfinity(v))
        {
            return v > 0.0 ? "∞" : "-∞";
        }
        double a = Math.Abs(v);
        if (a >= 1.0e9)
        {
            return Scientific(v);
        }
        if (a >= 1000.0)
        {
            return GroupThousands((long)Math.Round(v, MidpointRounding.AwayFromZero));
        }
        if (a >= 100.0)
        {
            return v.ToString("F0", CultureInfo.InvariantCulture);
        }
        if (a >= 10.0)
        {
            return v.ToString("F1", CultureInfo.InvariantCulture);
        }
        if (a >= 1.0)
        {
            return v.ToString("F2", CultureInfo.InvariantCulture);
        }
        if (a == 0.0)
        {
            return "0";
        }
        if (a >= 0.01)
        {
            return v.ToString("F3", CultureInfo.InvariantCulture);
        }
        return Scientific(v);
    }

    public bool IsValid() => ErrorMessage.Length == 0;

    // ---------------------------------------------------------------- 필드

    public bool HasField(string name) => _fieldByName.ContainsKey(name);

    /// <summary>필드 설명 사전 (없으면 빈 사전).</summary>
    public OrderedDictionary<string, object?> Field(string name) =>
        _fieldByName.TryGetValue(name, out OrderedDictionary<string, object?>? f) ? f : new();

    /// <summary>globe.json 순서의 필드 이름.</summary>
    public List<string> FieldNames() => Fields.Select(f => Json.S(f["name"])).ToList();

    /// <summary>무리(group)별로 모은 필드 이름. 무리 순서는 globe.json 에 처음 나온 순서입니다.</summary>
    public List<string> OrderedFieldNames()
    {
        var groups = new List<string>();
        foreach (string name in FieldNames())
        {
            string g = GroupOf(name);
            if (!groups.Contains(g))
            {
                groups.Add(g);
            }
        }
        var names = new List<string>();
        foreach (string g in groups)
        {
            names.AddRange(FieldNames().Where(n => GroupOf(n) == g));
        }
        return names;
    }

    /// <summary>기본으로 보여 줄 필드: 고도, 없으면 첫 필드.</summary>
    public string DefaultField()
    {
        if (HasField(ElevationField))
        {
            return ElevationField;
        }
        List<string> names = OrderedFieldNames();
        return names.Count > 0 ? names[0] : "";
    }

    public string LabelOf(string name) => Json.Str(Field(name), "label", name);

    public string GroupOf(string name) => Json.Str(Field(name), "group", "기타");

    /// <summary>globe.json 에 적힌 단위 (로그 필드는 저장한 값의 단위, 예: "log10 m³/s").</summary>
    public string UnitOf(string name) => Json.Str(Field(name), "unit", "");

    /// <summary>보여 줄 때 쓰는 단위. 로그 필드는 10^값 의 단위 (예: "m³/s").</summary>
    public string DisplayUnitOf(string name)
    {
        string unit = UnitOf(name);
        if (IsLog(name))
        {
            if (unit.StartsWith("log10", StringComparison.Ordinal))
            {
                unit = unit["log10".Length..];
            }
            unit = unit.Trim();
            if (unit.StartsWith('(') && unit.EndsWith(')'))
            {
                unit = unit[1..^1];
            }
        }
        return unit;
    }

    public bool IsCategorical(string name) => Json.Str(Field(name), "kind", "continuous") == "categorical";

    /// <summary>저장한 값이 log10 인 필드인지.</summary>
    public bool IsLog(string name) => Json.Get(Field(name), "log") is true;

    /// <summary>연속 필드의 색 막대 범위 [min, max]. 없으면 색 지도 범위. 이 범위 밖의 값은 끝 색으로 칠합니다.</summary>
    public Vector2 ValueRange(string name)
    {
        OrderedDictionary<string, object?> f = Field(name);
        if (Json.Get(f, "min") is object lo && Json.Get(f, "max") is object hi)
        {
            return new Vector2(Json.F(lo), Json.F(hi));
        }
        double[] values = GetColormap(name).Values;
        if (values.Length == 0)
        {
            return Vector2.Zero;
        }
        return new Vector2((float)values[0], (float)values[^1]);
    }

    /// <summary>실제 자료의 범위 [data_min, data_max]. 없으면 NaN.</summary>
    public Vector2 DataRange(string name)
    {
        OrderedDictionary<string, object?> f = Field(name);
        object? lo = Json.Get(f, "data_min");
        object? hi = Json.Get(f, "data_max");
        return new Vector2(lo is null ? float.NaN : Json.F(lo), hi is null ? float.NaN : Json.F(hi));
    }

    /// <summary>값이 없는 칸의 글. 필드에 nan_label 이 있으면 덧붙입니다 (예: '값 없음 · 바다').</summary>
    public string NoDataTextOf(string name)
    {
        object? why = Json.Get(Field(name), "nan_label");
        if (why is null || Json.S(why).Length == 0 || Json.S(why) == NoDataText)
        {
            return NoDataText;
        }
        return NoDataText + " · " + Json.S(why);
    }

    /// <summary>범주가 덮은 표면 넓이의 몫 (0~1, globe.json 의 area_fraction). 없으면 NaN.</summary>
    public double AreaFraction(string name, int id)
    {
        foreach (object? cat in Categories(name))
        {
            if (Json.Obj(cat) is OrderedDictionary<string, object?> c && Json.Get(c, "id") is object cid && Json.I(cid) == id)
            {
                object? frac = Json.Get(c, "area_fraction");
                return frac is null ? double.NaN : Json.D(frac);
            }
        }
        return double.NaN;
    }

    /// <summary>범주 넓이 몫의 잣대를 설명하는 글 (globe.json 의 area_fraction_note, 없으면 빈 글).</summary>
    public string AreaFractionNote(string name) => Json.Str(Field(name), "area_fraction_note", "");

    /// <summary>범주 필드의 범주 [{"id", "label", "rgb"}] (globe.json 순서).</summary>
    public List<object?> Categories(string name) => Json.Arr(Json.Get(Field(name), "categories")) ?? [];

    /// <summary>범주가 '값 없음'(categories 의 no_data: true, 예: 바다의 지질)인지.</summary>
    public bool IsNoDataCategory(string name, int id) =>
        CategoryTable(name).TryGetValue(id, out Category? c) && c.NoData;

    /// <summary>연속 필드의 색이 끊기는 값 (globe.json 의 color_break, 없으면 NaN).</summary>
    public double ColorBreak(string name)
    {
        object? v = Json.Get(Field(name), "color_break");
        return Json.IsNumber(v) ? Json.D(v) : double.NaN;
    }

    /// <summary>범주 필드의 드문 칸을 화면에 점으로도 찍는지 (globe.json 의 point_markers).</summary>
    public bool HasPointMarkers(string name) => IsCategorical(name) && Json.Get(Field(name), "point_markers") is true;

    /// <summary>점으로 찍을 칸 번호 (f·N² + r·N + c): 범주 0 도 '값 없음' 범주도 아닌 칸. 처음 부를 때 셉니다.</summary>
    public int[] MarkerCells(string name)
    {
        if (_markerCells.TryGetValue(name, out int[]? cached))
        {
            return cached;
        }
        var outCells = new List<int>();
        if (HasPointMarkers(name) && _values.GetValueOrDefault(name) is byte[] bytes)
        {
            bool[] wanted = new bool[256];
            foreach (object? cat in Categories(name))
            {
                if (Json.Obj(cat) is not OrderedDictionary<string, object?> c || !c.ContainsKey("id"))
                {
                    continue;
                }
                int id = Json.I(c["id"]);
                if (id <= 0 || id > 255 || IsNoDataCategory(name, id))
                {
                    continue;
                }
                wanted[id] = true;
            }
            for (int i = 0; i < bytes.Length; i++)
            {
                if (wanted[bytes[i]])
                {
                    outCells.Add(i);
                }
            }
        }
        int[] result = [.. outCells];
        _markerCells[name] = result;
        return result;
    }

    /// <summary>텍스처 알파(텍셀의 종류) 하나: 값 없음, color_break 아래, 그 밖. 범주 필드는 늘 TexelValue 입니다.</summary>
    public int TexelKind(string name, double value)
    {
        if (IsCategorical(name))
        {
            return TexelValue;
        }
        if (double.IsNaN(value))
        {
            return TexelNoData;
        }
        double brk = ColorBreak(name);
        if (!double.IsNaN(brk) && value < brk)
        {
            return TexelBelowBreak;
        }
        return TexelValue;
    }

    public string CategoryLabel(string name, int id) =>
        CategoryTable(name).TryGetValue(id, out Category? c) ? c.Label : $"번호 {id}";

    public Color CategoryColor(string name, int id) =>
        CategoryTable(name).TryGetValue(id, out Category? c) ? c.Color : NoDataColorValue;

    /// <summary>필드의 저장 값 배열 (float[] 또는 byte[], 6·N² 개).</summary>
    public Array? Values(string name) => _values.GetValueOrDefault(name);

    public int CellIndex(int face, int row, int col) => (((face * FaceRes) + row) * FaceRes) + col;

    /// <summary>칸 하나의 저장 값. 값이 없으면 NaN. 범주 필드는 번호를 double 로 돌려줍니다.</summary>
    public double RawValue(string name, int face, int row, int col)
    {
        Array? arr = _values.GetValueOrDefault(name);
        int i = CellIndex(face, row, col);
        return arr switch
        {
            float[] f => f[i],
            byte[] b => b[i],
            _ => double.NaN,
        };
    }

    /// <summary>방향이 가리키는 칸의 저장 값 (NaN = 값 없음).</summary>
    public double ValueAt(string name, Vector3 dir)
    {
        Cell cell = DirectionToCell(dir);
        return RawValue(name, cell.Face, cell.Row, cell.Col);
    }

    /// <summary>값 한 개를 단위와 함께 글로 (범주 필드는 범주 이름, NaN 은 '값 없음').</summary>
    public string FormatValue(string name, double value)
    {
        if (double.IsNaN(value))
        {
            return NoDataTextOf(name);
        }
        if (IsCategorical(name))
        {
            return CategoryLabel(name, (int)value);
        }
        string unit = DisplayUnitOf(name);
        string text = FormatNumber(IsLog(name) ? Math.Pow(10.0, value) : value);
        return unit.Length == 0 ? text : text + " " + unit;
    }

    // ---------------------------------------------------------------- 겹쳐 보기

    public bool HasOverlay(string name) => _overlayValues.ContainsKey(name);

    public OrderedDictionary<string, object?> Overlay(string name) =>
        Overlays.FirstOrDefault(o => Json.S(o["name"]) == name) ?? new();

    /// <summary>방향이 가리키는 칸의 겹쳐 보기 값 (강은 유량 등급 1~4, 호수는 1, 없으면 0).</summary>
    public int OverlayValueAt(string name, Vector3 dir)
    {
        if (!_overlayValues.TryGetValue(name, out byte[]? arr))
        {
            return 0;
        }
        Cell cell = DirectionToCell(dir);
        return arr[CellIndex(cell.Face, cell.Row, cell.Col)];
    }

    /// <summary>방향이 가리키는 칸에 겹쳐 보기(강, 호수)가 있는지.</summary>
    public bool OverlayAt(string name, Vector3 dir) => OverlayValueAt(name, dir) != 0;

    /// <summary>겹쳐 보기 값의 이름 (overlays[].categories, 없으면 '있음'/'없음').</summary>
    public string OverlayValueLabel(string name, int value)
    {
        foreach (object? cat in Json.Arr(Json.Get(Overlay(name), "categories")) ?? [])
        {
            if (Json.Obj(cat) is OrderedDictionary<string, object?> c && Json.Get(c, "id") is object id && Json.I(id) == value)
            {
                return Json.Str(c, "label", value.ToString(CultureInfo.InvariantCulture));
            }
        }
        return value != 0 ? "있음" : "없음";
    }

    /// <summary>겹쳐 보기 값의 색 (categories 의 rgb, 없으면 overlays[].rgb, 그것도 없으면 fallback).</summary>
    public Color OverlayValueColor(string name, int value, Color fallback)
    {
        OrderedDictionary<string, object?> o = Overlay(name);
        foreach (object? cat in Json.Arr(Json.Get(o, "categories")) ?? [])
        {
            if (Json.Obj(cat) is OrderedDictionary<string, object?> c && Json.Get(c, "id") is object id
                && Json.I(id) == value && Json.Arr(Json.Get(c, "rgb")) is List<object?> rgb)
            {
                return Rgb8(rgb);
            }
        }
        if (Json.Arr(Json.Get(o, "rgb")) is List<object?> orgb)
        {
            return Rgb8(orgb);
        }
        return fallback;
    }

    // ---------------------------------------------------------------- 격자

    /// <summary>칸 (면, 행, 열)과 면 좌표 (a, b).</summary>
    public readonly record struct Cell(int Face, int Row, int Col, double A, double B);

    /// <summary>방향 → 칸. 면은 n 과의 내적이 가장 큰 면입니다.</summary>
    public Cell DirectionToCell(Vector3 dir)
    {
        Vector3 d = dir.Normalized();
        int face = 0;
        double best = double.NegativeInfinity;
        for (int f = 0; f < FaceCount; f++)
        {
            double k = d.Dot(FaceN[f]);
            if (k > best)
            {
                best = k;
                face = f;
            }
        }
        double a = Math.Atan(d.Dot(FaceU[face]) / best) / QuarterPi;
        double b = Math.Atan(d.Dot(FaceV[face]) / best) / QuarterPi;
        int col = Math.Clamp((int)Math.Floor((a + 1.0) * 0.5 * FaceRes), 0, FaceRes - 1);
        int row = Math.Clamp((int)Math.Floor((b + 1.0) * 0.5 * FaceRes), 0, FaceRes - 1);
        return new Cell(face, row, col, a, b);
    }

    /// <summary>칸 중심의 단위 방향.</summary>
    public Vector3 CellDir(int face, int row, int col)
    {
        double a = -1.0 + (2.0 * (col + 0.5) / FaceRes);
        double b = -1.0 + (2.0 * (row + 0.5) / FaceRes);
        return FacePoint(face, a, b);
    }

    /// <summary>모서리의 단위 방향.</summary>
    public Vector3 CornerDir(int face, int row, int col)
    {
        double a = -1.0 + (2.0 * col / FaceRes);
        double b = -1.0 + (2.0 * row / FaceRes);
        return FacePoint(face, a, b);
    }

    /// <summary>면 좌표 (a, b) → 단위 방향 (등각 사상).</summary>
    public Vector3 FacePoint(int face, double a, double b) =>
        (FaceN[face] + ((float)TanQuarter(a) * FaceU[face]) + ((float)TanQuarter(b) * FaceV[face])).Normalized();

    /// <summary>모서리 고도를 쌍선형으로 보간한 고도 (m). 지구본 메시의 높이와 거의 같습니다.</summary>
    public double CornerElevationAt(Vector3 dir)
    {
        if (CornerElevationM.Length == 0)
        {
            return 0.0;
        }
        Cell cell = DirectionToCell(dir);
        int side = FaceRes + 1;
        double x = Math.Clamp((cell.A + 1.0) * 0.5 * FaceRes, 0.0, FaceRes);
        double y = Math.Clamp((cell.B + 1.0) * 0.5 * FaceRes, 0.0, FaceRes);
        int c0 = Math.Min((int)x, FaceRes - 1);
        int r0 = Math.Min((int)y, FaceRes - 1);
        double tx = x - c0;
        double ty = y - r0;
        int i = (cell.Face * side * side) + (r0 * side) + c0;
        double top = Mathf.Lerp(CornerElevationM[i], CornerElevationM[i + 1], tx);
        double bottom = Mathf.Lerp(CornerElevationM[i + side], CornerElevationM[i + side + 1], tx);
        return Mathf.Lerp(top, bottom, ty);
    }

    /// <summary>칸 모서리 넷 가운데 가장 높은 고도 (m). 칸 안 어디서든 메시가 이보다 높지 않습니다.</summary>
    public double CellTopElevation(int face, int row, int col)
    {
        if (CornerElevationM.Length == 0)
        {
            return SeaLevelM;
        }
        int side = FaceRes + 1;
        int i = (face * side * side) + (row * side) + col;
        return Math.Max(
            Math.Max(CornerElevationM[i], CornerElevationM[i + 1]),
            Math.Max(CornerElevationM[i + side], CornerElevationM[i + side + 1]));
    }

    /// <summary>히어로 유역의 단위 방향 (없으면 Vector3.Zero).</summary>
    public Vector3 HeroDirection()
    {
        if (Hero.Count == 0 || Json.Arr(Json.Get(Hero, "unit")) is not List<object?> u)
        {
            return Vector3.Zero;
        }
        return new Vector3(Json.F(u[0]), Json.F(u[1]), Json.F(u[2])).Normalized();
    }

    // ---------------------------------------------------------------- 텍스처

    /// <summary>값 하나의 색 (범주 필드는 범주 색, NaN 은 회색).</summary>
    public Color ColorOf(string name, double value)
    {
        if (double.IsNaN(value))
        {
            return NoDataColorValue;
        }
        if (IsCategorical(name))
        {
            return CategoryColor(name, (int)value);
        }
        Colormap cm = GetColormap(name);
        if (cm.Values.Length == 0)
        {
            return NoDataColorValue;
        }
        return Interpolate(cm.Values, cm.Colors, StartOf(cm, value), value);
    }

    /// <summary>필드의 면별 RGBA8 텍스처 6장 (N × N, 픽셀 (x, y) = 칸 (col, row)). 처음 부를 때 만듭니다.</summary>
    public ImageTexture[] FieldTextures(string name)
    {
        if (_textures.TryGetValue(name, out ImageTexture[]? cached))
        {
            return cached;
        }
        if (!_values.ContainsKey(name))
        {
            return [];
        }
        int nn = FaceRes * FaceRes;
        var outTex = new ImageTexture[FaceCount];
        for (int f = 0; f < FaceCount; f++)
        {
            byte[] bytes = IsCategorical(name) ? CategoricalBytes(name, f * nn, nn) : ContinuousBytes(name, f * nn, nn);
            Image image = Image.CreateFromData(FaceRes, FaceRes, false, Image.Format.Rgba8, bytes);
            outTex[f] = ImageTexture.CreateFromImage(image);
        }
        _textures[name] = outTex;
        return outTex;
    }

    /// <summary>면별 모서리 고도 텍스처 6장 (FORMAT_RF, (N+1) × (N+1), 단위 m).</summary>
    public ImageTexture[] CornerTextures()
    {
        if (_cornerTextures.Length > 0 || CornerElevationM.Length == 0)
        {
            return _cornerTextures;
        }
        int side = FaceRes + 1;
        int count = side * side;
        var outTex = new ImageTexture[FaceCount];
        for (int f = 0; f < FaceCount; f++)
        {
            byte[] bytes = Files.FloatBytes(CornerElevationM.AsSpan(f * count, count).ToArray());
            outTex[f] = ImageTexture.CreateFromImage(Image.CreateFromData(side, side, false, Image.Format.Rf, bytes));
        }
        _cornerTextures = outTex;
        return _cornerTextures;
    }

    /// <summary>
    /// 면별 겹쳐 보기 텍스처 6장 (RGBA8, N × N). R = 강 값(유량 등급 0~4), G = 호수 값(0, 1)을 바이트 그대로 넣습니다.
    /// 셰이더가 등급별 색과 '몇 등급부터 그릴지'를 정합니다.
    /// </summary>
    public ImageTexture[] OverlayTextures()
    {
        if (_overlayTextures.Length > 0)
        {
            return _overlayTextures;
        }
        int nn = FaceRes * FaceRes;
        byte[] rivers = _overlayValues.GetValueOrDefault("rivers", []);
        byte[] lakes = _overlayValues.GetValueOrDefault("lakes", []);
        var outTex = new ImageTexture[FaceCount];
        for (int f = 0; f < FaceCount; f++)
        {
            byte[] bytes = new byte[nn * 4];
            int b = f * nn;
            for (int i = 0; i < nn; i++)
            {
                int k = i * 4;
                bytes[k] = rivers.Length == 0 ? (byte)0 : rivers[b + i];
                bytes[k + 1] = lakes.Length == 0 ? (byte)0 : lakes[b + i];
                bytes[k + 3] = 255;
            }
            outTex[f] = ImageTexture.CreateFromImage(Image.CreateFromData(FaceRes, FaceRes, false, Image.Format.Rgba8, bytes));
        }
        _overlayTextures = outTex;
        return _overlayTextures;
    }

    /// <summary>연속 필드의 범례 막대 그림 (width × 1, 왼쪽 = min, 오른쪽 = max).</summary>
    public Image LegendImage(string name, int width = 256)
    {
        Image image = Image.CreateEmpty(width, 1, false, Image.Format.Rgba8);
        Vector2 r = ValueRange(name);
        for (int x = 0; x < width; x++)
        {
            double t = (x + 0.5) / width;
            image.SetPixel(x, 0, ColorOf(name, Mathf.Lerp((double)r.X, r.Y, t)));
        }
        return image;
    }

    // ---------------------------------------------------------------- 읽기

    private void Read(string dir)
    {
        Folder = dir;
        string jsonPath = dir.PathJoin(ManifestName);
        if (!Files.Exists(jsonPath))
        {
            ErrorMessage = $"지구본 설명 파일이 없습니다: {jsonPath}";
            Missing = true;
            return;
        }
        OrderedDictionary<string, object?>? parsed = Json.ReadObject(jsonPath);
        if (parsed is null)
        {
            ErrorMessage = $"지구본 설명 파일을 JSON 으로 읽지 못했습니다: {jsonPath}";
            return;
        }
        Meta = parsed;
        foreach (string key in new[] { "format", "format_version", "face_res", "faces", "fields", "corners" })
        {
            if (!Meta.ContainsKey(key))
            {
                ErrorMessage = $"{jsonPath} 에 '{key}' 가 없습니다";
                return;
            }
        }
        if (Json.S(Meta["format"]) != FormatName)
        {
            ErrorMessage = $"지구본 형식이 아닙니다: format = {Json.S(Meta["format"])} (기대 {FormatName})";
            return;
        }
        if (Json.I(Meta["format_version"]) > FormatVersion)
        {
            ErrorMessage = $"지구본 형식 버전 {Json.S(Meta["format_version"])} 을 읽을 수 없습니다 (읽을 수 있는 버전 {FormatVersion} 이하)";
            return;
        }
        FaceRes = Json.I(Meta["face_res"]);
        if (FaceRes < 2)
        {
            ErrorMessage = $"face_res = {FaceRes} 가 너무 작습니다";
            return;
        }
        if (Json.Get(Meta, "radius_m") is object rm)
        {
            RadiusM = Json.D(rm);
        }
        object? sea = Json.Get(Meta, "sea_level_m");
        SeaLevelM = sea is null ? 0.0 : Json.D(sea);
        if (Json.Arr(Json.Get(Meta, "nan_rgb")) is List<object?> nanRgb && nanRgb.Count >= 3)
        {
            NoDataColorValue = Rgb8(nanRgb);
        }
        if (!ReadFaces(Meta["faces"]) || !ReadCorners(Meta["corners"]))
        {
            return;
        }
        foreach (object? f in Json.Arr(Meta["fields"]) ?? [])
        {
            if (Json.Obj(f) is not OrderedDictionary<string, object?> fd || !ReadField(fd))
            {
                if (ErrorMessage.Length == 0)
                {
                    ErrorMessage = "fields 의 항목이 사전이 아닙니다";
                }
                return;
            }
        }
        if (Fields.Count == 0)
        {
            ErrorMessage = $"{jsonPath} 에 필드가 하나도 없습니다";
            return;
        }
        foreach (object? o in Json.Arr(Json.Get(Meta, "overlays")) ?? [])
        {
            if (Json.Obj(o) is not OrderedDictionary<string, object?> od || !ReadOverlay(od))
            {
                if (ErrorMessage.Length == 0)
                {
                    ErrorMessage = "overlays 의 항목이 사전이 아닙니다";
                }
                return;
            }
        }
        if (Json.Obj(Json.Get(Meta, "hero")) is OrderedDictionary<string, object?> h && h.ContainsKey("unit"))
        {
            Hero = h;
        }
    }

    private bool ReadFaces(object? faces)
    {
        if (Json.Arr(faces) is not List<object?> list || list.Count != FaceCount)
        {
            ErrorMessage = "faces 는 면 6개의 배열이어야 합니다";
            return false;
        }
        var seen = new HashSet<int>();
        for (int i = 0; i < FaceCount; i++)
        {
            if (Json.Obj(list[i]) is not OrderedDictionary<string, object?> entry)
            {
                ErrorMessage = $"faces[{i}] 가 사전이 아닙니다";
                return false;
            }
            int index = Json.Get(entry, "index") is object ix ? Json.I(ix) : i;
            if (index < 0 || index >= FaceCount || !seen.Add(index))
            {
                ErrorMessage = $"faces 의 index {index} 가 잘못됐습니다";
                return false;
            }
            foreach (string key in new[] { "n", "u", "v" })
            {
                if (Json.Arr(Json.Get(entry, key)) is not List<object?> vec || vec.Count != 3)
                {
                    ErrorMessage = $"faces[{i}].{key} 가 길이 3 인 배열이 아닙니다";
                    return false;
                }
            }
            FaceN[index] = Vec3(entry["n"]);
            FaceU[index] = Vec3(entry["u"]);
            FaceV[index] = Vec3(entry["v"]);
        }
        return true;
    }

    private bool ReadCorners(object? info)
    {
        if (Json.Obj(info) is not OrderedDictionary<string, object?> d || !d.ContainsKey("file"))
        {
            ErrorMessage = "corners 에 file 이 없습니다";
            return false;
        }
        int side = FaceRes + 1;
        if (!CheckShape(d, [FaceCount, side, side], "corners"))
        {
            return false;
        }
        byte[] bytes = ReadBin(Json.S(d["file"]), Json.Str(d, "dtype", "float32"), FaceCount * side * side);
        if (bytes.Length == 0)
        {
            return false;
        }
        CornerElevationM = Files.Floats(bytes);
        double lo = double.PositiveInfinity;
        double hi = double.NegativeInfinity;
        for (int i = 0; i < CornerElevationM.Length; i++)
        {
            float z = CornerElevationM[i];
            if (!float.IsFinite(z))
            {
                CornerElevationM[i] = (float)SeaLevelM;
                continue;
            }
            lo = Math.Min(lo, z);
            hi = Math.Max(hi, z);
        }
        if (lo > hi)
        {
            lo = SeaLevelM;
            hi = SeaLevelM;
        }
        CornerMinM = lo;
        CornerMaxM = hi;
        return true;
    }

    private bool ReadField(OrderedDictionary<string, object?> f)
    {
        foreach (string key in new[] { "name", "file", "dtype" })
        {
            if (!f.ContainsKey(key))
            {
                ErrorMessage = $"필드 항목에 '{key}' 가 없습니다: {Json.Str(f, "name", "?")}";
                return false;
            }
        }
        string name = Json.S(f["name"]);
        if (_fieldByName.ContainsKey(name))
        {
            ErrorMessage = $"필드 이름 '{name}' 가 두 번 나옵니다";
            return false;
        }
        string kind = Json.Str(f, "kind", "continuous");
        if (kind != "continuous" && kind != "categorical")
        {
            ErrorMessage = $"필드 '{name}' 의 kind '{kind}' 를 모릅니다";
            return false;
        }
        if (kind == "categorical" && Json.Arr(Json.Get(f, "categories")) is null)
        {
            ErrorMessage = $"범주 필드 '{name}' 에 categories 가 없습니다";
            return false;
        }
        if (kind == "continuous" && (Json.Arr(Json.Get(f, "colormap")) is not List<object?> cmap || cmap.Count < 1))
        {
            ErrorMessage = $"연속 필드 '{name}' 에 colormap 이 없습니다";
            return false;
        }
        if (!CheckShape(f, [FaceCount, FaceRes, FaceRes], $"필드 '{name}'"))
        {
            return false;
        }
        int count = FaceCount * FaceRes * FaceRes;
        string dtype = Json.S(f["dtype"]);
        byte[] bytes = ReadBin(Json.S(f["file"]), dtype, count);
        if (bytes.Length == 0)
        {
            return false;
        }
        _values[name] = dtype == "float32" ? Files.Floats(bytes) : bytes;
        _fieldByName[name] = f;
        Fields.Add(f);
        return true;
    }

    private bool ReadOverlay(OrderedDictionary<string, object?> o)
    {
        if (!o.ContainsKey("name") || !o.ContainsKey("file"))
        {
            ErrorMessage = "겹쳐 보기 항목에 name 이나 file 이 없습니다";
            return false;
        }
        string name = Json.S(o["name"]);
        if (!CheckShape(o, [FaceCount, FaceRes, FaceRes], $"겹쳐 보기 '{name}'"))
        {
            return false;
        }
        string dtype = Json.Str(o, "dtype", "uint8");
        if (dtype != "uint8")
        {
            ErrorMessage = $"겹쳐 보기 '{name}' 의 dtype 은 uint8 이어야 합니다 ({dtype})";
            return false;
        }
        byte[] bytes = ReadBin(Json.S(o["file"]), dtype, FaceCount * FaceRes * FaceRes);
        if (bytes.Length == 0)
        {
            return false;
        }
        _overlayValues[name] = bytes;
        Overlays.Add(o);
        return true;
    }

    private bool CheckShape(OrderedDictionary<string, object?> info, int[] expected, string what)
    {
        if (Json.Arr(Json.Get(info, "shape")) is not List<object?> shape)
        {
            return true;
        }
        int[] got = shape.Select(Json.I).ToArray();
        if (!got.SequenceEqual(expected))
        {
            ErrorMessage = $"{what} 의 shape [{string.Join(", ", got)}] 가 [{string.Join(", ", expected)}] 와 다릅니다";
            return false;
        }
        return true;
    }

    /// <summary>.bin 을 읽어 크기를 확인합니다. 실패하면 빈 배열과 ErrorMessage.</summary>
    private byte[] ReadBin(string file, string dtype, int count)
    {
        int itemSize;
        switch (dtype)
        {
            case "float32":
                itemSize = 4;
                break;
            case "uint8":
                itemSize = 1;
                break;
            default:
                ErrorMessage = $"{file} 의 dtype '{dtype}' 를 읽을 수 없습니다 (float32, uint8 만)";
                return [];
        }
        string path = Folder.PathJoin(file);
        if (!Files.Exists(path))
        {
            ErrorMessage = $"지구본 파일이 없습니다: {path}";
            return [];
        }
        byte[] bytes = Files.Bytes(path);
        if (bytes.Length != count * itemSize)
        {
            ErrorMessage = $"{path} 크기 {bytes.Length} 바이트가 기대한 {count * itemSize} 바이트({count} 개 × {itemSize})와 다릅니다";
            return [];
        }
        return bytes;
    }

    // ---------------------------------------------------------------- 색 계산

    private Colormap GetColormap(string name)
    {
        if (_colormaps.TryGetValue(name, out Colormap? cached))
        {
            return cached;
        }
        var values = new List<double>();
        var colors = new List<Color>();
        foreach (object? knot in Json.Arr(Json.Get(Field(name), "colormap")) ?? [])
        {
            if (Json.Arr(knot) is List<object?> k && k.Count == 2 && Json.Arr(k[1]) is List<object?> rgb && rgb.Count >= 3)
            {
                values.Add(Json.D(k[0]));
                colors.Add(Rgb8(rgb));
            }
        }
        var cm = new Colormap { Values = [.. values], Colors = [.. colors] };
        int n = cm.Values.Length;
        if (n >= 2 && cm.Values[n - 1] > cm.Values[0])
        {
            // 칸 b 가 시작하는 값 이하인 마지막 매듭 번호 (n - 2 를 넘지 않음)
            int[] starts = new int[LutBins];
            double width = (cm.Values[n - 1] - cm.Values[0]) / LutBins;
            int i = 0;
            for (int b = 0; b < LutBins; b++)
            {
                double at = cm.Values[0] + (b * width);
                while (i + 1 <= n - 2 && cm.Values[i + 1] <= at)
                {
                    i++;
                }
                starts[b] = i;
            }
            cm.Starts = starts;
            cm.Lo = cm.Values[0];
            cm.InvWidth = 1.0 / width;
        }
        _colormaps[name] = cm;
        return cm;
    }

    private static int StartOf(Colormap cm, double v)
    {
        if (cm.Starts.Length == 0)
        {
            return 0;
        }
        return cm.Starts[Math.Clamp(LutIndex(v, cm.Lo, cm.InvWidth), 0, LutBins - 1)];
    }

    /// <summary>GDScript int() 처럼 0 쪽으로 자릅니다. 범위를 넘으면 끝으로 둡니다.</summary>
    private static int LutIndex(double v, double lo, double invWidth)
    {
        double x = (v - lo) * invWidth;
        if (x >= int.MaxValue)
        {
            return int.MaxValue;
        }
        if (x <= int.MinValue)
        {
            return int.MinValue;
        }
        return (int)x;
    }

    /// <summary>
    /// 매듭 [values, colors] 사이를 선형 보간합니다. start 는 values[start] &lt;= v 인 매듭 번호입니다.
    /// 같은 값의 매듭이 둘 와도 멈추지 않고 그 값부터 뒤 매듭의 색을 씁니다.
    /// </summary>
    private static Color Interpolate(double[] values, Color[] colors, int start, double v)
    {
        int k = values.Length;
        if (k == 1 || v < values[0])
        {
            return colors[0];
        }
        if (v >= values[k - 1])
        {
            return colors[k - 1];
        }
        int i = start;
        while (i + 1 < k && values[i + 1] <= v)
        {
            i++;
        }
        double span = values[i + 1] - values[i];
        double t = span > 0.0 ? (v - values[i]) / span : 1.0;
        return colors[i].Lerp(colors[i + 1], (float)t);
    }

    private byte[] ContinuousBytes(string name, int b, int count)
    {
        Array arr = _values[name];
        Colormap cm = GetColormap(name);
        double brk = ColorBreak(name);
        bool hasBreak = !double.IsNaN(brk);
        byte[] bytes = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            double v = arr is float[] fa ? fa[b + i] : ((byte[])arr)[b + i];
            Color c = NoDataColorValue;
            int kind = TexelNoData;
            if (!double.IsNaN(v) && cm.Values.Length > 0)
            {
                int start = 0;
                if (cm.Starts.Length > 0)
                {
                    start = cm.Starts[Math.Clamp(LutIndex(v, cm.Lo, cm.InvWidth), 0, LutBins - 1)];
                }
                c = Interpolate(cm.Values, cm.Colors, start, v);
                kind = hasBreak && v < brk ? TexelBelowBreak : TexelValue;
            }
            int k = i * 4;
            bytes[k] = (byte)c.R8;
            bytes[k + 1] = (byte)c.G8;
            bytes[k + 2] = (byte)c.B8;
            bytes[k + 3] = (byte)kind;
        }
        return bytes;
    }

    private byte[] CategoricalBytes(string name, int b, int count)
    {
        Array arr = _values[name];
        // 번호 0~255 → RGBA. 표에 없는 번호는 회색.
        byte[] lut = new byte[256 * 4];
        for (int id = 0; id < 256; id++)
        {
            Color c = CategoryColor(name, id);
            lut[id * 4] = (byte)c.R8;
            lut[(id * 4) + 1] = (byte)c.G8;
            lut[(id * 4) + 2] = (byte)c.B8;
            lut[(id * 4) + 3] = 255;
        }
        Color grey = NoDataColorValue;
        byte[] bytes = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            double v = arr is float[] fa ? fa[b + i] : ((byte[])arr)[b + i];
            int k = i * 4;
            if (double.IsNaN(v) || v < 0.0 || v > 255.0)
            {
                bytes[k] = (byte)grey.R8;
                bytes[k + 1] = (byte)grey.G8;
                bytes[k + 2] = (byte)grey.B8;
                bytes[k + 3] = 255;
                continue;
            }
            int j = (int)v * 4;
            bytes[k] = lut[j];
            bytes[k + 1] = lut[j + 1];
            bytes[k + 2] = lut[j + 2];
            bytes[k + 3] = 255;
        }
        return bytes;
    }

    private Dictionary<int, Category> CategoryTable(string name)
    {
        if (_categories.TryGetValue(name, out Dictionary<int, Category>? cached))
        {
            return cached;
        }
        var table = new Dictionary<int, Category>();
        foreach (object? cat in Categories(name))
        {
            if (Json.Obj(cat) is not OrderedDictionary<string, object?> c || !c.ContainsKey("id"))
            {
                continue;
            }
            // rgb 가 없거나 null 이거나 짧으면 '값 없음' 색으로 칠합니다.
            Color color = NoDataColorValue;
            if (Json.Arr(Json.Get(c, "rgb")) is List<object?> rgb && rgb.Count >= 3)
            {
                color = Rgb8(rgb);
            }
            int id = Json.I(c["id"]);
            table[id] = new Category(Json.Str(c, "label", $"번호 {id}"), color, Json.Get(c, "no_data") is true);
        }
        _categories[name] = table;
        return table;
    }

    /// <summary>면 경계(a = ±1)는 정확히 ±1 로 둡니다. 이웃 면과 꼭짓점이 비트 단위로 같아야 틈이 없습니다.</summary>
    public static double TanQuarter(double a)
    {
        if (a >= 1.0)
        {
            return 1.0;
        }
        if (a <= -1.0)
        {
            return -1.0;
        }
        return Math.Tan(a * QuarterPi);
    }

    private static Color Rgb8(List<object?> rgb) =>
        Color.Color8((byte)Json.I(rgb[0]), (byte)Json.I(rgb[1]), (byte)Json.I(rgb[2]));

    /// <summary>+ 0.0 은 -0.0 을 0.0 으로 바꿉니다 (면 경계 꼭짓점이 이웃 면과 비트 단위로 같도록).</summary>
    private static Vector3 Vec3(object? v)
    {
        List<object?> a = Json.Arr(v)!;
        return new Vector3(Json.F(a[0]) + 0.0f, Json.F(a[1]) + 0.0f, Json.F(a[2]) + 0.0f);
    }

    private static string GroupThousands(long n)
    {
        string digits = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
        string outText = "";
        while (digits.Length > 3)
        {
            outText = "," + digits[^3..] + outText;
            digits = digits[..^3];
        }
        return (n < 0 ? "-" : "") + digits + outText;
    }

    private static string Scientific(double v)
    {
        int exponent = (int)Math.Floor(Math.Log(Math.Abs(v)) / Math.Log(10.0));
        double mantissa = v / Math.Pow(10.0, exponent);
        if (Math.Abs(mantissa) >= 9.995)
        {
            mantissa /= 10.0;
            exponent++;
        }
        return mantissa.ToString("F2", CultureInfo.InvariantCulture) + "×10" + Superscript(exponent);
    }

    private static string Superscript(int n)
    {
        string[] digitsSup = ["⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹"];
        string outText = n < 0 ? "⁻" : "";
        foreach (char ch in Math.Abs(n).ToString(CultureInfo.InvariantCulture))
        {
            outText += digitsSup[ch - '0'];
        }
        return outText;
    }
}
