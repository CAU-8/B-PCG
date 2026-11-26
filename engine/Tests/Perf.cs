using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Bpcg.Core;
using Godot;

namespace Bpcg.Engine.Tests;

/// <summary>
/// 회랑 장면의 그리기 비용 재기. 시점 셋마다 그림자와 주변 지형을 켜고 끈 조합을 바꿔 가며, 화면 속도(FPS), 화면 한 장을
/// 그리는 데 걸린 시간, 그린 삼각형 수를 재어 '[그리기]' 기록 줄로 찍습니다.
/// </summary>
/// <remarks>
/// 창이 떠야 그려지므로 --headless 를 쓰지 않고, HOME 을 임시 폴더로 바꿔 돌립니다(screenshots.tscn 과 같음).
/// 재는 동안 키보드·마우스 입력은 받지 않습니다.
/// <c>HOME=$(mktemp -d) godot --path . --disable-vsync --resolution 1920x1080 res://Tests/perf.tscn -- --baked-dir=&lt;폴더&gt; [--perf-seconds=3]</c>.
/// 줄마다 두 값을 씁니다. 'fps' 는 사람이 보는 화면 속도입니다. 맥은 창 화면을 60 Hz 로만 받아 가므로 수직 동기화를 꺼도 60 에서 멈춥니다.
/// '그리기만 한 장' 은 화면에 내보내지 않고 같은 장면을 거듭 그려 잰 한 장의 시간이라 60 fps 보다 빠른 설정끼리도 견줄 수 있습니다.
/// 맥의 Metal 판 Godot 는 GPU 시간을 재 주지 않아 'GPU 시간 없음' 으로 나옵니다.
/// 끝나면 'BPCG_PERF_OK &lt;잰 줄 수&gt;' 를 찍고 끝납니다. 화면이 그려지지 않으면(창이 가려짐, 화면 꺼짐) 'BPCG_PERF_FAIL' 입니다.
/// </remarks>
public partial class Perf : Node
{
    public const string MainScene = "res://scenes/main.tscn";
    public const string SecondsArg = "--perf-seconds=";

    /// <summary>설정을 바꾼 뒤 재기 전에 기다리는 화면 수 (셰이더 컴파일, 그림자 지도, GPU 시간 값이 자리 잡도록).</summary>
    public const int SettleFrames = 30;

    /// <summary>시점·설정 하나를 재는 시간 (s). --perf-seconds 로 바꿈.</summary>
    public const double DefaultSeconds = 3.0;

    private double _seconds = DefaultSeconds;
    private int _lines;

    /// <summary>재는 시점 하나: 이름과 (눈, 바라보는 곳).</summary>
    private sealed record View(string Name, Vector3 Eye, Vector3 Target);

    /// <summary>재는 설정 하나: 이름과, 기본 상태에서 바꿀 내용.</summary>
    private sealed record Setting(string Name, Action<Main> Apply);

    private static readonly Setting[] Settings =
    [
        new("기본", _ => { }),
        new("그림자 끔", m => Sun(m).ShadowEnabled = false),
        new("주변 지형 끔", m => m.Baked.SetLayerVisible("surround", false)),
        new("둘 다 끔", m =>
        {
            Sun(m).ShadowEnabled = false;
            m.Baked.SetLayerVisible("surround", false);
        }),
    ];

    private static DirectionalLight3D Sun(Main main) => main.GetNode<DirectionalLight3D>("Sun");

    /// <summary>설정마다 재기 전에 장면을 처음 상태로 돌립니다.</summary>
    private static void Reset(Main main)
    {
        Sun(main).ShadowEnabled = true;
        main.Baked.ShowAll();
    }

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(SecondsArg, StringComparison.Ordinal)
                && double.TryParse(arg[SecondsArg.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                && s > 0.0)
            {
                _seconds = s;
            }
        }
        _ = RunGuarded();
    }

    private async Task RunGuarded()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Finish(false, $"예외: {e}");
        }
    }

    private SignalAwaiter ProcessFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Run()
    {
        await ProcessFrame();
        GetTree().Root.GuiDisableInput = true;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Godot.Engine.MaxFps = 0;
        var main = GD.Load<PackedScene>(MainScene).Instantiate<Main>();
        GetTree().Root.AddChild(main);
        main.SetProcessUnhandledInput(false);
        main.Hud.SetProcessUnhandledInput(false);
        // 회랑은 다른 스레드와 여러 프레임에 나눠 불러옵니다. 다 불러온 뒤 플레이어를 멈춥니다.
        while (!main.IsLoaded)
        {
            await ProcessFrame();
        }
        main.Player.SetProcessUnhandledInput(false);
        main.Player.SetPhysicsProcess(false);
        FrameStats.EnableGpuTiming(GetViewport());
        BakedLayers baked = main.Baked;
        if (!baked.Loaded)
        {
            Finish(false, "구운 묶음이 없습니다 (--baked-dir=<회랑 폴더>)");
            return;
        }
        var sun = main.GetNode<DirectionalLight3D>("Sun");
        Window window = GetWindow();
        GD.Print($"[그리기] 기기: {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetCurrentRenderingDriverName()}), "
            + $"창 {window.Size.X}×{window.Size.Y} px, 화면 배율 {UiScale.ScreenFactor(window):0.##}, 수직 동기화 끔, "
            + $"그림자 거리 {sun.DirectionalShadowMaxDistance:F0} m, 굽기 폴더 {baked.BakedDir}");
        GD.Print($"[그리기] 회랑 장면 불러오기 끝: 지형 메시 노드 {LogText.N(main.Terrain.GetNode("Mesh").GetChildCount())}개, "
            + $"가장 긴 프레임 {main.LongestLoadFrameMs.ToString("F0", CultureInfo.InvariantCulture)} ms, "
            + $"{main.LoadSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");

        List<View> views = Views(main);
        // 1. 모든 시점·설정을 한 번씩 그려, Godot 가 뒤에서 하는 셰이더 준비(파이프라인 컴파일)를 먼저 끝냅니다.
        //    준비가 끝나기 전에는 느린 공용 셰이더로 그려서 처음 잰 값이 부풀기 때문입니다.
        ulong warmStart = Time.GetTicksMsec();
        long compiled = PipelineCompilations();
        foreach (View view in views)
        {
            main.Player.LookFrom(view.Eye, view.Target);
            foreach (Setting setting in Settings)
            {
                Reset(main);
                setting.Apply(main);
                await Frames(WarmupFrames);
            }
        }
        Reset(main);
        for (int round = 0; round < WarmupMaxRounds; round++)
        {
            long now = PipelineCompilations();
            await Frames(WarmupFrames);
            if (PipelineCompilations() == now)
            {
                break;
            }
        }
        GD.Print($"[그리기] 미리 그리기 끝: 셰이더 준비 {LogText.N(PipelineCompilations() - compiled)}번, "
            + $"{((Time.GetTicksMsec() - warmStart) / 1000.0).ToString("F1", CultureInfo.InvariantCulture)} s");

        // 2. 잽니다.
        foreach (View view in views)
        {
            main.Player.LookFrom(view.Eye, view.Target);
            foreach (Setting setting in Settings)
            {
                Reset(main);
                setting.Apply(main);
                if (!await Measure(view.Name, setting.Name))
                {
                    return;
                }
            }
        }
        Reset(main);
        Finish(true, $"{_lines}");
    }

    /// <summary>시작 시점, 회랑 서쪽 끝에서 동쪽으로 회랑 전체를 따라 본 시점, 하늘에서 비스듬히 내려다본 시점.</summary>
    private static List<View> Views(Main main)
    {
        Player player = main.Player;
        Vector3 eye0 = player.EyePosition();
        Vector3 ahead = -player.Head.GlobalBasis.Z;
        HeightmapTerrain terrain = main.Terrain;
        Vector3 c = terrain.ToGlobal(terrain.Heightmap!.Center());
        Rect2 r = main.Baked.CorridorRect;
        bool alongX = r.Size.X >= r.Size.Y;
        Vector3 west = alongX
            ? new Vector3(r.Position.X + 50.0f, 0.0f, c.Z)
            : new Vector3(c.X, 0.0f, r.End.Y - 50.0f);
        Vector3 east = alongX
            ? new Vector3(r.End.X - 50.0f, 0.0f, c.Z)
            : new Vector3(c.X, 0.0f, r.Position.Y + 50.0f);
        west.Y = Ground(main, west) + 1.6f;
        east.Y = Ground(main, east);
        return
        [
            new("시작 시점 (회랑 가운데 위 40 m)", eye0, eye0 + (ahead * 100.0f)),
            new("회랑 끝에서 회랑 전체를 따라 봄 (눈높이)", west, east),
            new("하늘에서 회랑과 주변 지형", new Vector3(c.X + 1800.0f, c.Y + 1600.0f, r.End.Y + 1800.0f), c),
        ];
    }

    private static float Ground(Main main, Vector3 p)
    {
        float g = main.GroundY(p);
        return float.IsFinite(g) ? g : p.Y;
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ProcessFrame();
        }
    }

    /// <summary>Godot 가 지금까지 한 셰이더 준비(그리기 파이프라인 컴파일) 횟수.</summary>
    private static long PipelineCompilations() =>
        (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsCanvas)
        + (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsMesh)
        + (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSurface)
        + (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsDraw)
        + (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSpecialization);

    /// <summary>설정을 바꾼 화면이 자리 잡을 때까지 기다린 뒤 _seconds 동안 잽니다. 화면이 그려지지 않으면 false.</summary>
    private async Task<bool> Measure(string view, string setting)
    {
        await Frames(SettleFrames);
        long compiledBefore = PipelineCompilations();
        Viewport viewport = GetViewport();
        var gpu = new List<double>();
        long triangles = 0;
        long shadow = 0;
        long calls = 0;
        int frames = 0;
        ulong start = Time.GetTicksUsec();
        while (Time.GetTicksUsec() - start < (ulong)(_seconds * 1e6))
        {
            await ProcessFrame();
            FrameStats.Sample s = FrameStats.Read(viewport);
            frames++;
            if (s.GpuMs > 0.0)
            {
                gpu.Add(s.GpuMs);
            }
            triangles = Math.Max(triangles, s.Triangles);
            shadow = Math.Max(shadow, s.ShadowTriangles);
            calls = Math.Max(calls, s.DrawCalls);
        }
        double elapsedS = (Time.GetTicksUsec() - start) / 1e6;
        if (triangles == 0)
        {
            Finish(false, $"화면이 그려지지 않았습니다 ({view} · {setting}, 화면 {frames}장, GPU 시간 {gpu.Count}번 읽힘). "
                + "창이 가려졌거나 화면이 꺼져 있으면 Godot 가 그리지 않습니다");
            return false;
        }
        gpu.Sort();
        string gpuText = gpu.Count > 0
            ? $"GPU 한 장 가운데값 {gpu[gpu.Count / 2].ToString("F1", CultureInfo.InvariantCulture)} ms"
            : "GPU 시간 없음";
        double drawMs = DrawOnlyMs();
        long compiledDuring = PipelineCompilations() - compiledBefore;
        string compiledText = compiledDuring > 0 ? $", 재는 동안 셰이더 준비 {LogText.N(compiledDuring)}번 (값이 부풀었을 수 있음)" : "";
        GD.Print($"[그리기] {view} · {setting}: {(frames / elapsedS).ToString("F1", CultureInfo.InvariantCulture)} fps, "
            + $"화면에 내보내지 않고 그리기만 한 장 {drawMs.ToString("F1", CultureInfo.InvariantCulture)} ms "
            + $"(≈ {(1000.0 / drawMs).ToString("F0", CultureInfo.InvariantCulture)} fps), {gpuText}, "
            + $"삼각형 {LogText.N(triangles)} + 그림자 {LogText.N(shadow)}, 그리기 호출 {LogText.N(calls)}, "
            + $"{elapsedS.ToString("F1", CultureInfo.InvariantCulture)} s{compiledText}");
        _lines++;
        return true;
    }

    /// <summary>
    /// 화면에 내보내지 않고 같은 장면을 묶음마다 DrawOnlyFrames 장씩 그려, 한 장에 걸린 시간 (ms) 을 돌려줍니다.
    /// 맥은 창 화면을 60 Hz 로만 받아 가서 fps 가 60 에서 멈추므로, 그보다 빠른 설정끼리는 이 값으로 견줍니다.
    /// GPU 는 앞선 화면을 그리는 동안 다음 화면을 받아 두므로, 몇 장을 먼저 그려 줄을 채운 뒤 잽니다.
    /// 다른 프로그램이 CPU 를 나눠 쓰면 그 묶음만 느려지므로, 묶음 DrawOnlyBatches 개 가운데 가장 빠른 값을 씁니다.
    /// </summary>
    private static double DrawOnlyMs()
    {
        for (int i = 0; i < DrawOnlyWarmup; i++)
        {
            RenderingServer.ForceDraw(false);
        }
        double best = double.MaxValue;
        for (int batch = 0; batch < DrawOnlyBatches; batch++)
        {
            ulong start = Time.GetTicksUsec();
            for (int i = 0; i < DrawOnlyFrames; i++)
            {
                RenderingServer.ForceDraw(false);
            }
            best = Math.Min(best, (Time.GetTicksUsec() - start) / 1000.0 / DrawOnlyFrames);
        }
        return best;
    }

    /// <summary>미리 그리기에서 시점·설정마다 그리는 화면 수와, 셰이더 준비가 멈추기를 기다리는 최대 횟수.</summary>
    public const int WarmupFrames = 20;

    public const int WarmupMaxRounds = 60;

    /// <summary>그리기만 재기: 줄을 채우는 화면 수, 묶음 하나의 화면 수, 묶음 수.</summary>
    public const int DrawOnlyWarmup = 5;

    public const int DrawOnlyFrames = 12;

    public const int DrawOnlyBatches = 6;

    private void Finish(bool ok, string detail)
    {
        GD.Print(ok ? $"BPCG_PERF_OK {detail}" : $"BPCG_PERF_FAIL: {detail}");
        GetTree().Quit(ok ? 0 : 1);
    }
}
