using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine.Tests;

/// <summary>
/// 회랑 진입 시간 재기. 회랑 장면을 트리에 붙인 때부터 걸어 다닐 수 있게 될 때까지 걸린 시간과, 그동안 화면이 가장 길게
/// 멈춘 프레임의 길이를 잽니다.
/// </summary>
/// <remarks>
/// 실행 (engine 폴더에서): <c>HOME=$(mktemp -d) godot --headless --path . res://Tests/load_timing.tscn -- --baked-dir=&lt;회랑 폴더&gt;</c>.
/// 진입마다 'BPCG_LOAD_TIMING N번째 진입 … ms, 가장 긴 프레임 … ms, 프레임 …개' 를 찍습니다 (<see cref="Entries"/>번).
/// 프랙탈 디테일 레이어가 있으면 첫 진입을 다 불러오고 <see cref="IdleBeforeToggleS"/> 초 뒤에 처음 끄고 다시 켜는 데
/// 걸린 시간도 찍습니다.
/// 화면 없이(--headless) 돌리면 텍스처와 메시를 그래픽 카드로 올리는 시간이 빠지므로, 창을 띄운 실행보다 짧게 나옵니다.
/// </remarks>
public partial class LoadTiming : Node
{
    public const string MainScene = "res://scenes/main.tscn";

    /// <summary>재기 전에 기다리는 프레임 수 (엔진이 처음 몇 프레임에 하는 일을 빼려고).</summary>
    public const int WarmupFrames = 5;

    /// <summary>이보다 오래 걸리면 실패로 끝냅니다 (ms).</summary>
    public const ulong TimeoutMs = 300000;

    /// <summary>다 불러온 뒤 프랙탈 디테일을 끄기 전에 기다리는 시간 (s). 그동안 기본 지형을 다른 스레드에서 준비합니다.</summary>
    public const double IdleBeforeToggleS = 3.0;

    /// <summary>
    /// 같은 프로세스에서 회랑에 들어가는 횟수. 첫 진입에는 C# 코드 컴파일과 글꼴 같은 처음 한 번 일이 섞이고, 두 번째는
    /// 지구본(M)에서 회랑으로 돌아올 때와 같습니다.
    /// </summary>
    public const int Entries = 2;

    public override void _Ready() => _ = RunGuarded();

    private async Task RunGuarded()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            GD.Print($"BPCG_LOAD_TIMING_FAIL: 예외: {e}");
            GetTree().Quit(1);
        }
    }

    private SignalAwaiter ProcessFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    /// <summary>회랑 장면이 다 불러와져 걸어 다닐 수 있는지.</summary>
    private static bool IsReady(Main main) => main.IsLoaded;

    private async Task Run()
    {
        for (int i = 0; i < WarmupFrames; i++)
        {
            await ProcessFrame();
        }
        var packed = GD.Load<PackedScene>(MainScene);
        for (int entry = 1; entry <= Entries; entry++)
        {
            Main? main = await Enter(packed, entry);
            if (main is null)
            {
                return;
            }
            main.QueueFree();
            await ProcessFrame();
            await ProcessFrame();
        }
        GetTree().Quit(0);
    }

    /// <summary>회랑 장면을 한 번 붙여 다 불러올 때까지 재고 한 줄을 찍습니다. 첫 진입이면 프랙탈 디테일 끄기도 잽니다.</summary>
    private async Task<Main?> Enter(PackedScene packed, int entry)
    {
        ulong prevUs = Time.GetTicksUsec();
        ulong startUs = prevUs;
        var main = packed.Instantiate<Main>();
        GetTree().Root.AddChild(main);
        ulong addUs = Time.GetTicksUsec() - startUs;
        ulong maxGapUs = 0;
        int frames = 0;
        do
        {
            await ProcessFrame();
            ulong now = Time.GetTicksUsec();
            maxGapUs = Math.Max(maxGapUs, now - prevUs);
            prevUs = now;
            frames++;
            if ((now - startUs) / 1000 > TimeoutMs)
            {
                GD.Print($"BPCG_LOAD_TIMING_FAIL: {TimeoutMs} ms 안에 회랑을 다 불러오지 못했습니다");
                GetTree().Quit(1);
                return null;
            }
        }
        while (!IsReady(main));
        double totalMs = (prevUs - startUs) / 1000.0;
        string stages = string.Join(", ", main.Baked.LoadMs.Select(kv => $"{kv.Key} {kv.Value}"));
        GD.Print($"회랑: 지형 {main.Terrain.UsedStem}, 레이어 {string.Join(", ", main.Baked.LayerIds())}, "
            + $"동굴 삼각형 {main.Baked.CaveFaces} ({main.Baked.CaveSource})");
        GD.Print($"단계별 시간 (ms): {stages}");
        string toggle = "";
        if (entry == 1 && main.Baked.LayerIds().Contains("fractal_detail"))
        {
            ulong idleStart = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - idleStart < IdleBeforeToggleS * 1000.0)
            {
                await ProcessFrame();
            }
            ulong t = Time.GetTicksUsec();
            main.Baked.SetLayerVisible("fractal_detail", false);
            double offMs = (Time.GetTicksUsec() - t) / 1000.0;
            t = Time.GetTicksUsec();
            main.Baked.SetLayerVisible("fractal_detail", true);
            double onMs = (Time.GetTicksUsec() - t) / 1000.0;
            toggle = $", 디테일 처음 끄기 {offMs:F0} ms, 다시 켜기 {onMs:F0} ms";
        }
        GD.Print($"BPCG_LOAD_TIMING {entry}번째 진입 {totalMs:F0} ms, 장면 붙이기 {addUs / 1000.0:F0} ms, "
            + $"가장 긴 프레임 {maxGapUs / 1000.0:F0} ms, 프레임 {frames}개{toggle}");
        return main;
    }
}
