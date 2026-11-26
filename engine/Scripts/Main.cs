using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>걷기로 입구 앞에 설 때: 굴 축을 따라 지표를 재는 간격, 입구 아래 가장자리에서 밖으로 물러나는 거리 (m).</summary>
    public const float EntranceStepM = 0.25f;

    public const float EntranceStandBackM = 0.6f;

    /// <summary>입구 구멍 한가운데에서 동굴 바닥을 찾는 깊이 (m). 통로 반지름의 위 한계(3 m)의 두 배보다 조금 깊게.</summary>
    public const float EntranceFloorSearchM = 8.0f;

    /// <summary>
    /// 낙하 한계: 걷는 플레이어가 지형·주변 지형·동굴 가운데 가장 낮은 면보다 이만큼(m) 아래로 떨어지면 처음 자리로 돌려놓습니다.
    /// 그 아래에는 설 땅이 없으므로, 동굴 바닥이나 깊은 골짜기에 선 플레이어는 되돌려지지 않습니다.
    /// </summary>
    public const float FallMarginM = 100.0f;

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
    /// 불러오는 동안 한 프레임에 쓰는 만들기 시간 (ms). 일 조각(메시 조각, 충돌 조각)을 이 시간이 찰 때까지 이어서 하고
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
        Player.InCaveAt = InCave;
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
        Player.FallLimitYM = LowestSolidY() - FallMarginM;
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

    /// <summary>지금 보고 있는 동굴 입구 번호 (entrances.json 순서, 0 부터). 아직 T 를 누르지 않았으면 −1.</summary>
    public int EntranceIndex => _entranceIndex;

    /// <summary>
    /// step 번째 다음 동굴 입구 앞으로 옮겨 입구 안쪽을 봅니다. 걷는 중이면 입구 아래 가장자리 바로 밖 땅에 세워,
    /// 앞으로 걸으면 굴로 들어가게 합니다(<see cref="EntranceStand"/>). 설 자리가 없는 입구(지표로 열린 구멍이 없는 굴 등)는
    /// 같은 방향으로 건너뜁니다. 날고 있거나 설 자리가 있는 입구가 하나도 없으면 노클립으로 산비탈 밖 공중에서 봅니다.
    /// </summary>
    public void GoToEntrance(int step)
    {
        int n = Baked.EntrancesInside.Count;
        if (n == 0)
        {
            return;
        }
        _entranceIndex = Mathf.PosMod(_entranceIndex + step, n);
        for (int tried = 0; !Player.Noclip && tried < n; tried++)
        {
            if (EntranceStand(_entranceIndex) is (Vector3 feet, Vector3 target, _))
            {
                Player.LookFrom(feet + (Vector3.Up * Player.Head.Position.Y), target);
                Player.Velocity = Vector3.Zero;
                return;
            }
            _entranceIndex = Mathf.PosMod(_entranceIndex + (step < 0 ? -1 : 1), n);
        }
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

    /// <summary>
    /// 걷는 플레이어를 세울 동굴 입구 앞 자리를 찾습니다. 반환: (발 위치, 바라볼 점, 굴 바닥 높이), 못 찾으면 null.
    /// </summary>
    /// <remarks>
    /// 입구 굴은 수평이고, entrances.json 의 inside(굴이 시작하는 샘 칸)와 outside(산비탈 밖 공중 끝)가 같은 높이의 축입니다.
    /// 1. 축을 밖에서 안으로 따라가며 지표가 축 높이에 닿는 곳을 찾습니다. 지표가 굴을 가로지르는 입구 구멍의 한가운데입니다.
    /// 2. 거기서 동굴 충돌체만 맞히는 광선을 아래로 쏘아 굴 바닥 높이를 잽니다.
    /// 3. 밖으로 되돌아가며 지표가 바닥 높이까지 내려오는 곳(입구의 아래 가장자리)을 찾고, 거기서 0.6 m 밖 땅에 세웁니다.
    ///    산비탈이 끝까지 바닥보다 높으면(강가 샘 등) 구멍 한가운데의 굴 바닥에 세웁니다.
    /// 예: 축 높이 121.8 m 인 굴에서 지표가 121.8 m 가 되는 곳의 바닥이 119.8 m 이면, 지표가 119.8 m 까지 내려오는
    /// 산비탈 자리에서 0.6 m 밖에 서서 굴 안쪽을 봅니다.
    /// </remarks>
    public (Vector3 Feet, Vector3 Target, float FloorY)? EntranceStand(int index)
    {
        Vector3 inside = Baked.EntrancesInside[index];
        Vector3 outside = Baked.EntrancesOutside[index];
        var dir = new Vector3(inside.X - outside.X, 0.0f, inside.Z - outside.Z);
        float length = dir.Length();
        if (length < EntranceStepM)
        {
            return null;
        }
        Vector3 u = dir / length;
        Vector3 At(float t) => new(outside.X + (u.X * t), inside.Y, outside.Z + (u.Z * t));
        float tMid = float.NaN;
        for (float t = 0.0f; t <= length; t += EntranceStepM)
        {
            float g = GroundY(At(t));
            if (float.IsFinite(g) && g >= inside.Y)
            {
                tMid = t;
                break;
            }
        }
        if (float.IsNaN(tMid))
        {
            return null;
        }
        float floorY = CaveCollision.FloorBelow(GetWorld3D().DirectSpaceState, At(tMid), EntranceFloorSearchM);
        if (!float.IsFinite(floorY))
        {
            return null;
        }
        Vector3 target = At(tMid + (4.0f * EntranceFloorSearchM));
        target.Y = floorY + Player.Head.Position.Y;
        for (float t = tMid; t >= 0.0f; t -= EntranceStepM)
        {
            float g = GroundY(At(t));
            if (float.IsFinite(g) && g <= floorY)
            {
                Vector3 feet = At(Math.Max(t - EntranceStandBackM, 0.0f));
                float gs = GroundY(feet);
                feet.Y = (float.IsFinite(gs) ? gs : g) + 0.05f;
                return (feet, target, floorY);
            }
        }
        Vector3 onFloor = At(tMid);
        onFloor.Y = floorY + 0.05f;
        return (onFloor, target, floorY);
    }

    /// <summary>
    /// 걸어서 설 수 있는 면 가운데 가장 낮은 높이 (엔진 Y): 회랑 지형, 주변 지형, 동굴 충돌면의 최저점.
    /// 프랙탈 디테일을 켜고 끄면 회랑 지형의 최저점이 몇 m 바뀌지만, 낙하 한계의 여유(FallMarginM)가 그보다 큽니다.
    /// 예: 회랑 최저 0 m, 주변 지형(유역 출구 쪽) 최저 −1,200 m, 동굴 최저 −150 m 이면 −1,200 m 입니다.
    /// </summary>
    public float LowestSolidY()
    {
        float low = float.PositiveInfinity;
        foreach (HeightmapTerrain? t in new[] { Terrain, Baked.GetNodeOrNull<HeightmapTerrain>("Surround") })
        {
            if (t?.Heightmap is HeightmapLoader hm && hm.IsValid())
            {
                low = Math.Min(low, t.ToGlobal(new Vector3(0.0f, hm.Origin.Y + hm.MinHeightM, 0.0f)).Y);
            }
        }
        if (Baked.CaveBody is StaticBody3D cave)
        {
            Transform3D xf = cave.GlobalTransform;
            foreach (CollisionShape3D s in cave.GetChildren().OfType<CollisionShape3D>())
            {
                foreach (Vector3 v in ((ConcavePolygonShape3D)s.Shape).GetFaces())
                {
                    low = Math.Min(low, (xf * v).Y);
                }
            }
        }
        return float.IsFinite(low) ? low : 0.0f;
    }

    /// <summary>
    /// 전역 위치 p 가 땅속 동굴의 빈 곳 안인지: 지표 아래이고, 동굴 충돌면이 p 를 앞면으로 둘러쌉니다 (<see cref="CaveCollision.Inside"/>).
    /// 동굴 충돌체가 없으면 false.
    /// </summary>
    public bool InCave(Vector3 p)
    {
        float ground = GroundY(p);
        return Baked.CaveBody is not null && float.IsFinite(ground) && p.Y < ground
            && CaveCollision.Inside(GetWorld3D().DirectSpaceState, p);
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

    /// <summary>상태 글 맨 위에 띄우는, 노이즈 비교본임을 알리는 줄.</summary>
    public const string NoiseWorldLine = "노이즈 비교본: B-PCG 와 비교하려고 판·침식·물의 흐름 계산 없이 노이즈만으로 만든 땅";

    /// <summary>
    /// 지금 연 회랑이 노이즈 비교본(목차의 generator 가 "noise")인지. B-PCG 가 구운 회랑에는 generator 가 없습니다.
    /// </summary>
    public bool IsNoiseWorld => Json.Str(Baked.Manifest, "generator", "") == Bpcg.NoiseWorld.NoiseCorridor.Generator;

    /// <summary>왼쪽 위 상태 글을 다시 씁니다 (화면 캡처가 찍기 전에도 부름).</summary>
    public void UpdateStatus()
    {
        Vector3 eye = Player.EyePosition();
        var lines = new List<string>();
        if (IsNoiseWorld)
        {
            lines.Add(NoiseWorldLine);
        }
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
