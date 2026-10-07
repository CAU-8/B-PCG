using System;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 창 크기와 화면 글자·패널(HUD) 크기.
/// </summary>
/// <remarks>
/// 맥 레티나처럼 화면 배율이 2 인 곳에서는 Godot 가 창(1152×648)과 15 px 글자를 픽셀로 그려
/// 둘 다 절반 크기로 보입니다. 그래서
/// (1) 처음 뜰 때 창을 화면 배율만큼 키우고(<see cref="FitWindow"/>, 크기를 따로 주지 않았을 때만),
/// (2) 창의 ContentScaleFactor 로 2D(HUD)만 키우고 3D 는 원래 해상도로 그립니다.
/// 배율 = 자동 배율 × 사용자 배율입니다. 자동 배율은 화면 배율(맥·웨이랜드는 OS 배율, 윈도는 DPI ÷ 96)이되,
/// 창이 작으면 HUD 가 서로 겹치지 않게 <see cref="MinCanvas"/> 를 넘지 않을 만큼 줄입니다(1 보다 작게는 줄이지 않음).
/// 사용자 배율은 [-]·[=] 키로 10 % 씩 바꾸고 user://settings.cfg 에 남겨, 장면을 바꾸거나 다시 실행해도 그대로 씁니다.
/// </remarks>
public static class UiScale
{
    public const float Step = 0.1f;
    public const float UserMin = 0.6f;
    public const float UserMax = 2.5f;

    /// <summary>창을 키울 때 화면의 쓸 수 있는 영역에서 차지할 수 있는 최대 비율.</summary>
    public const float MaxScreenFraction = 0.9f;

    public const string SettingsPath = "user://settings.cfg";
    private const string SettingsSection = "hud";
    private const string SettingsKey = "scale";

    /// <summary>배율을 바꾼 뒤 상태 줄에 알려 주는 시간 [ms].</summary>
    public const ulong NoticeMs = 2000;

    /// <summary>
    /// 자동 배율에서 HUD 가 쓰는 2D 화면이 적어도 이만큼(배율 1 의 px)은 되게 합니다.
    /// 레이어 패널(약 290×420)과 아래 도움말(6 줄)이 겹치지 않는 크기입니다.
    /// </summary>
    public static readonly Vector2 MinCanvas = new(1024.0f, 600.0f);

    private static float User = -1.0f; // 음수 = 아직 설정 파일을 읽지 않음
    private static ulong ChangedAtMs;
    private static bool HasChanged;
    private static Window? Watched;
    private static bool WindowFitted;

    /// <summary>창 크기를 한 번 맞추고 HUD 배율을 적용합니다. 장면의 _Ready 에서 부릅니다. 반환: 적용한 배율.</summary>
    public static float Apply(Window? window)
    {
        if (window is null)
        {
            return 1.0f;
        }
        if (!WindowFitted)
        {
            WindowFitted = true;
            FitWindow(window);
        }
        if (Watched != window)
        {
            // 창 크기를 바꾸거나 다른 화면으로 옮기면 다시 맞춥니다.
            Watched = window;
            window.SizeChanged += () => Apply(window);
            window.DpiChanged += () => Apply(window);
        }
        float factor = AutoFactor(window) * UserFactor();
        if (!Mathf.IsEqualApprox(window.ContentScaleFactor, factor))
        {
            window.ContentScaleFactor = factor;
        }
        return factor;
    }

    /// <summary>
    /// 창이 프로젝트 기본 크기(픽셀) 그대로면 화면 배율만큼 키워 가운데에 둡니다. 화면 없이(headless) 돌 때,
    /// 전체 화면일 때, --resolution 등으로 크기를 줬을 때는 그대로 둡니다. 반환: 크기를 바꿨으면 true.
    /// </summary>
    public static bool FitWindow(Window window)
    {
        if (DisplayServer.GetName() == "headless" || window.Mode != Window.ModeEnum.Windowed)
        {
            return false;
        }
        var baseSize = new Vector2I(
            ProjectSettings.GetSetting("display/window/size/viewport_width", 1152).AsInt32(),
            ProjectSettings.GetSetting("display/window/size/viewport_height", 648).AsInt32());
        float scale = ScreenFactor(window);
        if (window.Size != baseSize || scale <= 1.0f)
        {
            return false;
        }
        Vector2 usable = DisplayServer.ScreenGetUsableRect(window.CurrentScreen).Size;
        Vector2 target = (Vector2)baseSize * scale;
        float shrink = Math.Min(1.0f, Math.Min(usable.X * MaxScreenFraction / target.X, usable.Y * MaxScreenFraction / target.Y));
        window.Size = (Vector2I)(target * shrink).Round();
        window.MoveToCenter();
        return true;
    }

    /// <summary>화면 배율 (1~4).</summary>
    public static float ScreenFactor(Window window)
    {
        int screen = window.CurrentScreen;
        float s = DisplayServer.ScreenGetScale(screen);
        if (OS.GetName() == "Windows")
        {
            // 윈도는 ScreenGetScale 이 늘 1 이라 DPI 로 셈 (96 DPI = 100 %).
            s = DisplayServer.ScreenGetDpi(screen) / 96.0f;
        }
        return Math.Clamp(s, 1.0f, 4.0f);
    }

    /// <summary>자동 배율: 화면 배율이되, 2D 화면이 MinCanvas 보다 작아지지 않게 줄임 (1 이상).</summary>
    public static float AutoFactor(Window window)
    {
        Vector2 size = window.Size;
        float fit = Math.Min(size.X / MinCanvas.X, size.Y / MinCanvas.Y);
        return Math.Max(1.0f, Math.Min(ScreenFactor(window), fit));
    }

    /// <summary>사용자 배율 (UserMin~UserMax, 기본 1).</summary>
    public static float UserFactor()
    {
        if (User < 0.0f)
        {
            User = 1.0f;
            var cfg = new ConfigFile();
            if (cfg.Load(SettingsPath) == Error.Ok)
            {
                User = Math.Clamp((float)cfg.GetValue(SettingsSection, SettingsKey, 1.0).AsDouble(), UserMin, UserMax);
            }
        }
        return User;
    }

    /// <summary>사용자 배율을 steps 칸(10 %) 바꾸고 저장한 뒤 창에 적용합니다. 반환: 새 사용자 배율.</summary>
    public static float Change(Window window, int steps)
    {
        User = Math.Clamp(Mathf.Snapped(UserFactor() + (Step * steps), Step), UserMin, UserMax);
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // 없으면 빈 파일에 씀
        cfg.SetValue(SettingsSection, SettingsKey, User);
        cfg.Save(SettingsPath);
        ChangedAtMs = Time.GetTicksMsec();
        HasChanged = true;
        Apply(window);
        return User;
    }

    /// <summary>[-]·[=] (숫자판 -·+) 키면 배율을 바꾸고 true 를 돌려줍니다.</summary>
    public static bool HandleKey(Window window, Key key)
    {
        switch (key)
        {
            case Key.Equal:
            case Key.KpAdd:
                Change(window, 1);
                return true;
            case Key.Minus:
            case Key.KpSubtract:
                Change(window, -1);
                return true;
            default:
                return false;
        }
    }

    /// <summary>배율을 바꾼 직후 잠깐 상태 줄에 보일 글. 그 밖에는 빈 글.</summary>
    public static string Notice()
    {
        if (!HasChanged || Time.GetTicksMsec() - ChangedAtMs > NoticeMs)
        {
            return "";
        }
        return $"글자·패널 크기 {Mathf.RoundToInt(UserFactor() * 100.0f)} % ([-] 작게, [=] 크게, "
            + $"{Mathf.RoundToInt(UserMin * 100.0f)}~{Mathf.RoundToInt(UserMax * 100.0f)} %, 다음 실행에도 씀)";
    }
}
