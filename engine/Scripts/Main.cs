using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 회랑 장면 (scenes/main.tscn). 지형과 구운 레이어를 불러오고, 플레이어를 세우고, 보기 전환 키를 받습니다.
/// </summary>
/// <remarks>
/// 불러오기는 두 부분입니다. 장면이 열리면 다른 스레드가 회랑 파일을 모두 읽고 메시 배열을 계산합니다
/// (<see cref="CorridorFiles.Read"/>). 그동안 화면에는 진행 막대가 움직입니다. 다 읽으면 프레임마다 한 단계씩
/// 노드를 만듭니다 (<see cref="BakedLayers.Build"/>). 끝나면 플레이어를 세우고 <see cref="IsLoaded"/> 가 true 가 되며
/// CorridorReady 신호를 냅니다. 읽지 못한 파일은 화면의 '불러오기 문제' 상자에 나옵니다.
/// 불러오는 동안에는 N(처음 화면), M(지구본), 화면 크기 키만 받고 플레이어는 멈춰 둡니다.
/// 키는 <see cref="Hud.HelpText"/> 에 적혀 있습니다. 레이어 숫자 키(1~9, Shift, 0)는 여기서 직접 받습니다.
/// 숫자는 패널 순서(<see cref="BakedLayers.LayerIds"/>)를 따릅니다. 구운 레이어가 다 있으면 1~8 입니다.
/// </remarks>
public partial class Main : Node3D
{
    /// <summary>회랑을 다 불러와 걸어 다닐 수 있게 되면 냅니다.</summary>
    [Signal]
    public delegate void CorridorReadyEventHandler();

    /// <summary>단면을 켤 때 눈앞 거리 (m).</summary>
    public const float SectionDistanceM = 15.0f;

    /// <summary>[ ] 키로 단면을 옮기는 속력 (m/s, Shift 면 5 배).</summary>
    public const float SectionSpeedMS = 10.0f;

    /// <summary>노클립으로 시작할 때 지면 위 높이 (m).</summary>
    public const float NoclipSpawnClearanceM = 40.0f;

    /// <summary>동굴 입구로 옮길 때 산비탈 밖 끝에서 더 물러나는 거리와 높이 (m).</summary>
    public const float EntranceBackoffM = 4.0f;

    public const float EntranceEyeUpM = 1.0f;

    /// <summary>상태 글을 다시 쓰는 간격 (s).</summary>
    public const double StatusIntervalS = 0.1;

    public static readonly Dictionary<int, string> ColorModeNames = new()
    {
        [1] = "자연색",
        [2] = "지질도 (흙 아래 첫 암석)",
    };

    /// <summary>M 키로 가는 지구본 장면 (행성 전체).</summary>
    public const string GlobeScene = "res://scenes/globe.tscn";

    /// <summary>N 키로 가는 처음 화면 (행성 만들기·지난 결과 열기).</summary>
    public const string StartScene = "res://scenes/start.tscn";

    private int _colorMode = 1;
    private int _entranceIndex = -1;
    private double _statusTimer;

    private Task<CorridorFiles>? _reading;
    private CorridorRequest? _request;
    private LoadProgress? _readProgress;
    private IEnumerator<int>? _building;
    private int _buildStage;
    private long _loadStart;
    private long _lastLoadFrame;

    /// <summary>
    /// 불러오는 동안 한 프레임에 쓰는 만들기 시간 (ms). 일 조각(메시 덩어리, 충돌 조각)을 이 시간이 찰 때까지 이어서 하고
    /// 화면을 그립니다. 조각 하나가 이보다 길면 그 프레임만 길어집니다.
    /// </summary>
    public const double LoadFrameBudgetMs = 25.0;

    /// <summary>불러오기 화면의 전체 단계 수 (읽기 + 만들기).</summary>
    public static int LoadSteps => CorridorFiles.Stages.Length + BakedLayers.BuildStages.Length;

    /// <summary>회랑 파일을 다 불러와 걸어 다닐 수 있는지.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>장면이 열린 때부터 다 불러올 때까지 걸린 시간 (s).</summary>
    public double LoadSeconds { get; private set; }

    /// <summary>불러오는 동안 가장 길었던 프레임 (ms). 화면이 이만큼 멈췄습니다.</summary>
    public double LongestLoadFrameMs { get; private set; }

    public HeightmapTerrain Terrain { get; private set; } = null!;

    public BakedLayers Baked { get; private set; } = null!;

    public Player Player { get; private set; } = null!;

    public Hud Hud { get; private set; } = null!;

    public Light3D Lamp { get; private set; } = null!;

    public override void _Ready()
    {
        UiScale.Apply(GetWindow());
        Terrain = GetNode<HeightmapTerrain>("Terrain");
        Baked = GetNode<BakedLayers>("Baked");
        Player = GetNode<Player>("Player");
        Hud = GetNode<Hud>("Hud");
        Lamp = GetNode<Light3D>("Player/Head/Camera/Lamp");

        Baked.SectionAnchor = SectionInFront;
        Player.GroundHeightAt = GroundY;
        Hud.ActionRequested += OnHudAction;
        Baked.TerrainRebuilt += OnTerrainRebuilt;
        StartLoading();
    }

    /// <summary>
    /// 다른 스레드에서 회랑 파일 읽기를 시작하고 불러오기 화면을 띄웁니다. 그동안 플레이어는 멈춰 둡니다
    /// (땅이 없어 떨어지지 않게).
    /// </summary>
    private void StartLoading()
    {
        CorridorRequest request = CorridorRequest.For(Terrain);
        Player.ProcessMode = ProcessModeEnum.Disabled;
        Hud.SetWorldVisible(false);
        Hud.Loading.Begin(request.Dir);
        Hud.Loading.SetStage(1, LoadSteps, CorridorFiles.Stages[0], 0.0);
        _loadStart = Stopwatch.GetTimestamp();
        _lastLoadFrame = _loadStart;
        var progress = new LoadProgress();
        _request = request;
        _readProgress = progress;
        _reading = Task.Run(() => CorridorFiles.Read(request, progress));
    }

    /// <summary>
    /// 불러오기를 한 프레임만큼 나아갑니다. 읽는 중이면 진행 글만 바꾸고, 다 읽었으면 만들기 일 조각을
    /// <see cref="LoadFrameBudgetMs"/> 동안 이어서 합니다.
    /// </summary>
    private void StepLoading()
    {
        long now = Stopwatch.GetTimestamp();
        LongestLoadFrameMs = Math.Max(LongestLoadFrameMs, Stopwatch.GetElapsedTime(_lastLoadFrame, now).TotalMilliseconds);
        _lastLoadFrame = now;
        double seconds = Stopwatch.GetElapsedTime(_loadStart, now).TotalSeconds;
        if (_building is null)
        {
            if (!_reading!.IsCompleted)
            {
                int stage = _readProgress!.Stage;
                Hud.Loading.SetStage(stage + 1, LoadSteps, CorridorFiles.Stages[stage], seconds);
                return;
            }
            // Read 는 예외를 문제 한 줄로 바꾸므로 보통은 실패하지 않습니다. 그래도 실패하면 빈 결과로 엽니다.
            CorridorFiles files = _reading.IsCompletedSuccessfully
                ? _reading.Result
                : CorridorFiles.Failed(_request!.Dir, _reading.Exception?.GetBaseException());
            _reading = null;
            _building = Baked.Build(files, Terrain).GetEnumerator();
        }
        long frameStart = Stopwatch.GetTimestamp();
        do
        {
            if (!BuildPiece())
            {
                FinishLoading(Stopwatch.GetElapsedTime(_loadStart).TotalSeconds);
                return;
            }
        }
        while (Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds < LoadFrameBudgetMs);
        Hud.Loading.SetStage(
            CorridorFiles.Stages.Length + _buildStage + 1, LoadSteps, BakedLayers.BuildStages[_buildStage].Label, seconds);
    }

    /// <summary>만들기 일 한 조각을 하고, 그 시간을 단계별 시간에 더합니다. 다 만들었으면 false.</summary>
    private bool BuildPiece()
    {
        // 이번 조각은 앞 MoveNext 가 알려 준 단계의 일입니다.
        (string key, string label) = BakedLayers.BuildStages[_buildStage];
        long workStart = Stopwatch.GetTimestamp();
        bool more;
        try
        {
            more = _building!.MoveNext();
        }
        catch (Exception e)
        {
            more = false;
            Baked.Problems.Add($"회랑을 만들다 예외가 나서 멈췄습니다 ({label}): {e.GetType().Name}: {e.Message}. 콘솔 기록에 전체 내용이 있습니다");
            GD.Print($"회랑 만들기 예외: {e}");
        }
        long workMs = (long)Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        Baked.LoadMs[key] = Baked.LoadMs.GetValueOrDefault(key) + workMs;
        if (more)
        {
            _buildStage = _building!.Current;
        }
        return more;
    }

    /// <summary>다 불러왔습니다: 플레이어를 세우고, 패널을 묶고, 불러오기 문제를 화면과 콘솔에 띄웁니다.</summary>
    private void FinishLoading(double seconds)
    {
        _building = null;
        IsLoaded = true;
        LoadSeconds = seconds;
        float clearance = Player.Noclip ? NoclipSpawnClearanceM : 2.0f;
        Player.GlobalPosition = Terrain.SpawnPoint(clearance);
        Player.RememberSpawn();
        Player.ProcessMode = ProcessModeEnum.Inherit;
        Hud.BindLayers(Baked);
        Hud.SetWorldVisible(true);
        Hud.Loading.Finish(Baked.Problems);
        foreach (string problem in Baked.Problems)
        {
            GD.Print("[회랑] 불러오기 문제: " + problem);
        }
        UpdateOptionLabels();
        UpdateStatus();
        Baked.PrintLoadTimes();
        GD.Print($"[회랑] 불러오기 끝: 문제 {Baked.Problems.Count}개, 가장 긴 프레임 {LongestLoadFrameMs:F0} ms, {seconds:F1} s");
        EmitSignal(SignalName.CorridorReady);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsLoaded)
        {
            LoadingInput(@event);
            return;
        }
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            Key code = key.PhysicalKeycode;
            if (code >= Key.Key1 && code <= Key.Key9)
            {
                List<string> ids = Baked.LayerIds();
                int i = (int)(code - Key.Key1);
                if (i < ids.Count)
                {
                    if (key.ShiftPressed && Baked.CanSolo(ids[i]))
                    {
                        Baked.Solo(ids[i]);
                    }
                    else
                    {
                        Baked.ToggleLayer(ids[i]);
                    }
                }
                return;
            }
            if (code == Key.Key0)
            {
                Baked.ShowAll();
                return;
            }
            if (code == Key.M)
            {
                OpenGlobe();
                return;
            }
            if (code == Key.N)
            {
                OpenStart();
                return;
            }
            if (UiScale.HandleKey(GetWindow(), code))
            {
                UpdateStatus();
                return;
            }
        }
        if (@event.IsActionPressed("toggle_section"))
        {
            Baked.ToggleLayer("section");
        }
        else if (@event.IsActionPressed("toggle_color_mode"))
        {
            CycleColorMode();
        }
        else if (@event.IsActionPressed("toggle_lamp"))
        {
            Lamp.Visible = !Lamp.Visible;
            UpdateOptionLabels();
        }
        else if (@event.IsActionPressed("next_entrance"))
        {
            bool shift = @event is InputEventWithModifiers m && m.ShiftPressed;
            GoToEntrance(shift ? -1 : 1);
        }
        else if (@event.IsActionPressed("toggle_help"))
        {
            Hud.ToggleHelp();
        }
        else if (@event.IsActionPressed("toggle_panel"))
        {
            Hud.TogglePanel();
        }
    }

    /// <summary>불러오는 동안 받는 키: N(처음 화면), M(지구본), 화면 크기(-, =).</summary>
    private void LoadingInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }
        switch (key.PhysicalKeycode)
        {
            case Key.N:
                OpenStart();
                break;
            case Key.M:
                OpenGlobe();
                break;
            default:
                UiScale.HandleKey(GetWindow(), key.PhysicalKeycode);
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (!IsLoaded)
        {
            StepLoading();
            return;
        }
        if (Baked.SectionOn)
        {
            float push = Input.GetAxis("section_pull", "section_push");
            if (push != 0.0f)
            {
                float fast = Input.IsActionPressed("move_fast") ? 5.0f : 1.0f;
                Baked.MoveSection(push * SectionSpeedMS * fast * (float)delta);
            }
        }
        _statusTimer -= delta;
        if (_statusTimer <= 0.0)
        {
            _statusTimer = StatusIntervalS;
            UpdateStatus();
        }
    }

    /// <summary>지구본 장면(행성 전체)으로 갑니다. 지구본에서 M 을 누르면 이 장면으로 돌아옵니다.</summary>
    public void OpenGlobe()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().ChangeSceneToFile(GlobeScene);
    }

    /// <summary>처음 화면(행성 만들기·지난 결과)으로 갑니다.</summary>
    public void OpenStart()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().ChangeSceneToFile(StartScene);
    }

    /// <summary>지형 색: 자연색 ↔ 지질도. 구운 재질 부피가 없으면 바꾸지 않습니다.</summary>
    public void CycleColorMode()
    {
        if (Terrain.Material is not ShaderMaterial m || Baked.Strata is null)
        {
            return;
        }
        _colorMode = _colorMode == 1 ? 2 : 1;
        m.SetShaderParameter("color_mode", _colorMode);
        UpdateOptionLabels();
    }

    /// <summary>step 번째 다음 동굴 입구 앞으로 옮겨 입구 안쪽을 봅니다 (노클립으로 바꿈).</summary>
    public void GoToEntrance(int step)
    {
        int n = Baked.EntrancesInside.Count;
        if (n == 0)
        {
            return;
        }
        _entranceIndex = Mathf.PosMod(_entranceIndex + step, n);
        Vector3 inside = Baked.EntrancesInside[_entranceIndex];
        Vector3 outside = Baked.EntrancesOutside[_entranceIndex];
        Vector3 outDir = outside - inside;
        outDir.Y = 0.0f;
        outDir = outDir.Length() > 1e-6f ? outDir.Normalized() : Vector3.Back;
        Vector3 eye = outside + (outDir * EntranceBackoffM) + (Vector3.Up * EntranceEyeUpM);
        if (!Player.Noclip)
        {
            Player.SetNoclip(true);
        }
        Player.LookFrom(eye, inside);
    }

    private void OnHudAction(string action)
    {
        switch (action)
        {
            case "color_mode":
                CycleColorMode();
                break;
            case "globe":
                OpenGlobe();
                break;
            case "start":
                OpenStart();
                break;
            case "lamp":
                Lamp.Visible = !Lamp.Visible;
                UpdateOptionLabels();
                break;
        }
    }

    /// <summary>회랑 지형 높이맵이 바뀌면 (프랙탈 디테일) 걷는 플레이어가 새 땅속에 묻히지 않게 올립니다.</summary>
    private void OnTerrainRebuilt()
    {
        if (!Player.Noclip)
        {
            Player.LiftAboveGround();
        }
    }

    private void UpdateOptionLabels()
    {
        string colorLabel = ColorModeNames.GetValueOrDefault(_colorMode, "높이 무늬");
        if (Baked.Strata is null)
        {
            colorLabel = Baked.Loaded ? "자연색" : "높이 무늬 (표본 지형)";
        }
        Hud.SetOptionLabels(colorLabel, Lamp.Visible);
    }

    /// <summary>눈앞 SectionDistanceM 에 수직으로 선 자르는 면 (점, 법선(카메라 쪽)).</summary>
    private (Vector3 Point, Vector3 Normal) SectionInFront()
    {
        Vector3 eye = Player.EyePosition();
        Vector3 forward = -Player.Head.GlobalBasis.Z;
        forward.Y = 0.0f;
        forward = forward.Length() > 1e-6f ? forward.Normalized() : Vector3.Forward;
        return (eye + (forward * SectionDistanceM), -forward);
    }

    /// <summary>전역 위치의 회랑 지면 높이 (엔진 Y). 회랑 밖이면 주변 지형 높이, 그것도 없으면 NaN.</summary>
    public float GroundY(Vector3 p)
    {
        float h = Terrain.HeightAt(Terrain.ToLocal(p));
        if (float.IsFinite(h))
        {
            return Terrain.ToGlobal(new Vector3(0.0f, h, 0.0f)).Y;
        }
        if (Baked.GetNodeOrNull("Surround") is HeightmapTerrain surround)
        {
            float hs = surround.HeightAt(surround.ToLocal(p));
            if (float.IsFinite(hs))
            {
                return surround.ToGlobal(new Vector3(0.0f, hs, 0.0f)).Y;
            }
        }
        return float.NaN;
    }

    /// <summary>왼쪽 위 상태 글을 다시 씁니다 (화면 캡처가 찍기 전에도 부름).</summary>
    public void UpdateStatus()
    {
        Vector3 eye = Player.EyePosition();
        var lines = new List<string>();
        string mode = Player.Noclip ? "날기 (땅을 뚫고 지나감)" : "걷기";
        lines.Add($"{mode} · 속력 {Player.CurrentSpeedMS():F0} m/s");
        string where = $"동 {eye.X:+0;-0;+0} m, 북 {-eye.Z:+0;-0;+0} m (회랑 가운데 기준)";
        if (Baked.Loaded)
        {
            where += $" · 해발 {eye.Y + Baked.YOffsetM:F0} m";
        }
        lines.Add(where);
        float ground = GroundY(eye);
        if (float.IsFinite(ground))
        {
            if (eye.Y < ground)
            {
                string info = $"지표 아래 {ground - eye.Y:F0} m";
                if (Baked.Strata is not null)
                {
                    int id = Baked.Strata.IdAt(eye);
                    if (id >= 0)
                    {
                        info += $" · {Baked.Strata.NameOf(id)} 속";
                    }
                    else if (ground - eye.Y > Baked.Strata.DepthM())
                    {
                        info += $" · 암석 자료는 지표 아래 {Baked.Strata.DepthM():F0} m까지만 있음";
                    }
                }
                lines.Add(info);
            }
            else
            {
                lines.Add($"지표 위 {eye.Y - ground:F0} m");
            }
            float wt = Baked.WaterTableY(eye);
            if (float.IsFinite(wt))
            {
                lines.Add($"땅속 물이 차 있는 높이(지하수면): 지표 아래 {ground - wt:F1} m");
            }
        }
        if (Baked.SectionOn)
        {
            float d = Math.Abs(Baked.SectionNormal.Dot(eye - Baked.SectionPoint));
            lines.Add($"단면 칼 켜짐 · 눈앞 {d:F0} m ([ ]로 옮기기, X로 끄기)");
        }
        if (Baked.EntrancesInside.Count > 0)
        {
            string at = _entranceIndex < 0 ? "" : $" (지금 {_entranceIndex + 1}번 앞)";
            lines.Add($"동굴 입구 {Baked.EntrancesInside.Count}곳{at} · T로 다음 입구 앞에 가기");
        }
        string notice = UiScale.Notice();
        if (notice.Length > 0)
        {
            lines.Add(notice);
        }
        Hud.SetStatus(string.Join("\n", lines));
    }
}
