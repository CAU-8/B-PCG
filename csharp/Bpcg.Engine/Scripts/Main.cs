using System;
using System.Collections.Generic;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 회랑 장면 (scenes/main.tscn). 지형과 구운 레이어를 불러오고, 플레이어를 세우고, 보기 전환 키를 받습니다.
/// </summary>
/// <remarks>
/// 자식의 _Ready() 가 부모보다 먼저 불리므로 여기서는 지형이 이미 준비돼 있습니다.
/// 키는 <see cref="Hud.HelpText"/> 에 적혀 있습니다. 레이어 숫자 키(1~9, Shift, 0)는 여기서 직접 받습니다.
/// 숫자는 패널 순서(<see cref="BakedLayers.LayerIds"/>)를 따릅니다. 구운 레이어가 다 있으면 1~8 입니다.
/// </remarks>
public partial class Main : Node3D
{
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
        [2] = "지질도 (흙 아래 기반암)",
    };

    /// <summary>M 키로 가는 지구본 장면 (행성 전체).</summary>
    public const string GlobeScene = "res://scenes/globe.tscn";

    /// <summary>N 키로 가는 처음 화면 (행성 만들기·지난 결과 열기).</summary>
    public const string StartScene = "res://scenes/start.tscn";

    private int _colorMode = 1;
    private int _entranceIndex = -1;
    private double _statusTimer;

    public HeightmapTerrain Terrain { get; private set; } = null!;

    public BakedLayers Baked { get; private set; } = null!;

    public Player Player { get; private set; } = null!;

    public Hud Hud { get; private set; } = null!;

    public Light3D Lamp { get; private set; } = null!;

    public override void _Ready()
    {
        Terrain = GetNode<HeightmapTerrain>("Terrain");
        Baked = GetNode<BakedLayers>("Baked");
        Player = GetNode<Player>("Player");
        Hud = GetNode<Hud>("Hud");
        Lamp = GetNode<Light3D>("Player/Head/Camera/Lamp");

        Baked.SectionAnchor = SectionInFront;
        Baked.Setup(Terrain);
        Player.GroundHeightAt = GroundY;
        float clearance = Player.Noclip ? NoclipSpawnClearanceM : 2.0f;
        Player.GlobalPosition = Terrain.SpawnPoint(clearance);
        Hud.BindLayers(Baked);
        Hud.ActionRequested += OnHudAction;
        Baked.TerrainRebuilt += OnTerrainRebuilt;
        UpdateOptionLabels();
        UpdateStatus();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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

    public override void _Process(double delta)
    {
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
            colorLabel = Baked.Loaded ? "자연색" : "높이 무늬 (표본)";
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
        string mode = Player.Noclip ? "날기 (노클립)" : "걷기";
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
                string info = $"땅속 {ground - eye.Y:F0} m";
                if (Baked.Strata is not null)
                {
                    int id = Baked.Strata.IdAt(eye);
                    if (id >= 0)
                    {
                        info += $" · {Baked.Strata.NameOf(id)}";
                    }
                    else if (ground - eye.Y > Baked.Strata.DepthM())
                    {
                        info += $" · 구운 깊이({Baked.Strata.DepthM():F0} m)보다 깊음";
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
                lines.Add($"이 자리 지하수면: 지표 아래 {ground - wt:F1} m");
            }
        }
        if (Baked.SectionOn)
        {
            float d = Math.Abs(Baked.SectionNormal.Dot(eye - Baked.SectionPoint));
            lines.Add($"단면 켜짐 · 눈에서 {d:F0} m ([ ] 로 옮김, X 로 끔)");
        }
        if (Baked.EntrancesInside.Count > 0)
        {
            string at = _entranceIndex < 0 ? "" : $" (지금 {_entranceIndex + 1} 번)";
            lines.Add($"동굴 입구 {Baked.EntrancesInside.Count} 곳{at} · T 로 옮겨 가기");
        }
        Hud.SetStatus(string.Join("\n", lines));
    }
}
