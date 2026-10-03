using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine.Tests;

/// <summary>
/// 지구본 연기 검사(smoke test). 화면 없이(--headless) 지구본 장면을 열어 핵심 경로를 봅니다.
/// </summary>
/// <remarks>
/// 실행: <c>godot --headless --path . res://Tests/globe_smoke.tscn -- --baked-dir=&lt;폴더&gt; [--expect-globe]</c>.
/// 성공하면 'BPCG_GLOBE_OK' 를 찍고 Quit(0), 실패하면 'BPCG_GLOBE_FAIL: &lt;이유&gt;' 를 찍고 Quit(1) 합니다.
/// 자료가 없으면 알림 화면이 뜨고 멈추지 않는지만 보고 'BPCG_GLOBE_OK (no data)' 를 찍습니다.
/// 자료가 있으면 면 메시, 면 경계, 모든 필드 고르기(텍스처 색·알파, 점), 방향 ↔ 칸 왕복, .bin 값과 ValueAt,
/// 히어로 핀, 표시 설정(키 입력 포함), 카메라, 클릭 고정 상자를 봅니다.
/// </remarks>
public partial class GlobeSmokeTest : Node
{
    public const string GlobeScene = "res://scenes/globe.tscn";

    public static readonly Dictionary<string, string[]> ShaderUniforms = new()
    {
        ["res://shaders/globe.gdshader"] =
        [
            "corner_elevation", "field_color", "overlay", "categorical", "face_res", "radius_m",
            "sea_level_m", "exaggeration", "show_rivers", "show_lakes", "show_grid", "river_colors",
            "river_min_class", "lake_color", "shade_relief", "light_dir_view", "relief_shading",
            "relief_shading_limit",
        ],
        ["res://shaders/atmosphere.gdshader"] = ["glow_color", "strength", "falloff", "globe_radius", "glow_radius"],
    };

    public const string ExpectArg = "--expect-globe";
    public const int RoundTripSamples = 200;
    public const int ValueSamples = 300;

    /// <summary>위도·경도 허용 차 (도).</summary>
    public const double LatLonToleranceDeg = 0.01;

    /// <summary>화면 가운데를 골랐을 때 카메라 방향과의 허용 각 (rad).</summary>
    public const float PickToleranceRad = 0.01f;
    public const ulong WatchdogMs = 90000;

    private bool _done;
    private ulong _startedMs;
    private int _pixelsChecked;
    private int _markerFields;

    public override void _Ready()
    {
        _startedMs = Time.GetTicksMsec();
        GD.Print($"Godot {Godot.Engine.GetVersionInfo()["string"]} | 지구본 폴더: {GlobeData.DefaultDir()}");
        _ = RunGuarded();
    }

    public override void _Process(double delta)
    {
        if (!_done && Time.GetTicksMsec() - _startedMs > WatchdogMs)
        {
            Fail($"시간 제한 {WatchdogMs} ms 를 넘었습니다");
        }
    }

    private async Task RunGuarded()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Fail($"예외: {e}");
        }
    }

    private SignalAwaiter ProcessFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Run()
    {
        await ProcessFrame();
        if (!CheckShaders())
        {
            return;
        }
        if (GD.Load(GlobeScene) is not PackedScene packed || packed.Instantiate() is not GlobeMain main)
        {
            Fail($"지구본 장면을 읽지 못했습니다: {GlobeScene}");
            return;
        }
        GetTree().Root.AddChild(main);
        await ProcessFrame();
        await ProcessFrame();
        GlobeView? globe = main.GetNodeOrNull<GlobeView>("Globe");
        GlobeHud? hud = main.GetNodeOrNull<GlobeHud>("Hud");
        Camera3D? camera = main.GetNodeOrNull<Camera3D>("Camera");
        if (globe is null || hud is null || camera is not GlobeCamera globeCamera)
        {
            Fail("장면에 Globe(GlobeView), Hud(GlobeHud), Camera(GlobeCamera) 가 없습니다");
            return;
        }
        if (main.GetNodeOrNull("WorldEnvironment") is not WorldEnvironment)
        {
            Fail("WorldEnvironment 가 없습니다");
            return;
        }
        GlobeData? data = main.Data;
        if (data is null || !data.IsValid())
        {
            await CheckNoData(main, hud, data);
            return;
        }
        if (!CheckFaces(globe, data) || !CheckSeams(globe, data) || !CheckFields(globe, hud, data)
            || !CheckRoundTrip(data) || !CheckValues(data) || !CheckHero(globe, data))
        {
            return;
        }
        if (!await CheckDisplay(globe, hud, data) || !await CheckCamera(globe, globeCamera, data) || !CheckPinned(main, hud, data))
        {
            return;
        }
        Pass("");
    }

    private bool CheckShaders()
    {
        foreach ((string path, string[] uniforms) in ShaderUniforms)
        {
            if (GD.Load(path) is not Shader shader)
            {
                return Fail($"셰이더를 읽지 못했습니다: {path}");
            }
            var names = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
            foreach (string name in uniforms)
            {
                if (!names.Contains(name))
                {
                    return Fail($"셰이더 uniform '{name}' 가 없습니다 (컴파일 오류?): {path}");
                }
            }
        }
        return true;
    }

    /// <summary>자료가 없으면 알림이 보이고, 키를 눌러도 멈추지 않아야 합니다.</summary>
    private async Task CheckNoData(GlobeMain main, GlobeHud hud, GlobeData? data)
    {
        if (OS.GetCmdlineUserArgs().Contains(ExpectArg))
        {
            Fail($"지구본 자료를 기대했지만 읽지 못했습니다: {(data is null ? "자료 없음" : data.ErrorMessage)}");
            return;
        }
        bool missing = data is null || data.Missing;
        string headline = missing ? "지구본 자료가 없습니다" : "지구본 자료를 읽지 못했습니다";
        if (!hud.IsMissingVisible() || !hud.MissingTextShown().StartsWith(headline, StringComparison.Ordinal))
        {
            Fail($"자료 알림이 '{headline}' 로 시작하지 않습니다: '{hud.MissingTextShown()}'");
            return;
        }
        if (hud.IsPanelVisible())
        {
            Fail("자료가 없는데 레이어 패널이 보입니다");
            return;
        }
        foreach (Key key in new[] { Key.Key1, Key.R, Key.K, Key.L, Key.Bracketleft, Key.Bracketright, Key.Escape, Key.Space, Key.F, Key.H, Key.Tab })
        {
            Press(key);
        }
        await ProcessFrame();
        await ProcessFrame();
        if (!IsInstanceValid(main) || main.GetParent() != GetTree().Root)
        {
            Fail("자료가 없는 장면이 키 입력 뒤 사라졌습니다");
            return;
        }
        GD.Print($"자료 없음 알림: {hud.MissingTextShown().Split('\n')[0]}");
        Pass(" (no data)");
    }

    private bool CheckFaces(GlobeView globe, GlobeData data)
    {
        int n = data.FaceRes;
        if (globe.Faces.Count != GlobeData.FaceCount)
        {
            return Fail($"면 메시가 {globe.Faces.Count} 개입니다 (6 개여야 함)");
        }
        for (int f = 0; f < GlobeData.FaceCount; f++)
        {
            if (globe.GetNodeOrNull<MeshInstance3D>($"Face{f}") is not MeshInstance3D mi || mi != globe.Faces[f] || mi.Mesh is not ArrayMesh mesh)
            {
                return Fail($"Face{f} 메시(ArrayMesh)가 없습니다");
            }
            int vertexCount = mesh.SurfaceGetArrayLen(0);
            if (vertexCount != (n + 1) * (n + 1))
            {
                return Fail($"면 {f} 의 꼭짓점 {vertexCount} 개가 (N+1)² = {(n + 1) * (n + 1)} 와 다릅니다");
            }
            if (mesh.SurfaceGetArrayIndexLen(0) != n * n * 6)
            {
                return Fail($"면 {f} 의 인덱스 수가 6N² 가 아닙니다");
            }
            if (mi.MaterialOverride is not ShaderMaterial m || m.Shader?.ResourcePath != "res://shaders/globe.gdshader")
            {
                return Fail($"면 {f} 의 재질이 globe.gdshader 가 아닙니다");
            }
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            // Plane(a, b, c) 는 시계 방향 점 순서로 법선을 정합니다. 앞면이 바깥을 봐야 합니다.
            int nTris = indices.Length / 3;
            foreach (int t in new[] { 0, nTris >> 1, nTris - 1 })
            {
                Vector3 a = vertices[indices[t * 3]];
                Vector3 b = vertices[indices[(t * 3) + 1]];
                Vector3 c = vertices[indices[(t * 3) + 2]];
                if (new Plane(a, b, c).Normal.Dot((a + b + c) / 3.0f) <= 0.0f)
                {
                    return Fail($"면 {f} 의 삼각형 {t} 앞면이 안쪽을 봅니다");
                }
            }
            int half = vertexCount >> 1;
            foreach (int i in new[] { 0, n, vertexCount - 1, half })
            {
                if (Math.Abs(vertices[i].Length() - 1.0f) > 1e-5f)
                {
                    return Fail($"면 {f} 꼭짓점 {i} 이 단위 구 위에 있지 않습니다");
                }
            }
            // 꼭짓점 (r, c) 의 UV 는 (c/N, r/N), 방향은 CornerDir(f, r, c). 대각선 밖 꼭짓점으로 전치를 잡습니다.
            Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (uvs.Length != vertexCount)
            {
                return Fail($"면 {f} 의 UV 가 {uvs.Length} 개입니다 (꼭짓점 {vertexCount} 개)");
            }
            int side = n + 1;
            int[][] rcs = [[0, 0], [0, n], [n, 0], [n, n], [1, Math.Min(3, n)], [n / 2, n / 4], [n / 2, n / 2], [n - 1, 1]];
            foreach (int[] rc in rcs)
            {
                int r = rc[0];
                int c = rc[1];
                var wantUv = new Vector2((float)c / n, (float)r / n);
                if (!uvs[(r * side) + c].IsEqualApprox(wantUv))
                {
                    return Fail($"면 {f} 꼭짓점 (행 {r}, 열 {c}) 의 UV {uvs[(r * side) + c]} 가 (열/N, 행/N) = {wantUv} 가 아닙니다");
                }
                if (vertices[(r * side) + c].DistanceTo(data.CornerDir(f, r, c)) > 1e-5f)
                {
                    return Fail($"면 {f} 꼭짓점 (행 {r}, 열 {c}) 방향이 문서의 사상과 다릅니다");
                }
            }
        }
        if (globe.Atmosphere is null)
        {
            return Fail("대기 빛(Atmosphere) 이 없습니다");
        }
        GD.Print($"면 메시: 6 × ({n}+1)² 꼭짓점, 앞면 바깥, UV = (열/N, 행/N)");
        return true;
    }

    /// <summary>면 경계 꼭짓점이 다른 면의 꼭짓점과 비트 단위로 같은 자리이고, 모서리 고도도 같은지 봅니다.</summary>
    private bool CheckSeams(GlobeView globe, GlobeData data)
    {
        int n = data.FaceRes;
        int side = n + 1;
        var seen = new Dictionary<Vector3, (int Face, float Z)>();
        int shared = 0;
        float worstM = 0.0f;
        for (int f = 0; f < GlobeData.FaceCount; f++)
        {
            Vector3[] vertices = ((ArrayMesh)globe.Faces[f].Mesh).SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int r = 0; r < side; r++)
            {
                for (int c = 0; c < side; c++)
                {
                    if (r != 0 && r != n && c != 0 && c != n)
                    {
                        continue;
                    }
                    Vector3 v = vertices[(r * side) + c];
                    float z = data.CornerElevationM[(f * side * side) + (r * side) + c];
                    if (seen.TryGetValue(v, out (int Face, float Z) other))
                    {
                        if (other.Face != f)
                        {
                            shared++;
                            worstM = Math.Max(worstM, Math.Abs(z - other.Z));
                        }
                    }
                    else
                    {
                        seen[v] = (f, z);
                    }
                }
            }
        }
        // 면마다 둘레 4N 개, 모두 다른 면과 나눔. 큐브 꼭짓점 8 개는 세 면이 나눕니다.
        int boundary = 6 * 4 * n;
        int unique = seen.Count;
        if ((unique * 2) + 8 != boundary)
        {
            return Fail($"면 경계 꼭짓점이 이웃 면과 맞지 않습니다: 서로 다른 자리 {unique} 개 (기대 {(boundary - 8) / 2})");
        }
        if (worstM > 1e-3f)
        {
            return Fail($"이웃 면이 나누는 꼭짓점의 고도가 최대 {worstM:F3} m 다릅니다 (틈이 생김)");
        }
        GD.Print($"면 경계: 나눈 꼭짓점 {shared} 쌍, 고도 차 최대 {worstM:F4} m");
        return true;
    }

    private bool CheckFields(GlobeView globe, GlobeHud hud, GlobeData data)
    {
        if (globe.ActiveField != data.DefaultField())
        {
            return Fail($"처음 고른 필드가 {globe.ActiveField} 입니다 (기대 {data.DefaultField()})");
        }
        Texture2D? previous = null;
        List<string> names = data.FieldNames();
        foreach (string name in names)
        {
            if (!globe.SetField(name))
            {
                return Fail($"필드 '{name}' 를 고르지 못했습니다");
            }
            ImageTexture[] textures = data.FieldTextures(name);
            if (textures.Length != GlobeData.FaceCount)
            {
                return Fail($"필드 '{name}' 의 텍스처가 {textures.Length} 장입니다");
            }
            for (int f = 0; f < GlobeData.FaceCount; f++)
            {
                ShaderMaterial m = globe.Materials[f];
                if (m.GetShaderParameter("field_color").AsGodotObject() != textures[f])
                {
                    return Fail($"필드 '{name}' 를 골랐는데 면 {f} 재질 텍스처가 그대로입니다");
                }
                if (m.GetShaderParameter("categorical").AsBool() != data.IsCategorical(name))
                {
                    return Fail($"필드 '{name}' 의 categorical uniform 이 틀립니다");
                }
            }
            if (textures[0] == previous)
            {
                return Fail($"필드 '{name}' 의 텍스처가 앞 필드와 같습니다");
            }
            previous = textures[0];
            if (!hud.LegendTitle().StartsWith(data.LabelOf(name), StringComparison.Ordinal))
            {
                return Fail($"범례 제목 '{hud.LegendTitle()}' 이 필드 '{data.LabelOf(name)}' 와 다릅니다");
            }
            Button? button = hud.FieldButton(name);
            if (button is null || !button.ButtonPressed)
            {
                return Fail($"필드 '{name}' 버튼이 눌린 상태가 아닙니다");
            }
            if (data.IsCategorical(name))
            {
                if (hud.LegendChipCount() != data.Categories(name).Count)
                {
                    return Fail($"범주 필드 '{name}' 범례 칸 {hud.LegendChipCount()} 개가 범주 {data.Categories(name).Count} 개와 다릅니다");
                }
                if (hud.LegendAreaNote() != data.AreaFractionNote(name))
                {
                    return Fail($"범주 필드 '{name}' 범례의 넓이 몫 글 '{hud.LegendAreaNote()}' 가 globe.json 과 다릅니다");
                }
            }
            else
            {
                string[] ticks = hud.LegendTicks();
                if (!hud.LegendBarVisible() || ticks[0].Length == 0 || ticks[2].Length == 0)
                {
                    return Fail($"연속 필드 '{name}' 의 색 막대나 최소·최대 글이 없습니다");
                }
            }
            if (Json.Str(data.Field(name), "description", "").Length == 0)
            {
                return Fail($"필드 '{name}' 에 설명(description)이 없습니다");
            }
            if (!CheckTextureColors(data, name, textures) || !CheckMarkers(globe, data, name))
            {
                return false;
            }
        }
        globe.SetField(data.DefaultField());
        if (globe.MarkerCount != 0 || globe.PointMarkers is not null)
        {
            return Fail($"점을 찍지 않는 필드로 바꿨는데 점 {globe.MarkerCount} 개가 남았습니다");
        }
        GD.Print($"필드 {names.Count} 개 모두 고름 (텍스처 픽셀 {_pixelsChecked} 개 색 확인, 점 찍은 필드 {_markerFields} 개): "
            + string.Join(", ", names));
        return true;
    }

    /// <summary>point_markers 필드는 범주 0 도 '값 없음' 범주도 아닌 칸마다 점 하나 (그 밖의 필드는 점 없음).</summary>
    private bool CheckMarkers(GlobeView globe, GlobeData data, string name)
    {
        OrderedDictionary<string, object?> entry = data.Field(name);
        if (Json.Get(entry, "point_markers") is not true)
        {
            if (globe.MarkerCount != 0 || globe.PointMarkers is not null)
            {
                return Fail($"필드 '{name}' 는 점을 찍지 않는데 점이 {globe.MarkerCount} 개 있습니다");
            }
            return true;
        }
        var skip = new HashSet<int> { 0 };
        foreach (object? cat in Json.Arr(Json.Get(entry, "categories")) ?? [])
        {
            if (Json.Obj(cat) is OrderedDictionary<string, object?> c && Json.Get(c, "no_data") is true)
            {
                skip.Add(Json.I(c["id"]));
            }
        }
        byte[] bytes = Files.Bytes(data.Folder.PathJoin(Json.S(entry["file"])));
        int want = bytes.Count(b => !skip.Contains(b));
        if (globe.MarkerCount != want || (want > 0) != (globe.PointMarkers is not null))
        {
            return Fail($"필드 '{name}' 의 점 {globe.MarkerCount} 개가 0 이 아닌 칸 {want} 개와 다릅니다");
        }
        if (want > 0)
        {
            if (globe.PointMarkers!.Mesh is not ArrayMesh mesh || mesh.SurfaceGetPrimitiveType(0) != Mesh.PrimitiveType.Points
                || mesh.SurfaceGetArrayLen(0) != want)
            {
                return Fail($"필드 '{name}' 의 점 메시가 점 {want} 개가 아닙니다");
            }
            Vector3[] points = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            foreach (Vector3 p in new[] { points[0], points[^1] })
            {
                if (p.Length() < globe.SurfaceRadius(p.Normalized()))
                {
                    return Fail($"필드 '{name}' 의 점이 지표 아래에 있습니다: {p}");
                }
            }
            if (Json.Get(entry, "n_nonzero_cells") is object nz && Json.I(nz) != want)
            {
                return Fail($"globe.json 의 n_nonzero_cells {Json.S(nz)} 가 셈 {want} 과 다릅니다");
            }
        }
        _markerFields++;
        return true;
    }

    /// <summary>텍스처의 픽셀 색이 ColorOf(저장 값) 과 같은지 봅니다 (텍스처 그림을 읽을 수 있을 때만).</summary>
    private bool CheckTextureColors(GlobeData data, string name, ImageTexture[] textures)
    {
        var rng = new RandomNumberGenerator { Seed = 7 };
        for (int k = 0; k < 20; k++)
        {
            int f = rng.RandiRange(0, 5);
            Image? image = textures[f].GetImage();
            if (image is null || image.IsEmpty())
            {
                return true;
            }
            int r = rng.RandiRange(0, data.FaceRes - 1);
            int c = rng.RandiRange(0, data.FaceRes - 1);
            double value = data.RawValue(name, f, r, c);
            Color want = data.ColorOf(name, value);
            Color got = image.GetPixel(c, r);
            _pixelsChecked++;
            if (Math.Abs(got.R - want.R) + Math.Abs(got.G - want.G) + Math.Abs(got.B - want.B) > 3.5f / 255.0f)
            {
                return Fail($"필드 '{name}' 텍스처 (면 {f}, 행 {r}, 열 {c}) 색 {got} 가 {want} 와 다릅니다");
            }
            // 알파 = 텍셀의 종류. globe.json 의 color_break 로 따로 계산합니다.
            int kind = 255;
            if (!data.IsCategorical(name))
            {
                object? brk = Json.Get(data.Field(name), "color_break");
                if (double.IsNaN(value))
                {
                    kind = 0;
                }
                else if (Json.IsNumber(brk) && value < Json.D(brk))
                {
                    kind = 128;
                }
            }
            if (got.A8 != kind || data.TexelKind(name, value) != kind)
            {
                return Fail($"필드 '{name}' 텍스처 (면 {f}, 행 {r}, 열 {c}) 알파 {got.A8} 가 종류 {kind} 가 아닙니다");
            }
        }
        return true;
    }

    private bool CheckRoundTrip(GlobeData data)
    {
        var rng = new RandomNumberGenerator { Seed = 2026 };
        int n = data.FaceRes;
        // 칸 하나의 각 크기는 면 가운데에서 (π/2)/N 이고 어디서나 그 1.5 배를 넘지 않습니다.
        double maxAngle = 1.5 * Math.Sqrt(2.0) * (Math.PI / 2.0) / n;
        double worst = 0.0;
        for (int i = 0; i < RoundTripSamples; i++)
        {
            Vector3 d = new Vector3(rng.Randfn(), rng.Randfn(), rng.Randfn()).Normalized();
            GlobeData.Cell cell = data.DirectionToCell(d);
            Vector3 center = data.CellDir(cell.Face, cell.Row, cell.Col);
            GlobeData.Cell back = data.DirectionToCell(center);
            if (back.Face != cell.Face || back.Row != cell.Row || back.Col != cell.Col)
            {
                return Fail($"왕복 실패: 방향 {d} → 칸 {cell} → 중심 {center} → 칸 {back}");
            }
            double angle = d.AngleTo(center);
            worst = Math.Max(worst, angle);
            if (angle > maxAngle)
            {
                return Fail($"방향 {d} 와 그 칸 중심의 각 {angle:F5} rad 가 칸 크기 {maxAngle:F5} 보다 큽니다");
            }
        }
        for (int i = 0; i < RoundTripSamples; i++)
        {
            int f = rng.RandiRange(0, 5);
            int r = rng.RandiRange(0, n - 1);
            int c = rng.RandiRange(0, n - 1);
            GlobeData.Cell cell = data.DirectionToCell(data.CellDir(f, r, c));
            if (cell.Face != f || cell.Row != r || cell.Col != c)
            {
                return Fail($"칸 ({f}, {r}, {c}) 중심이 칸 {cell} 로 돌아왔습니다");
            }
        }
        GD.Print($"왕복: 방향 {RoundTripSamples} 개, 칸 {RoundTripSamples} 개, 중심까지 최대 {worst:F4} rad");
        return true;
    }

    /// <summary>globe.json 을 따로 읽고 역사상을 따로 짜서 .bin 의 값과 ValueAt 을 비교합니다.</summary>
    private bool CheckValues(GlobeData data)
    {
        OrderedDictionary<string, object?> meta = Json.ReadObject(data.Folder.PathJoin(GlobeData.ManifestName))!;
        int n = Json.I(meta["face_res"]);
        var normals = new Vector3[6];
        var us = new Vector3[6];
        var vs = new Vector3[6];
        foreach (object? faceObj in Json.Arr(meta["faces"])!)
        {
            OrderedDictionary<string, object?> face = Json.Obj(faceObj)!;
            int k = Json.I(face["index"]);
            normals[k] = V3(face["n"]);
            us[k] = V3(face["u"]);
            vs[k] = V3(face["v"]);
        }
        string categorical = "";
        string continuous = "";
        foreach (object? fo in Json.Arr(meta["fields"])!)
        {
            OrderedDictionary<string, object?> f = Json.Obj(fo)!;
            if (Json.S(f["kind"]) == "categorical" && categorical.Length == 0)
            {
                categorical = Json.S(f["name"]);
            }
            if (Json.S(f["kind"]) == "continuous" && Json.S(f["dtype"]) == "float32" && continuous.Length == 0)
            {
                continuous = Json.S(f["name"]);
            }
        }
        var rng = new RandomNumberGenerator { Seed = 99 };
        int checkedCount = 0;
        foreach (string name in new[] { categorical, continuous })
        {
            if (name.Length == 0)
            {
                continue;
            }
            OrderedDictionary<string, object?> entry = data.Field(name);
            byte[] bytes = Files.Bytes(data.Folder.PathJoin(Json.S(entry["file"])));
            float[]? floats = Json.S(entry["dtype"]) == "float32" ? Files.Floats(bytes) : null;
            for (int i = 0; i < ValueSamples; i++)
            {
                Vector3 d = new Vector3(rng.Randfn(), rng.Randfn(), rng.Randfn()).Normalized();
                int face = 0;
                for (int k = 0; k < 6; k++)
                {
                    if (d.Dot(normals[k]) > d.Dot(normals[face]))
                    {
                        face = k;
                    }
                }
                double dn = d.Dot(normals[face]);
                double a = 4.0 / Math.PI * Math.Atan(d.Dot(us[face]) / dn);
                double b = 4.0 / Math.PI * Math.Atan(d.Dot(vs[face]) / dn);
                int col = Math.Clamp((int)Math.Floor((a + 1.0) * n / 2.0), 0, n - 1);
                int row = Math.Clamp((int)Math.Floor((b + 1.0) * n / 2.0), 0, n - 1);
                int index = (((face * n) + row) * n) + col;
                double want = floats is null ? bytes[index] : floats[index];
                double got = data.ValueAt(name, d);
                bool same = (double.IsNaN(want) && double.IsNaN(got)) || want == got;
                if (!same)
                {
                    return Fail($"ValueAt('{name}', {d}) = {got} 가 .bin 의 칸 ({face}, {row}, {col}) 값 {want} 와 다릅니다");
                }
                checkedCount++;
            }
        }
        GD.Print($"ValueAt: {categorical}, {continuous} 의 {checkedCount} 칸이 .bin 과 같음");
        return true;
    }

    private bool CheckHero(GlobeView globe, GlobeData data)
    {
        if (data.Hero.Count == 0)
        {
            if (globe.HeroMarker is not null)
            {
                return Fail("히어로가 없는데 핀이 있습니다");
            }
            GD.Print("히어로: globe.json 에 없음");
            return true;
        }
        Vector3 dir = data.HeroDirection();
        Vector2 ll = GlobeData.LatLon(dir);
        var want = new Vector2(Json.F(data.Hero["lat_deg"]), Json.F(data.Hero["lon_deg"]));
        double dLat = Math.Abs(ll.X - want.X);
        double dLon = Math.Abs(Mathf.Wrap(ll.Y - want.Y, -180.0, 180.0)) * Math.Cos(Mathf.DegToRad(want.X));
        if (dLat > LatLonToleranceDeg || dLon > LatLonToleranceDeg)
        {
            return Fail($"히어로 위도·경도 {ll} 가 globe.json 의 {want} 와 다릅니다");
        }
        Vector3 back = GlobeData.DirectionFromLatLon(ll.X, ll.Y);
        if (back.DistanceTo(dir) > 1e-5f)
        {
            return Fail($"DirectionFromLatLon 이 LatLon 의 역이 아닙니다: {back} ≠ {dir}");
        }
        if (globe.HeroMarker is null || globe.HeroLabel is null)
        {
            return Fail("히어로 핀이나 이름표가 없습니다");
        }
        float radius = globe.HeroMarker.Position.Length();
        if (Math.Abs(radius - globe.SurfaceRadius(dir)) > 1e-4f)
        {
            return Fail($"히어로 핀이 지표에 있지 않습니다: 반지름 {radius}");
        }
        if (globe.HeroMarker.GlobalBasis.Y.Normalized().Dot(dir) < 0.9999f)
        {
            return Fail("히어로 핀이 지표에 수직이 아닙니다");
        }
        if (!globe.HeroLabel.Text.Contains("히어로 유역", StringComparison.Ordinal))
        {
            return Fail($"히어로 이름표 글이 '{globe.HeroLabel.Text}' 입니다");
        }
        GD.Print($"히어로: {GlobeData.FormatLatLon(ll)}, 이름표 '{globe.HeroLabel.Text}'");
        return true;
    }

    private async Task<bool> CheckDisplay(GlobeView globe, GlobeHud hud, GlobeData data)
    {
        ShaderMaterial m = globe.Materials[0];
        globe.SetExaggeration(50.0f);
        if (!Mathf.IsEqualApprox(m.GetShaderParameter("exaggeration").AsSingle(), 50.0f))
        {
            return Fail("고도 과장 50 이 셰이더에 들어가지 않았습니다");
        }
        globe.StepExaggeration(1);
        if (!Mathf.IsEqualApprox(globe.Exaggeration, 75.0f))
        {
            return Fail($"고도 과장 한 단계 위가 {globe.Exaggeration} 입니다 (기대 75)");
        }
        globe.SetExaggeration(1000.0f);
        if (globe.Exaggeration != GlobeView.MaxExaggeration)
        {
            return Fail($"고도 과장이 100 을 넘었습니다: {globe.Exaggeration}");
        }
        globe.SetExaggeration(0.0f);
        if (globe.Exaggeration != GlobeView.MinExaggeration)
        {
            return Fail($"고도 과장이 1 보다 작아졌습니다: {globe.Exaggeration}");
        }
        if (globe.HeroMarker is not null && Math.Abs(globe.HeroMarker.Position.Length() - globe.SurfaceRadius(data.HeroDirection())) > 1e-4f)
        {
            return Fail("고도 과장을 바꿨는데 히어로 핀이 따라오지 않았습니다");
        }
        globe.SetExaggeration(GlobeView.DefaultExaggeration);
        Press(Key.Bracketright);
        if (!Mathf.IsEqualApprox(m.GetShaderParameter("exaggeration").AsSingle(), 30.0f))
        {
            return Fail($"] 키로 고도 과장이 30 이 되지 않았습니다: {m.GetShaderParameter("exaggeration")}");
        }
        Press(Key.Bracketleft);
        if (!Mathf.IsEqualApprox(globe.Exaggeration, GlobeView.DefaultExaggeration))
        {
            return Fail("[ 키로 고도 과장이 돌아오지 않았습니다");
        }

        (string Uniform, Key Key, string Overlay)[] checks =
        [
            ("show_rivers", Key.R, GlobeView.OverlayRivers),
            ("show_lakes", Key.K, GlobeView.OverlayLakes),
            ("show_grid", Key.L, ""),
        ];
        foreach ((string uniform, Key key, string overlay) in checks)
        {
            if (overlay.Length > 0 && !data.HasOverlay(overlay))
            {
                continue;
            }
            bool before = m.GetShaderParameter(uniform).AsBool();
            Press(key);
            bool after = m.GetShaderParameter(uniform).AsBool();
            if (after == before)
            {
                return Fail($"키 {OS.GetKeycodeString(key)} 로 {uniform} 가 바뀌지 않았습니다");
            }
            BaseButton? button = overlay.Length == 0 ? hud.GridButton() : hud.OverlayButton(overlay);
            if (button is null || button.ButtonPressed != after)
            {
                return Fail($"{uniform} 버튼 상태가 uniform 과 다릅니다");
            }
            Press(key);
            if (m.GetShaderParameter(uniform).AsBool() != before)
            {
                return Fail($"키 {OS.GetKeycodeString(key)} 를 두 번 눌렀는데 {uniform} 가 돌아오지 않았습니다");
            }
        }
        if (data.HasOverlay(GlobeView.OverlayRivers))
        {
            int before = m.GetShaderParameter("river_min_class").AsInt32();
            Press(Key.R, true);
            int after = m.GetShaderParameter("river_min_class").AsInt32();
            if (after == before || after != globe.RiverMinClass)
            {
                return Fail($"Shift+R 로 강 등급 문턱이 바뀌지 않았습니다 ({before} → {after})");
            }
            if (!CheckRiverReadout(globe, hud, data))
            {
                return false;
            }
            globe.SetRiverMinClass(GlobeView.DefaultRiverMinClass);
        }

        // G: 그늘·대기 빛 (꾸밈) 켜기·끄기
        bool shadeBefore = m.GetShaderParameter("shade_relief").AsBool();
        Press(Key.G);
        bool shadeAfter = m.GetShaderParameter("shade_relief").AsBool();
        if (shadeAfter == shadeBefore || shadeAfter != globe.ShowShading)
        {
            return Fail("G 키로 그늘(shade_relief)이 바뀌지 않았습니다");
        }
        if (globe.Atmosphere!.Visible != shadeAfter)
        {
            return Fail("G 키로 그늘을 바꿨는데 대기 빛이 따라오지 않았습니다");
        }
        if (hud.ShadingButton() is null || hud.ShadingButton()!.ButtonPressed != shadeAfter)
        {
            return Fail("그늘 버튼 상태가 uniform 과 다릅니다");
        }
        Press(Key.G);
        if (m.GetShaderParameter("shade_relief").AsBool() != shadeBefore)
        {
            return Fail("G 키를 두 번 눌렀는데 그늘이 돌아오지 않았습니다");
        }

        // 숫자 키: 2 번째 필드, 패널 숨기기
        List<string> ordered = data.OrderedFieldNames();
        if (ordered.Count >= 2)
        {
            Press(Key.Key2);
            if (globe.ActiveField != ordered[1])
            {
                return Fail($"2 키로 '{ordered[1]}' 가 아니라 '{globe.ActiveField}' 를 골랐습니다");
            }
            Press(Key.Key1);
        }
        Press(Key.Tab);
        if (hud.IsPanelVisible())
        {
            return Fail("Tab 으로 패널이 숨지 않았습니다");
        }
        Press(Key.Tab);
        if (!hud.IsPanelVisible())
        {
            return Fail("Tab 을 두 번 눌렀는데 패널이 보이지 않습니다");
        }
        await ProcessFrame();
        GD.Print("표시: 고도 과장, 강·호수·경위선, 강 등급, 그늘, 숫자 키, 패널 숨기기 확인");
        return true;
    }

    /// <summary>문턱 아래 강 칸을 가리키면 마우스 자리 글에 '그리지 않음' 이 붙고, 문턱 위면 붙지 않습니다.</summary>
    private bool CheckRiverReadout(GlobeView globe, GlobeHud hud, GlobeData data)
    {
        byte[] rivers = Files.Bytes(data.Folder.PathJoin(Json.S(data.Overlay(GlobeView.OverlayRivers)["file"])));
        int low = 256;
        foreach (byte b in rivers)
        {
            if (b > 0 && b < low)
            {
                low = b;
            }
        }
        if (low == 256)
        {
            return true;
        }
        // 가장 낮은 등급 칸 하나: 문턱을 그 등급으로 두면 그리고, 그 위로 올리면 그리지 않음
        int index = Array.IndexOf(rivers, (byte)low);
        int nn = data.FaceRes * data.FaceRes;
        int face = index / nn;
        int row = (index - (face * nn)) / data.FaceRes;
        int col = index - (face * nn) - (row * data.FaceRes);
        Vector3 dir = data.CellDir(face, row, col);
        if (data.OverlayValueAt(GlobeView.OverlayRivers, dir) != low)
        {
            return Fail($"강 칸 ({face}, {row}, {col}) 의 중심이 그 칸으로 돌아오지 않습니다");
        }
        globe.SetOverlayVisible(GlobeView.OverlayRivers, true);
        globe.SetRiverMinClass(low);
        if (hud.DescribePoint(dir).Contains("그리지 않음", StringComparison.Ordinal))
        {
            return Fail($"문턱 {low} 등급인데 {low} 등급 강 칸을 '그리지 않음' 이라고 읽습니다");
        }
        if (low >= globe.MaxRiverClassOf())
        {
            GD.Print($"강 읽기: 가장 높은 등급 {low} 만 있어 문턱 위로 올려 볼 수 없습니다");
            return true;
        }
        globe.SetRiverMinClass(low + 1);
        string text = hud.DescribePoint(dir);
        if (!text.Contains("그리지 않음", StringComparison.Ordinal))
        {
            return Fail($"문턱 {low + 1} 등급 아래 강 칸을 그리는 것처럼 읽습니다:\n{text}");
        }
        GD.Print($"강 읽기: 문턱({low + 1} 등급) 아래 {low} 등급 칸은 '그리지 않음'");
        return true;
    }

    private async Task<bool> CheckCamera(GlobeView globe, GlobeCamera camera, GlobeData data)
    {
        await ProcessFrame();
        Vector2 center = new Vector2(GetTree().Root.Size.X, GetTree().Root.Size.Y) * 0.5f;
        Vector3 picked = globe.Pick(camera, center);
        Vector3 toward = camera.GlobalPosition.Normalized();
        if (picked == Vector3.Zero || picked.AngleTo(toward) > PickToleranceRad)
        {
            return Fail($"화면 가운데를 골랐는데 {picked} 입니다 (카메라 쪽 {toward})");
        }
        camera.ZoomBy(-100.0f);
        if (!Mathf.IsEqualApprox(camera.Distance, 1.15f))
        {
            return Fail($"가장 가까이 당겼는데 거리가 {camera.Distance} 입니다");
        }
        camera.ZoomBy(100.0f);
        if (!Mathf.IsEqualApprox(camera.Distance, 8.0f))
        {
            return Fail($"가장 멀리 밀었는데 거리가 {camera.Distance} 입니다");
        }
        Vector3 target = data.HeroDirection();
        if (target == Vector3.Zero)
        {
            target = GlobeData.DirectionFromLatLon(37.5, 127.0);
        }
        camera.FlyTo(target, 0.0f);
        await ProcessFrame();
        if (camera.GlobalPosition.Normalized().AngleTo(target) > 1e-3f)
        {
            return Fail($"FlyTo 뒤 카메라가 {target} 쪽에 있지 않습니다");
        }
        picked = globe.Pick(camera, center);
        if (picked.AngleTo(target) > PickToleranceRad)
        {
            return Fail($"FlyTo 뒤 화면 가운데가 {target} 가 아니라 {picked} 입니다");
        }
        Press(Key.Space);
        if (!camera.AutoRotate)
        {
            return Fail("Space 로 자동 회전이 켜지지 않았습니다");
        }
        Press(Key.Space);
        GD.Print("카메라: 가운데 고르기, 확대 한계 1.15 ~ 8, 날아가기, 자동 회전 확인");
        return true;
    }

    private bool CheckPinned(GlobeMain main, GlobeHud hud, GlobeData data)
    {
        Vector3 dir = data.HeroDirection();
        if (dir == Vector3.Zero)
        {
            dir = GlobeData.DirectionFromLatLon(10.0, 20.0);
        }
        main.Pin(dir);
        string text = hud.PinnedText();
        foreach (string name in data.FieldNames())
        {
            if (!text.Contains(data.LabelOf(name) + ": ", StringComparison.Ordinal))
            {
                return Fail($"고정 상자에 '{data.LabelOf(name)}' 가 없습니다:\n{text}");
            }
        }
        foreach (OrderedDictionary<string, object?> o in data.Overlays)
        {
            if (!text.Contains(Json.Str(o, "label", Json.S(o["name"])) + ": ", StringComparison.Ordinal))
            {
                return Fail($"고정 상자에 겹쳐 보기 '{Json.S(o["name"])}' 가 없습니다");
            }
        }
        string hover = hud.DescribePoint(dir);
        if (!hover.Contains("위도·경도", StringComparison.Ordinal) || !hover.Contains(data.LabelOf(data.DefaultField()), StringComparison.Ordinal))
        {
            return Fail($"마우스 자리 글에 위도·경도나 고도가 없습니다:\n{hover}");
        }
        Press(Key.Escape);
        if (hud.PinnedText().Length > 0)
        {
            return Fail("Esc 로 고정이 풀리지 않았습니다");
        }
        GD.Print($"고정 상자: 필드 {data.FieldNames().Count} 개와 겹쳐 보기 {data.Overlays.Count} 개를 보여 줌");
        return true;
    }

    private void Press(Key key, bool shift = false)
    {
        var ev = new InputEventKey { PhysicalKeycode = key, Keycode = key, ShiftPressed = shift, Pressed = true };
        GetTree().Root.PushInput(ev);
        var release = (InputEventKey)ev.Duplicate();
        release.Pressed = false;
        GetTree().Root.PushInput(release);
    }

    private static Vector3 V3(object? v)
    {
        List<object?> a = Json.Arr(v)!;
        return new Vector3(Json.F(a[0]), Json.F(a[1]), Json.F(a[2]));
    }

    private void Pass(string suffix)
    {
        _done = true;
        GD.Print("BPCG_GLOBE_OK" + suffix);
        GetTree().Quit(0);
    }

    private bool Fail(string reason)
    {
        if (!_done)
        {
            _done = true;
            GD.Print("BPCG_GLOBE_FAIL: " + reason);
            GetTree().Quit(1);
        }
        return false;
    }
}
