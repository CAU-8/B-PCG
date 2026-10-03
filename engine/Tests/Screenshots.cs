using System;
using System.Threading.Tasks;
using Godot;

namespace Bpcg.Engine.Tests;

/// <summary>
/// 화면 캡처. 회랑 장면을 여러 시점·레이어 조합으로, 지구본 장면을 몇 가지 레이어로 찍어 PNG 로 남깁니다 (보고서, 리뷰용).
/// </summary>
/// <remarks>
/// 창이 떠야 그려지므로 --headless 를 쓰지 않습니다. 편집기 설정을 덮어쓰지 않게 HOME 을 임시 폴더로 바꿔 돌립니다.
/// 찍는 동안에는 키보드·마우스 입력을 받지 않습니다 (창이 초점을 가져가 다른 곳에 치던 키가 들어오는 것을 막음).
/// <c>HOME=$(mktemp -d) godot --path . --resolution 1600x900 res://Tests/screenshots.tscn -- --shots-dir=/절대/경로 --baked-dir=&lt;폴더&gt;</c>.
/// 끝나면 'BPCG_SHOTS_OK &lt;장 수&gt;' 를 찍고 끝납니다.
/// </remarks>
public partial class Screenshots : Node
{
    public const string MainScene = "res://scenes/main.tscn";
    public const string GlobeScene = "res://scenes/globe.tscn";
    public const string ArgPrefix = "--shots-dir=";

    /// <summary>한 장을 찍기 전에 기다리는 프레임 수 (셰이더 컴파일, 그림자, 안개가 자리 잡도록).</summary>
    public const int SettleFrames = 12;

    private string _dir = "";
    private int _count;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(ArgPrefix, StringComparison.Ordinal))
            {
                _dir = arg[ArgPrefix.Length..];
            }
        }
        if (_dir.Length == 0)
        {
            GD.Print("BPCG_SHOTS_FAIL: --shots-dir=<폴더> 가 필요합니다");
            GetTree().Quit(1);
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(_dir);
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
            GD.Print($"BPCG_SHOTS_FAIL: 예외: {e}");
            GetTree().Quit(1);
        }
    }

    private SignalAwaiter ProcessFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Run()
    {
        await ProcessFrame();
        // 창이 키보드 초점을 가져가므로, 찍는 동안 들어온 키·마우스 입력이 시점이나 장면을 바꾸지 않게 막습니다.
        GetTree().Root.GuiDisableInput = true;
        var main = GD.Load<PackedScene>(MainScene).Instantiate<Main>();
        GetTree().Root.AddChild(main);
        main.SetProcessUnhandledInput(false);
        main.Player.SetProcessUnhandledInput(false);
        main.Player.SetPhysicsProcess(false);
        BakedLayers baked = main.Baked;
        Player player = main.Player;
        HeightmapTerrain terrain = main.Terrain;
        if (!baked.Loaded)
        {
            GD.Print("BPCG_SHOTS_FAIL: 구운 묶음이 없습니다");
            GetTree().Quit(1);
            return;
        }
        Vector3 c = terrain.ToGlobal(terrain.Heightmap!.Center());
        Rect2 r = baked.CorridorRect;

        // 1. 회랑 위 40 m, 북쪽을 봄 (시작 시점)
        await Shot("01_start", main);
        // 2. 높은 곳에서 회랑과 주변 25 m 지형
        player.LookFrom(new Vector3(c.X + 1800.0f, c.Y + 1600.0f, r.End.Y + 1800.0f), c);
        await Shot("02_aerial", main);
        // 3. 같은 시점, 지질도 색
        main.CycleColorMode();
        await Shot("03_aerial_geology", main);
        main.CycleColorMode();
        // 4. 지층 단면: 지표 위 60 m 에서, 눈앞 50 m 에 동서로 선 면을 잘라 내려다봄
        var eye = new Vector3(c.X, Ground(main, c) + 60.0f, c.Z + 50.0f);
        player.LookFrom(eye, new Vector3(c.X, Ground(main, c) - 80.0f, c.Z - 40.0f));
        baked.SetSection(true, new Vector3(c.X, 0.0f, c.Z), Vector3.Back);
        await Shot("04_section", main);
        // 4b. 같은 단면을 동굴이 지나는 곳에서
        if (baked.EntrancesInside.Count > 0)
        {
            Vector3 a = baked.EntrancesInside[baked.EntrancesInside.Count / 2];
            float g = Ground(main, a);
            player.LookFrom(new Vector3(a.X + 20.0f, g + 40.0f, a.Z + 70.0f), new Vector3(a.X, a.Y - 10.0f, a.Z));
            baked.SetSection(true, new Vector3(a.X, 0.0f, a.Z + 2.0f), Vector3.Back);
            await Shot("04b_section_cave", main);
        }
        baked.SetSection(false);
        // 5. 동굴만 보기 (위에서 비스듬히)
        if (baked.LayerIds().Contains("caves"))
        {
            baked.Solo("caves");
            player.LookFrom(new Vector3(c.X + 300.0f, c.Y + 250.0f, c.Z + 300.0f), new Vector3(c.X, c.Y - 50.0f, c.Z));
            await Shot("05_caves_solo", main);
            baked.ShowAll();
        }
        // 6. 물과 지하수면만
        baked.Solo("water");
        baked.SetLayerVisible("water_table", true);
        player.LookFrom(new Vector3(c.X + 500.0f, c.Y + 400.0f, c.Z + 600.0f), c);
        await Shot("06_water_and_table", main);
        baked.ShowAll();
        // 7. 첫 동굴 입구 앞
        if (baked.EntrancesInside.Count > 0)
        {
            main.GoToEntrance(1);
            await Shot("07_entrance", main);
        }
        // 8·9. 프랙탈 디테일 켬·끔 (같은 시점, 비탈 가까이)
        if (baked.LayerIds().Contains("fractal_detail"))
        {
            var p = new Vector3(c.X + 150.0f, 0.0f, c.Z - 400.0f);
            float g = Ground(main, p);
            player.LookFrom(new Vector3(p.X - 35.0f, g + 18.0f, p.Z + 35.0f), new Vector3(p.X, g - 5.0f, p.Z));
            baked.SetLayerVisible("fractal_detail", true);
            await Shot("08_detail_on", main);
            baked.SetLayerVisible("fractal_detail", false);
            await Shot("09_detail_off", main);
            baked.SetLayerVisible("fractal_detail", true);
        }
        // 10~. 지구본 장면 (행성 전체): 고도, 판, 강수, 히어로로 날아가기
        await GlobeShots(main);
        GD.Print($"BPCG_SHOTS_OK {_count}");
        GetTree().Quit(0);
    }

    private static float Ground(Main main, Vector3 p)
    {
        float g = main.GroundY(p);
        return float.IsFinite(g) ? g : p.Y;
    }

    private async Task Shot(string name, Main main)
    {
        for (int i = 0; i < SettleFrames; i++)
        {
            await ProcessFrame();
        }
        main.UpdateStatus();
        await ProcessFrame();
        Save(name);
    }

    private void Save(string name)
    {
        Image image = GetTree().Root.GetViewport().GetTexture().GetImage();
        string path = _dir.PathJoin(name + ".png");
        image.SavePng(path);
        _count++;
        GD.Print($"찍음: {path}");
    }

    private async Task GlobeShots(Main main)
    {
        if (!ResourceLoader.Exists(GlobeScene))
        {
            return;
        }
        main.QueueFree();
        await ProcessFrame();
        var globeMain = GD.Load<PackedScene>(GlobeScene).Instantiate<GlobeMain>();
        GetTree().Root.AddChild(globeMain);
        globeMain.SetProcessUnhandledInput(false);
        globeMain.Camera.SetProcessUnhandledInput(false);
        await ProcessFrame();
        if (globeMain.Globe.Data is null || globeMain.Globe.Data.Missing)
        {
            GD.Print("지구본 자료가 없어 지구본 장면은 찍지 않습니다");
            return;
        }
        await ShotPlain("10_globe_elevation");
        foreach ((int index, string name) in new[] { (2, "11_globe_plates"), (8, "12_globe_precip") })
        {
            globeMain.SelectFieldIndex(index);
            await ShotPlain(name);
        }
        globeMain.SelectFieldIndex(0);
        globeMain.Camera.FlyToHero();
        for (int i = 0; i < 90; i++)
        {
            await ProcessFrame();
        }
        await ShotPlain("13_globe_hero");
    }

    /// <summary>지구본 장면 캡처 (회랑 상태 글을 다시 쓰지 않음).</summary>
    private async Task ShotPlain(string name)
    {
        for (int i = 0; i < SettleFrames; i++)
        {
            await ProcessFrame();
        }
        Save(name);
    }
}
