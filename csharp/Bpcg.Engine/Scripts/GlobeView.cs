using System;
using System.Collections.Generic;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 지구본: 큐브스피어 면 6장을 고도만큼 부풀려 그리고, 고른 필드의 색을 칠합니다.
/// </summary>
/// <remarks>
/// Setup(data) 를 부르면 면 메시 6개(Face0~Face5), 대기 빛(Atmosphere), 히어로 표시(Hero)를 자식으로 만듭니다.
/// point_markers 인 범주 필드(드문 동굴 칸 등)를 고르면 그 칸마다 점(Markers)도 찍습니다. 반지름 1 이 해수면이고,
/// 좌표는 GlobeData 의 약속(Godot +Y = 북쪽)을 따릅니다. 면 f 의 메시는 모서리 (N+1)² 개의 단위 방향입니다.
/// 셰이더(globe.gdshader)가 모서리 고도 텍스처를 읽어 r = 1 + 과장 배율 · max(고도, 해수면) / 반지름 으로 밀어 냅니다.
/// </remarks>
public partial class GlobeView : Node3D
{
    /// <summary>고른 필드가 바뀌었을 때.</summary>
    [Signal]
    public delegate void FieldChangedEventHandler(string name);

    /// <summary>고도 과장, 겹쳐 보기, 경위선, 그늘이 바뀌었을 때.</summary>
    [Signal]
    public delegate void DisplayChangedEventHandler();

    public const string ShaderPath = "res://shaders/globe.gdshader";
    public const string AtmosphereShaderPath = "res://shaders/atmosphere.gdshader";
    public const float DefaultExaggeration = 20.0f;
    public const float MinExaggeration = 1.0f;
    public const float MaxExaggeration = 100.0f;

    /// <summary>[ ] 키와 패널 버튼이 오르내리는 고도 과장 단계.</summary>
    public static readonly float[] ExaggerationSteps = [1.0f, 2.0f, 5.0f, 10.0f, 20.0f, 30.0f, 50.0f, 75.0f, 100.0f];

    /// <summary>겹쳐 보기 이름 (globe.json 의 overlays[].name).</summary>
    public const string OverlayRivers = "rivers";
    public const string OverlayLakes = "lakes";

    /// <summary>
    /// 강 유량 등급 (globe.json 의 rivers 값 1~4). 기본은 아주 큰 강(4 등급, 1000 m³/s 이상)만 그립니다.
    /// L0 칸(약 20 km)에서는 육지 대부분이 강 문턱을 넘어 3 등급부터 그리면 지형을 가립니다.
    /// </summary>
    public const int DefaultRiverMinClass = 4;
    public const int MaxRiverClass = 7;

    /// <summary>globe.json 에 색이 없을 때 쓰는 강·호수 색 (sRGB).</summary>
    public static readonly Color RiverFallbackColor = Color.Color8(30, 100, 210);
    public static readonly Color LakeFallbackColor = Color.Color8(90, 200, 250);

    /// <summary>경위선 간격 (도).</summary>
    public const float GridStepDeg = 15.0f;

    /// <summary>대기 빛 구의 반지름 (지구본 반지름 배).</summary>
    public const float AtmosphereRadius = 1.06f;

    /// <summary>히어로 표시 핀의 크기 (지구본 반지름 배).</summary>
    public const float PinHeight = 0.06f;
    public const float PinRadius = 0.0035f;
    public const float PinHeadRadius = 0.012f;
    public static readonly Color PinColor = new(1.0f, 0.36f, 0.14f);

    /// <summary>화면에서 고르기: 해수면 구와 만난 뒤 그 자리 높이의 구로 다시 맞히는 횟수.</summary>
    public const int PickIterations = 4;

    /// <summary>드문 범주 칸의 점: 화면 크기(픽셀)와 지표 위로 띄우는 높이(지구본 반지름 배), 점 수 상한.</summary>
    public const float MarkerPointSizePx = 7.0f;
    public const float MarkerLift = 0.002f;
    public const int MaxPointMarkers = 20000;

    public GlobeData? Data { get; private set; }

    /// <summary>지금 칠한 필드 이름.</summary>
    public string ActiveField { get; private set; } = "";

    /// <summary>고도 과장 배율 (1 = 실제 비율).</summary>
    public float Exaggeration { get; private set; } = DefaultExaggeration;

    public bool ShowRivers { get; private set; } = true;

    public bool ShowLakes { get; private set; } = true;

    public bool ShowGrid { get; private set; } = true;

    /// <summary>기복 그늘과 대기 빛 (꾸밈, 자료 아님). 끄면 화면 색이 범례 색과 같습니다.</summary>
    public bool ShowShading { get; private set; } = true;

    /// <summary>이 등급 이상의 강만 그립니다 (1 = 모든 강 칸).</summary>
    public int RiverMinClass { get; private set; } = DefaultRiverMinClass;

    /// <summary>면 메시 6개 (면 번호 순서)와 그 재질.</summary>
    public List<MeshInstance3D> Faces { get; } = [];

    public List<ShaderMaterial> Materials { get; } = [];

    /// <summary>히어로 유역 표시 (없으면 null).</summary>
    public Node3D? HeroMarker { get; private set; }

    public Label3D? HeroLabel { get; private set; }

    public MeshInstance3D? Atmosphere { get; private set; }

    /// <summary>드문 범주 칸의 점 (point_markers 필드를 골랐을 때만, 없으면 null)과 그 수.</summary>
    public MeshInstance3D? PointMarkers { get; private set; }

    public int MarkerCount { get; private set; }

    /// <summary>자료로 지구본을 만듭니다. 자료가 없거나 잘못됐으면 아무것도 그리지 않습니다.</summary>
    public void Setup(GlobeData? globeData)
    {
        Clear();
        Data = globeData;
        if (Data is null || !Data.IsValid())
        {
            return;
        }
        ImageTexture[] corners = Data.CornerTextures();
        ImageTexture[] overlay = Data.OverlayTextures();
        Aabb box = CullBox();
        var shader = GD.Load<Shader>(ShaderPath);
        for (int f = 0; f < GlobeData.FaceCount; f++)
        {
            var m = new ShaderMaterial { Shader = shader };
            m.SetShaderParameter("corner_elevation", corners[f]);
            m.SetShaderParameter("overlay", overlay[f]);
            m.SetShaderParameter("face_res", Data.FaceRes);
            m.SetShaderParameter("radius_m", Data.RadiusM);
            m.SetShaderParameter("sea_level_m", Data.SeaLevelM);
            m.SetShaderParameter("grid_step_deg", GridStepDeg);
            m.SetShaderParameter("river_colors", RiverColors());
            m.SetShaderParameter("lake_color", Data.OverlayValueColor(OverlayLakes, 1, LakeFallbackColor));
            Materials.Add(m);
            var mi = new MeshInstance3D
            {
                Name = $"Face{f}",
                Mesh = BuildFaceMesh(f),
                MaterialOverride = m,
                // 셰이더가 꼭짓점을 밀어 내므로 화면 밖 판정 상자를 가장 큰 과장에 맞춥니다.
                CustomAabb = box,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mi);
            Faces.Add(mi);
        }
        BuildAtmosphere();
        BuildHeroMarker();
        ApplyDisplay();
        SetField(Data.DefaultField());
    }

    /// <summary>만든 자식을 모두 지웁니다.</summary>
    public void Clear()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        Faces.Clear();
        Materials.Clear();
        HeroMarker = null;
        HeroLabel = null;
        Atmosphere = null;
        PointMarkers = null;
        MarkerCount = 0;
        ActiveField = "";
    }

    public bool IsReady() => Data is not null && Data.IsValid() && Faces.Count == GlobeData.FaceCount;

    /// <summary>면 f 의 메시: 모서리 (N+1)² 개의 단위 방향, UV = (열/N, 행/N), 바깥이 앞면.</summary>
    public ArrayMesh BuildFaceMesh(int face)
    {
        GlobeData data = Data!;
        int n = data.FaceRes;
        int side = n + 1;
        // 모서리 좌표의 tan 값. 가운데를 기준으로 거울 대칭이 정확하도록 반만 계산합니다.
        double[] tans = new double[side];
        for (int k = 0; k <= n / 2; k++)
        {
            double t = GlobeData.TanQuarter(-1.0 + (2.0 * k / n));
            tans[k] = t;
            tans[n - k] = -t;
        }
        Vector3 nf = data.FaceN[face];
        Vector3 uf = data.FaceU[face];
        Vector3 vf = data.FaceV[face];
        var vertices = new Vector3[side * side];
        var normals = new Vector3[side * side];
        var uvs = new Vector2[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int i = (r * side) + c;
                Vector3 d = (nf + (uf * (float)tans[c]) + (vf * (float)tans[r])).Normalized();
                vertices[i] = d;
                normals[i] = d;
                uvs[i] = new Vector2((float)c / n, (float)r / n);
            }
        }

        // Godot 는 시계 방향(바깥에서 볼 때)을 앞면으로 봅니다. u × v = n 이므로
        // (북서, 남서, 북동) 순서가 바깥에서 시계 방향입니다 (HeightmapLoader 와 반대 축이라 순서도 반대).
        int[] indices = new int[n * n * 6];
        int kk = 0;
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int nw = (r * side) + c;
                int ne = nw + 1;
                int sw = nw + side;
                int se = sw + 1;
                indices[kk] = nw;
                indices[kk + 1] = sw;
                indices[kk + 2] = ne;
                indices[kk + 3] = ne;
                indices[kk + 4] = sw;
                indices[kk + 5] = se;
                kk += 6;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>필드를 고릅니다. 텍스처는 처음 고를 때 만듭니다. 없는 이름이면 false.</summary>
    public bool SetField(string name)
    {
        if (!IsReady() || !Data!.HasField(name))
        {
            return false;
        }
        ImageTexture[] textures = Data.FieldTextures(name);
        bool categorical = Data.IsCategorical(name);
        for (int f = 0; f < Materials.Count; f++)
        {
            Materials[f].SetShaderParameter("field_color", textures[f]);
            Materials[f].SetShaderParameter("categorical", categorical);
        }
        ActiveField = name;
        UpdatePointMarkers();
        EmitSignal(SignalName.FieldChanged, name);
        return true;
    }

    /// <summary>고도 과장 배율 (1 ~ 100).</summary>
    public void SetExaggeration(float value)
    {
        Exaggeration = Math.Clamp(value, MinExaggeration, MaxExaggeration);
        foreach (ShaderMaterial m in Materials)
        {
            m.SetShaderParameter("exaggeration", Exaggeration);
        }
        PlaceHeroMarker();
        UpdatePointMarkers();
        EmitSignal(SignalName.DisplayChanged);
    }

    /// <summary>고도 과장을 한 단계 올리거나(step &gt; 0) 내립니다.</summary>
    public void StepExaggeration(int step)
    {
        if (step > 0)
        {
            foreach (float s in ExaggerationSteps)
            {
                if (s > Exaggeration + 1e-6f)
                {
                    SetExaggeration(s);
                    return;
                }
            }
            SetExaggeration(MaxExaggeration);
        }
        else if (step < 0)
        {
            for (int i = ExaggerationSteps.Length - 1; i >= 0; i--)
            {
                if (ExaggerationSteps[i] < Exaggeration - 1e-6f)
                {
                    SetExaggeration(ExaggerationSteps[i]);
                    return;
                }
            }
            SetExaggeration(MinExaggeration);
        }
    }

    public bool HasOverlay(string name) => Data is not null && Data.HasOverlay(name);

    public bool IsOverlayVisible(string name) => name switch
    {
        OverlayRivers => ShowRivers,
        OverlayLakes => ShowLakes,
        _ => false,
    };

    public void SetOverlayVisible(string name, bool on)
    {
        switch (name)
        {
            case OverlayRivers:
                ShowRivers = on;
                break;
            case OverlayLakes:
                ShowLakes = on;
                break;
            default:
                return;
        }
        ApplyDisplay();
        EmitSignal(SignalName.DisplayChanged);
    }

    public void ToggleOverlay(string name) => SetOverlayVisible(name, !IsOverlayVisible(name));

    /// <summary>겹쳐 보기 값 하나가 지금 화면에 그려지지 않는 까닭 (그려지면 빈 글). 마우스 자리 읽기가 씁니다.</summary>
    public string OverlayHiddenReason(string name, int value)
    {
        if (value <= 0)
        {
            return "값 없음";
        }
        if (!IsOverlayVisible(name))
        {
            return "꺼 둠";
        }
        if (name == OverlayRivers && value < RiverMinClass)
        {
            return $"지금은 {RiverMinClass} 등급부터 그리므로 그리지 않음";
        }
        return "";
    }

    /// <summary>그릴 강의 가장 낮은 유량 등급 (1 ~ 가장 높은 등급).</summary>
    public void SetRiverMinClass(int value)
    {
        RiverMinClass = Math.Clamp(value, 1, MaxRiverClassOf());
        ApplyDisplay();
        EmitSignal(SignalName.DisplayChanged);
    }

    /// <summary>강 등급 문턱을 한 단계 올립니다. 가장 높은 등급 다음은 1 등급(모든 강)입니다.</summary>
    public void CycleRiverMinClass() =>
        SetRiverMinClass(RiverMinClass >= MaxRiverClassOf() ? 1 : RiverMinClass + 1);

    /// <summary>globe.json 의 강 등급 가운데 가장 높은 것 (없으면 1).</summary>
    public int MaxRiverClassOf()
    {
        int top = 1;
        if (Data is not null)
        {
            foreach (object? cat in Json.Arr(Json.Get(Data.Overlay(OverlayRivers), "categories")) ?? [])
            {
                if (Json.Obj(cat) is OrderedDictionary<string, object?> c)
                {
                    top = Math.Max(top, Json.Get(c, "id") is object id ? Json.I(id) : 0);
                }
            }
        }
        return Math.Min(top, MaxRiverClass);
    }

    public void SetGridVisible(bool on)
    {
        ShowGrid = on;
        ApplyDisplay();
        EmitSignal(SignalName.DisplayChanged);
    }

    public void ToggleGrid() => SetGridVisible(!ShowGrid);

    /// <summary>기복 그늘과 대기 빛(꾸밈)을 켜거나 끕니다. 끄면 모든 칸이 범례 색 그대로입니다.</summary>
    public void SetShadingVisible(bool on)
    {
        ShowShading = on;
        ApplyDisplay();
        EmitSignal(SignalName.DisplayChanged);
    }

    public void ToggleShading() => SetShadingVisible(!ShowShading);

    /// <summary>방향의 지표 반지름 (지구본 반지름 배). 모서리 고도를 쌍선형 보간해 메시와 거의 같습니다.</summary>
    public float SurfaceRadius(Vector3 dir)
    {
        if (!IsReady())
        {
            return 1.0f;
        }
        double z = Math.Max(Data!.CornerElevationAt(dir), Data.SeaLevelM);
        return (float)(1.0 + (Exaggeration * z / Data.RadiusM));
    }

    /// <summary>화면 위치가 가리키는 지구본 위 방향 (지구본 좌표의 단위 벡터). 지구본을 맞히지 않으면 Zero.</summary>
    public Vector3 Pick(Camera3D? camera, Vector2 screenPos)
    {
        if (!IsReady() || camera is null)
        {
            return Vector3.Zero;
        }
        Vector3 origin = ToLocal(camera.ProjectRayOrigin(screenPos));
        Vector3 ray = (GlobalBasis.Inverse() * camera.ProjectRayNormal(screenPos)).Normalized();
        float radius = 1.0f;
        Vector3 hit = Vector3.Zero;
        for (int i = 0; i < PickIterations; i++)
        {
            float t = RaySphere(origin, ray, radius);
            if (t < 0.0f)
            {
                return hit;
            }
            hit = (origin + (ray * t)).Normalized();
            radius = SurfaceRadius(hit);
        }
        return hit;
    }

    /// <summary>히어로 유역 방향 (없으면 Zero).</summary>
    public Vector3 HeroDirection() => Data is null ? Vector3.Zero : Data.HeroDirection();

    private void ApplyDisplay()
    {
        foreach (ShaderMaterial m in Materials)
        {
            m.SetShaderParameter("exaggeration", Exaggeration);
            m.SetShaderParameter("show_rivers", ShowRivers);
            m.SetShaderParameter("show_lakes", ShowLakes);
            m.SetShaderParameter("show_grid", ShowGrid);
            m.SetShaderParameter("river_min_class", RiverMinClass);
            m.SetShaderParameter("shade_relief", ShowShading);
        }
        if (Atmosphere is not null)
        {
            Atmosphere.Visible = ShowShading;
        }
    }

    /// <summary>강 등급별 색 (셰이더용 선형 색, 0 ~ MaxRiverClass 번).</summary>
    private Color[] RiverColors()
    {
        var colors = new Color[MaxRiverClass + 1];
        for (int k = 0; k <= MaxRiverClass; k++)
        {
            colors[k] = Data!.OverlayValueColor(OverlayRivers, k, RiverFallbackColor).SrgbToLinear();
        }
        return colors;
    }

    private Aabb CullBox()
    {
        double topM = Math.Max(Data!.CornerMaxM, Data.SeaLevelM);
        float r = (float)(1.0 + (MaxExaggeration * Math.Max(topM, 0.0) / Data.RadiusM) + PinHeight + 0.05);
        return new Aabb(new Vector3(-r, -r, -r), new Vector3(2.0f * r, 2.0f * r, 2.0f * r));
    }

    private void BuildAtmosphere()
    {
        var sphere = new SphereMesh
        {
            Radius = AtmosphereRadius,
            Height = 2.0f * AtmosphereRadius,
            RadialSegments = 96,
            Rings = 48,
        };
        var m = new ShaderMaterial { Shader = GD.Load<Shader>(AtmosphereShaderPath) };
        m.SetShaderParameter("globe_radius", 1.0f);
        m.SetShaderParameter("glow_radius", AtmosphereRadius);
        Atmosphere = new MeshInstance3D
        {
            Name = "Atmosphere",
            Mesh = sphere,
            MaterialOverride = m,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(Atmosphere);
    }

    private void BuildHeroMarker()
    {
        Vector3 dir = Data!.HeroDirection();
        if (dir == Vector3.Zero)
        {
            return;
        }
        HeroMarker = new Node3D { Name = "Hero" };
        var pinMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = PinColor,
        };
        var stickMesh = new CylinderMesh
        {
            TopRadius = PinRadius,
            BottomRadius = PinRadius,
            Height = PinHeight,
            RadialSegments = 12,
            Rings = 1,
        };
        HeroMarker.AddChild(new MeshInstance3D
        {
            Name = "Pin",
            Mesh = stickMesh,
            MaterialOverride = pinMaterial,
            Position = new Vector3(0.0f, PinHeight * 0.5f, 0.0f),
        });
        var headMesh = new SphereMesh
        {
            Radius = PinHeadRadius,
            Height = 2.0f * PinHeadRadius,
            RadialSegments = 16,
            Rings = 8,
        };
        HeroMarker.AddChild(new MeshInstance3D
        {
            Name = "Head",
            Mesh = headMesh,
            MaterialOverride = pinMaterial,
            Position = new Vector3(0.0f, PinHeight, 0.0f),
        });

        string title = Json.Str(Data.Hero, "label", "히어로 유역");
        HeroLabel = new Label3D
        {
            Name = "Label",
            Font = Ui.Font,
            FontSize = 30,
            OutlineSize = 10,
            PixelSize = 0.0009f,
            FixedSize = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            VerticalAlignment = VerticalAlignment.Bottom,
            Position = new Vector3(0.0f, PinHeight + (PinHeadRadius * 1.5f), 0.0f),
            Text = $"{title} ({GlobeData.FormatLatLon(GlobeData.LatLon(dir))})",
        };
        HeroMarker.AddChild(HeroLabel);
        AddChild(HeroMarker);
        PlaceHeroMarker();
    }

    /// <summary>
    /// 고른 필드가 point_markers 이면 0 이 아닌 칸마다 범주 색 점을 찍습니다 (화면 크기 일정).
    /// 점은 칸 모서리 가운데 가장 높은 곳보다 조금 위에 둡니다(메시에 묻히지 않게).
    /// </summary>
    private void UpdatePointMarkers()
    {
        if (PointMarkers is not null)
        {
            RemoveChild(PointMarkers);
            PointMarkers.QueueFree();
            PointMarkers = null;
        }
        MarkerCount = 0;
        if (!IsReady() || !Data!.HasPointMarkers(ActiveField))
        {
            return;
        }
        int[] cells = Data.MarkerCells(ActiveField);
        if (cells.Length == 0 || cells.Length > MaxPointMarkers)
        {
            return;
        }
        byte[] values = (byte[])Data.Values(ActiveField)!;
        int nn = Data.FaceRes * Data.FaceRes;
        var points = new Vector3[cells.Length];
        var colors = new Color[cells.Length];
        for (int j = 0; j < cells.Length; j++)
        {
            int index = cells[j];
            int face = index / nn;
            int rest = index - (face * nn);
            int row = rest / Data.FaceRes;
            int col = rest - (row * Data.FaceRes);
            Vector3 dir = Data.CellDir(face, row, col);
            double z = Math.Max(Data.CellTopElevation(face, row, col), Data.SeaLevelM);
            points[j] = dir * (float)(1.0 + (Exaggeration * z / Data.RadiusM) + MarkerLift);
            colors[j] = Data.CategoryColor(ActiveField, values[index]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            UsePointSize = true,
            PointSize = MarkerPointSizePx,
        };
        PointMarkers = new MeshInstance3D
        {
            Name = "Markers",
            Mesh = mesh,
            MaterialOverride = material,
            CustomAabb = CullBox(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(PointMarkers);
        MarkerCount = points.Length;
    }

    /// <summary>핀을 히어로 방향의 지표에 세웁니다 (핀의 +Y 가 바깥).</summary>
    private void PlaceHeroMarker()
    {
        if (HeroMarker is null)
        {
            return;
        }
        Vector3 dir = Data!.HeroDirection();
        Basis basis = Basis.Identity;
        if (dir.Dot(Vector3.Up) < -0.9999f)
        {
            basis = new Basis(Vector3.Right, Mathf.Pi);
        }
        else if (dir.Dot(Vector3.Up) < 0.9999f)
        {
            basis = new Basis(new Quaternion(Vector3.Up, dir));
        }
        HeroMarker.Transform = new Transform3D(basis, dir * SurfaceRadius(dir));
    }

    /// <summary>원점 중심 반지름 radius 구와 광선이 처음 만나는 거리. 안 만나면 -1.</summary>
    private static float RaySphere(Vector3 origin, Vector3 ray, float radius)
    {
        float b = origin.Dot(ray);
        float c = origin.LengthSquared() - (radius * radius);
        float disc = (b * b) - c;
        if (disc < 0.0f)
        {
            return -1.0f;
        }
        float root = Mathf.Sqrt(disc);
        float t = -b - root;
        if (t < 0.0f)
        {
            t = -b + root;
        }
        return t >= 0.0f ? t : -1.0f;
    }
}
