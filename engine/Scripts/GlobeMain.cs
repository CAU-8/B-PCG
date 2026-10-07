using System.Collections.Generic;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 지구본 장면 (scenes/globe.tscn). 구운 지구본 자료를 불러와 지구본을 만들고, 키와 자리 읽기를 맡습니다.
/// </summary>
/// <remarks>
/// 자료는 <see cref="GlobeData.DefaultDir"/> 에서 읽습니다. 없으면 만드는 법을, 있는데 잘못됐으면 읽지 못한 까닭을
/// 화면 가운데에 알리고 멈추지 않습니다. 키는 <see cref="GlobeHud.HelpText"/> 에 적혀 있고, 물리 키 위치로 직접 받습니다.
/// Space(자동 회전)와 F(히어로 유역으로)는 카메라(GlobeCamera)가 받습니다.
/// </remarks>
public partial class GlobeMain : Node3D
{
    public const string CorridorScene = "res://scenes/main.tscn";
    public const string StartScene = "res://scenes/start.tscn";

    /// <summary>상태 글을 다시 쓰는 간격 (s).</summary>
    public const double StatusIntervalS = 0.1;

    /// <summary>고정한 자리 표시 구의 반지름 (지구본 반지름 배).</summary>
    public const float PinMarkerRadius = 0.006f;

    /// <summary>히어로가 없을 때 처음 볼 위도 (도).</summary>
    public const double StartLatDeg = 20.0;

    public GlobeData? Data { get; private set; }

    /// <summary>클릭으로 고정한 자리 (지구본 좌표의 단위 벡터, 없으면 Zero).</summary>
    public Vector3 PinnedDirection { get; private set; } = Vector3.Zero;

    public GlobeView Globe { get; private set; } = null!;

    public GlobeCamera Camera { get; private set; } = null!;

    public GlobeHud Hud { get; private set; } = null!;

    private double _statusTimer;
    private MeshInstance3D? _pinMarker;

    public override void _Ready()
    {
        UiScale.Apply(GetWindow());
        Globe = GetNode<GlobeView>("Globe");
        Camera = GetNode<GlobeCamera>("Camera");
        Hud = GetNode<GlobeHud>("Hud");
        Hud.CorridorRequested += EnterCorridor;
        Hud.StartRequested += EnterStart;
        Hud.HeroRequested += Camera.FlyToHero;
        Camera.Clicked += OnClicked;
        Data = GlobeData.LoadDir();
        if (!Data.IsValid())
        {
            GD.Print($"지구본 자료를 읽지 못했습니다: {Data.ErrorMessage}");
            Hud.ShowMissing(Data.ErrorMessage, Data.Missing);
            return;
        }
        Globe.Setup(Data);
        Camera.HeroDirection = Globe.HeroDirection();
        // 처음에는 히어로 유역(없으면 북위 20°, 경도 0°)을 정면에 둡니다.
        Vector3 start = Globe.HeroDirection();
        if (start == Vector3.Zero)
        {
            start = GlobeData.DirectionFromLatLon(StartLatDeg, 0.0);
        }
        Camera.FlyTo(start, 0.0f, GlobeCamera.StartDistance);
        Globe.DisplayChanged += PlacePinMarker;
        Hud.Bind(Globe);
        BuildPinMarker();
        GD.Print($"지구본: 면당 {Data.FaceRes} 칸, 레이어 {Data.Fields.Count} 개, 겹쳐 보기 {Data.Overlays.Count} 개, "
            + $"히어로 {(Globe.HeroDirection() != Vector3.Zero ? "있음" : "없음")}");
        UpdateStatus();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }
        if (UiScale.HandleKey(GetWindow(), key.PhysicalKeycode))
        {
            GetViewport().SetInputAsHandled();
            UpdateStatus();
            return;
        }
        bool handled = true;
        switch (key.PhysicalKeycode)
        {
            case Key.M:
                // 장면을 바꾸면 이 노드가 곧바로 트리에서 빠져 GetViewport() 가 null 이 되므로,
                // 입력을 처리했다고 먼저 알리고 바꿉니다.
                GetViewport().SetInputAsHandled();
                EnterCorridor();
                return;
            case Key.N:
                GetViewport().SetInputAsHandled();
                EnterStart();
                return;
            case Key.H:
                Hud.ToggleHelp();
                break;
            case Key.Tab:
                Hud.TogglePanel();
                break;
            default:
                handled = Globe.IsReady() && GlobeKey(key.PhysicalKeycode, key.ShiftPressed);
                break;
        }
        if (handled)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _statusTimer -= delta;
        if (_statusTimer <= 0.0)
        {
            _statusTimer = StatusIntervalS;
            UpdateStatus();
        }
    }

    /// <summary>정렬한 레이어 목록의 index 번째(0부터)를 고릅니다. 숫자 키 1~9 가 0~8 입니다.</summary>
    public bool SelectFieldIndex(int index)
    {
        List<string> names = Data!.OrderedFieldNames();
        if (index < 0 || index >= names.Count)
        {
            return false;
        }
        return Globe.SetField(names[index]);
    }

    /// <summary>그 자리의 모든 레이어 값을 왼쪽 위 상자에 고정합니다.</summary>
    public void Pin(Vector3 dir)
    {
        if (dir == Vector3.Zero || !Globe.IsReady())
        {
            Unpin();
            return;
        }
        PinnedDirection = dir.Normalized();
        Hud.SetPinned(Hud.DescribeAll(PinnedDirection));
        PlacePinMarker();
    }

    public void Unpin()
    {
        PinnedDirection = Vector3.Zero;
        Hud.SetPinned("");
        PlacePinMarker();
    }

    /// <summary>회랑(걷는 장면)으로 갑니다.</summary>
    public void EnterCorridor() => GetTree().ChangeSceneToFile(CorridorScene);

    /// <summary>처음 화면(행성 만들기·지난 결과)으로 갑니다.</summary>
    public void EnterStart() => GetTree().ChangeSceneToFile(StartScene);

    private bool GlobeKey(Key key, bool shift)
    {
        if (key >= Key.Key1 && key <= Key.Key9)
        {
            SelectFieldIndex((int)(key - Key.Key1));
            return true;
        }
        switch (key)
        {
            case Key.R:
                if (shift)
                {
                    Globe.CycleRiverMinClass();
                }
                else
                {
                    Globe.ToggleOverlay(GlobeView.OverlayRivers);
                }
                break;
            case Key.K:
                Globe.ToggleOverlay(GlobeView.OverlayLakes);
                break;
            case Key.L:
                Globe.ToggleGrid();
                break;
            case Key.G:
                Globe.ToggleShading();
                break;
            case Key.Bracketleft:
                Globe.StepExaggeration(-1);
                break;
            case Key.Bracketright:
                Globe.StepExaggeration(1);
                break;
            case Key.Escape:
                Unpin();
                break;
            default:
                return false;
        }
        return true;
    }

    private void OnClicked(Vector2 screenPosition)
    {
        if (!Globe.IsReady())
        {
            return;
        }
        Pin(Globe.Pick(Camera, screenPosition));
    }

    private void BuildPinMarker()
    {
        var sphere = new SphereMesh
        {
            Radius = PinMarkerRadius,
            Height = 2.0f * PinMarkerRadius,
            RadialSegments = 16,
            Rings = 8,
        };
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1.0f, 1.0f, 1.0f),
        };
        _pinMarker = new MeshInstance3D { Name = "PinnedPoint", Mesh = sphere, MaterialOverride = material, Visible = false };
        AddChild(_pinMarker);
    }

    private void PlacePinMarker()
    {
        if (_pinMarker is null)
        {
            return;
        }
        _pinMarker.Visible = PinnedDirection != Vector3.Zero;
        if (_pinMarker.Visible)
        {
            // 반쯤 땅에 묻히지 않도록 반지름의 절반만큼 띄웁니다.
            float radius = Globe.SurfaceRadius(PinnedDirection) + (0.5f * PinMarkerRadius);
            _pinMarker.GlobalPosition = Globe.ToGlobal(PinnedDirection * radius);
        }
    }

    private void UpdateStatus()
    {
        if (!Globe.IsReady())
        {
            return;
        }
        var lines = new List<string>
        {
            $"B-PCG 지구본 · {Data!.LabelOf(Globe.ActiveField)} · 고도 과장 ×{GlobeHud.FormatFactor(Globe.Exaggeration)}"
                + $" · 자동 회전 {(Camera.AutoRotate ? "켜짐" : "꺼짐")}",
            $"카메라: 해수면 위 약 {GlobeData.FormatNumber((Camera.Distance - 1.0) * Data.RadiusM / 1000.0)} km",
        };
        Vector3 dir = Globe.Pick(Camera, GetViewport().GetMousePosition());
        lines.Add(dir == Vector3.Zero
            ? "마우스가 지구본 밖에 있습니다 · 끌어서 돌리고 휠로 확대합니다"
            : Hud.DescribePoint(dir));
        string notice = UiScale.Notice();
        if (notice.Length > 0)
        {
            lines.Add(notice);
        }
        Hud.SetStatus(string.Join("\n", lines));
    }
}
