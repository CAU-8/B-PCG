using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bpcg.Core;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 처음 화면 (scenes/start.tscn). 행성 설정·프로필·시드를 골라 행성을 만들고, 지난 결과를 엽니다.
/// </summary>
/// <remarks>
/// '만들기'를 누르면 Bpcg 라이브러리의 <see cref="Runs.All"/> (행성 → 히어로 → 회랑 굽기 → 지구본)을 주 스레드 밖에서
/// 돌려 user://runs/&lt;날짜-시각&gt;-&lt;프로필&gt;-s&lt;시드&gt;/ 에 씁니다. 기록 줄은 화면 아래에 쌓이고, 단계 표
/// (<see cref="StageProgress"/>)로 진행률을 셉니다. 끝나면 <see cref="BakedPaths.RuntimeDir"/> 를 그 실행의 회랑 폴더로 두고
/// 회랑 장면으로 갑니다. 명령줄에 <c>-- --baked-dir=&lt;폴더&gt;</c> 를 주면 이 화면을 건너뛰고 바로 회랑 장면을 엽니다.
/// </remarks>
public partial class StartMenu : Control
{
    public const string CorridorScene = "res://scenes/main.tscn";
    public const string GlobeScene = "res://scenes/globe.tscn";
    public const string DefaultPlanet = "earth";
    public const string DefaultProfile = "tiny";

    /// <summary>화면에 남기는 기록 줄 수.</summary>
    public const int MaxLogLines = 4000;

    private OptionButton _planet = null!;
    private OptionButton _profile = null!;
    private Label _profileNote = null!;
    private SpinBox _seed = null!;
    private CheckBox _flat = null!;
    private CheckBox _openWhenDone = null!;
    private Button _generate = null!;
    private ProgressBar _progress = null!;
    private Label _stage = null!;
    private RichTextLabel _log = null!;
    private ItemList _runs = null!;
    private Button _openCorridor = null!;
    private Button _openGlobe = null!;
    private Label _runsNote = null!;
    private List<EnginePaths.RunEntry> _runEntries = [];

    private readonly ConcurrentQueue<string> _lines = new();
    private Task<string>? _task;
    private StageProgress? _stages;
    private long _startedAt;
    private int _logLines;
    private bool _quitWhenDone;

    /// <summary>생성이 도는 중인지.</summary>
    public bool IsGenerating => _task is not null && !_task.IsCompleted;

    public override void _Ready()
    {
        if (BakedPaths.CommandLineDir() is not null)
        {
            // 명령줄로 굽기 폴더를 받았으면 처음 화면을 건너뜁니다 (예전 engine/ 과 같은 실행 방법).
            Callable.From(() => GetTree().ChangeSceneToFile(CorridorScene)).CallDeferred();
            return;
        }
        BuildUi();
        if (!EnginePaths.Configure())
        {
            AppendLog("[오류] " + EnginePaths.Error);
            _generate.Disabled = true;
        }
        FillOptions();
        RefreshRuns();
        ApplyCommandLine();
    }

    /// <summary>
    /// 명령줄로 화면을 몹니다 (시험·자동 실행): <c>-- --generate [--planet=earth] [--profile=tiny] [--seed=0] [--flat]
    /// [--quit-when-done]</c>. --quit-when-done 이면 끝나고 'BPCG_GENERATE_OK &lt;실행 폴더&gt;' 또는
    /// 'BPCG_GENERATE_FAIL: &lt;까닭&gt;' 을 찍고 종료 코드 0/1 로 끝냅니다.
    /// </summary>
    private void ApplyCommandLine()
    {
        bool generate = false;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            string[] kv = arg.Split('=', 2);
            string value = kv.Length > 1 ? kv[1] : "";
            switch (kv[0])
            {
                case "--generate":
                    generate = true;
                    break;
                case "--planet":
                    SelectByText(_planet, value);
                    break;
                case "--profile":
                    SelectByText(_profile, value);
                    OnProfileSelected(_profile.Selected);
                    break;
                case "--seed":
                    _seed.Value = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--flat":
                    _flat.ButtonPressed = true;
                    break;
                case "--quit-when-done":
                    _quitWhenDone = true;
                    _openWhenDone.ButtonPressed = false;
                    break;
            }
        }
        if (generate)
        {
            StartGeneration();
            if (_task is null && _quitWhenDone)
            {
                GD.Print("BPCG_GENERATE_FAIL: 생성을 시작하지 못했습니다 (설정 폴더?)");
                GetTree().Quit(1);
            }
        }
    }

    private static void SelectByText(OptionButton option, string text)
    {
        for (int i = 0; i < option.ItemCount; i++)
        {
            if (option.GetItemText(i) == text)
            {
                option.Select(i);
                return;
            }
        }
        GD.PushError($"'{text}' 를 고를 수 없습니다");
    }

    public override void _Process(double delta)
    {
        if (_stages is null)
        {
            return;
        }
        while (_lines.TryDequeue(out string? line))
        {
            AppendLog(line);
            _stages.Feed(line);
        }
        double elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        _progress.Value = 100.0 * _stages.Fraction;
        _stage.Text = $"{_stages.Current} · {StageProgress.FormatSeconds(elapsed)}";
        if (_task is not null && _task.IsCompleted)
        {
            FinishRun(elapsed);
        }
    }

    /// <summary>골라 둔 설정으로 행성을 만듭니다 (이미 도는 중이면 아무것도 하지 않음).</summary>
    public void StartGeneration()
    {
        if (IsGenerating || EnginePaths.ConfigsDir is null)
        {
            return;
        }
        string planet = _planet.GetItemText(_planet.Selected);
        string profile = _profile.GetItemText(_profile.Selected);
        long seed = (long)_seed.Value;
        bool flat = _flat.ButtonPressed;
        string runDir = Path.Combine(EnginePaths.RunsRoot(), $"{DateTime.Now:yyyyMMdd-HHmmss}-{profile}-s{seed}");
        _log.Clear();
        _logLines = 0;
        AppendLog($"[엔진] 만들기: 행성 {planet}, 프로필 {profile}, 시드 {seed}{(flat ? ", 평면 히어로" : "")}");
        AppendLog($"[엔진] 결과 폴더: {runDir}");
        _stages = new StageProgress(flat);
        _startedAt = Stopwatch.GetTimestamp();
        SetBusy(true);
        _task = Task.Run(() =>
        {
            Config cfg = Runs.WithSeed(Config.LoadConfig(planet, profile), seed);
            Pipeline.ApplyThreads(cfg);
            return Runs.All(cfg, runDir, flat, null, line => _lines.Enqueue(line));
        });
    }

    private void FinishRun(double elapsed)
    {
        Task<string> task = _task!;
        _task = null;
        while (_lines.TryDequeue(out string? line))
        {
            AppendLog(line);
        }
        SetBusy(false);
        if (task.IsFaulted)
        {
            Exception e = task.Exception!.GetBaseException();
            AppendLog($"[오류] {e.GetType().Name}: {e.Message}");
            GD.PushError($"생성 실패: {e}");
            _stage.Text = $"실패 · {StageProgress.FormatSeconds(elapsed)}";
            _stages = null;
            if (_quitWhenDone)
            {
                GD.Print($"BPCG_GENERATE_FAIL: {e.Message}");
                GetTree().Quit(1);
            }
            return;
        }
        _stages!.Finish();
        _progress.Value = 100.0;
        _stage.Text = $"끝 · {StageProgress.FormatSeconds(elapsed)}";
        _stages = null;
        string runDir = task.Result;
        BakedPaths.RuntimeDir = Path.Combine(runDir, Runs.CorridorDir);
        RefreshRuns();
        AppendLog($"[엔진] 다 만들었습니다 ({StageProgress.FormatSeconds(elapsed)}). 회랑 장면에서 M 을 누르면 지구본이 나옵니다");
        if (_quitWhenDone)
        {
            GD.Print($"BPCG_GENERATE_OK {runDir}");
            GetTree().Quit(0);
            return;
        }
        if (_openWhenDone.ButtonPressed)
        {
            GetTree().ChangeSceneToFile(CorridorScene);
        }
    }

    private void SetBusy(bool busy)
    {
        _generate.Disabled = busy;
        _generate.Text = busy ? "만드는 중…" : "행성 만들기";
        _planet.Disabled = busy;
        _profile.Disabled = busy;
        _seed.Editable = !busy;
        _flat.Disabled = busy;
        _openCorridor.Disabled = busy || _runs.GetSelectedItems().Length == 0;
        _openGlobe.Disabled = busy || !SelectedHasGlobe();
    }

    private void OpenSelected(string scene)
    {
        int[] sel = _runs.GetSelectedItems();
        if (IsGenerating || sel.Length == 0 || sel[0] >= _runEntries.Count)
        {
            return;
        }
        BakedPaths.RuntimeDir = _runEntries[sel[0]].CorridorDir;
        GetTree().ChangeSceneToFile(scene);
    }

    private bool SelectedHasGlobe()
    {
        int[] sel = _runs.GetSelectedItems();
        return sel.Length > 0 && sel[0] < _runEntries.Count && _runEntries[sel[0]].HasGlobe;
    }

    private void RefreshRuns()
    {
        _runEntries = EnginePaths.ListRuns();
        _runs.Clear();
        int select = -1;
        for (int i = 0; i < _runEntries.Count; i++)
        {
            EnginePaths.RunEntry r = _runEntries[i];
            string globe = r.HasGlobe ? "" : " · 지구본 없음";
            _runs.AddItem($"{r.Name}  (프로필 {r.Profile}, 시드 {r.Seed}{globe})");
            if (BakedPaths.RuntimeDir is string now && Path.GetFullPath(now) == Path.GetFullPath(r.CorridorDir))
            {
                select = i;
            }
        }
        if (_runEntries.Count > 0)
        {
            _runs.Select(select >= 0 ? select : 0);
        }
        _runsNote.Text = _runEntries.Count == 0
            ? $"아직 만든 행성이 없습니다. 결과는 {EnginePaths.RunsRoot()} 에 쌓입니다"
            : $"결과 폴더: {EnginePaths.RunsRoot()}";
        SetBusy(IsGenerating);
    }

    private void FillOptions()
    {
        _planet.Clear();
        foreach (string name in EnginePaths.ConfigNames("planets"))
        {
            _planet.AddItem(name);
            if (name == DefaultPlanet)
            {
                _planet.Select(_planet.ItemCount - 1);
            }
        }
        _profile.Clear();
        foreach (string name in EnginePaths.ConfigNames("profiles"))
        {
            _profile.AddItem(name);
            if (name == DefaultProfile)
            {
                _profile.Select(_profile.ItemCount - 1);
            }
        }
        if (_planet.ItemCount == 0 || _profile.ItemCount == 0)
        {
            _generate.Disabled = true;
        }
        OnProfileSelected(_profile.Selected);
    }

    private void OnProfileSelected(long index)
    {
        _profileNote.Text = index < 0 ? "" : EnginePaths.ConfigNote("profiles", _profile.GetItemText((int)index));
    }

    private void AppendLog(string line)
    {
        if (_logLines >= MaxLogLines)
        {
            _log.Clear();
            _logLines = 0;
            _log.AddText("(앞 기록은 지웠습니다)\n");
        }
        _log.AddText(line + "\n");
        _logLines++;
        // 터미널에서 띄웠을 때도 기록을 볼 수 있게 표준 출력에도 씁니다.
        GD.Print(line);
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = new Color(0.035f, 0.045f, 0.06f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 24);
        }
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        margin.AddChild(root);

        Label title = Ui.Label(26);
        title.Text = "B-PCG · 행성 만들기";
        root.AddChild(title);
        Label sub = Ui.Label(Ui.FontSize - 1);
        sub.Text = "판·비·강이 만든 지구 크기 행성을 C# 으로 만들고, 회랑을 걸어 다니며 봅니다. "
            + "결과는 회랑(2 m 지형·물·동굴·지층)과 지구본(행성 전체)으로 구워집니다.";
        sub.Modulate = GlobeHud.DimColor;
        root.AddChild(sub);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        root.AddChild(columns);

        // 왼쪽: 설정과 만들기
        PanelContainer leftPanel = Ui.Panel();
        leftPanel.CustomMinimumSize = new Vector2(440, 0);
        columns.AddChild(leftPanel);
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 8);
        leftPanel.AddChild(left);
        left.AddChild(Heading("새 행성"));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 8);
        left.AddChild(grid);
        grid.AddChild(FieldLabel("행성 설정"));
        _planet = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_planet);
        grid.AddChild(FieldLabel("프로필"));
        _profile = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _profile.ItemSelected += OnProfileSelected;
        grid.AddChild(_profile);
        grid.AddChild(FieldLabel("시드"));
        _seed = new SpinBox { MinValue = 0, MaxValue = int.MaxValue, Step = 1, Value = 0, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_seed);

        _profileNote = Ui.Label(Ui.FontSize - 2);
        _profileNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _profileNote.CustomMinimumSize = new Vector2(400, 0);
        _profileNote.Modulate = GlobeHud.DimColor;
        left.AddChild(_profileNote);

        _flat = new CheckBox { Text = "평면 히어로 (행성 없이 히어로 유역만, 지구본 없음)", FocusMode = FocusModeEnum.None };
        _flat.AddThemeFontOverride("font", Ui.Font);
        left.AddChild(_flat);
        _openWhenDone = new CheckBox { Text = "다 만들면 바로 회랑 장면 열기", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        _openWhenDone.AddThemeFontOverride("font", Ui.Font);
        left.AddChild(_openWhenDone);

        _generate = Ui.Button("행성 만들기", Ui.FontSize + 2);
        _generate.CustomMinimumSize = new Vector2(0, 44);
        _generate.Pressed += StartGeneration;
        left.AddChild(_generate);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, CustomMinimumSize = new Vector2(0, 20) };
        left.AddChild(_progress);
        _stage = Ui.Label(Ui.FontSize - 1);
        _stage.Text = "대기 중";
        left.AddChild(_stage);

        // 오른쪽: 지난 결과
        PanelContainer rightPanel = Ui.Panel();
        rightPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(rightPanel);
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 8);
        rightPanel.AddChild(right);
        right.AddChild(Heading("지난 결과"));
        _runs = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        _runs.AddThemeFontOverride("font", Ui.Font);
        _runs.ItemSelected += _ => SetBusy(IsGenerating);
        _runs.ItemActivated += _ => OpenSelected(CorridorScene);
        right.AddChild(_runs);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _openCorridor = Ui.Button("회랑 장면 열기");
        _openCorridor.Pressed += () => OpenSelected(CorridorScene);
        buttons.AddChild(_openCorridor);
        _openGlobe = Ui.Button("지구본 열기");
        _openGlobe.Pressed += () => OpenSelected(GlobeScene);
        buttons.AddChild(_openGlobe);
        Button folder = Ui.Button("결과 폴더 열기");
        folder.Pressed += () =>
        {
            Directory.CreateDirectory(EnginePaths.RunsRoot());
            OS.ShellOpen(EnginePaths.RunsRoot());
        };
        buttons.AddChild(folder);
        Button refresh = Ui.Button("새로 고침");
        refresh.Pressed += RefreshRuns;
        buttons.AddChild(refresh);
        right.AddChild(buttons);
        _runsNote = Ui.Label(Ui.FontSize - 2);
        _runsNote.Modulate = GlobeHud.DimColor;
        _runsNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        right.AddChild(_runsNote);

        // 아래: 기록
        PanelContainer logPanel = Ui.Panel();
        logPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        logPanel.CustomMinimumSize = new Vector2(0, 220);
        root.AddChild(logPanel);
        _log = new RichTextLabel
        {
            ScrollFollowing = true,
            SelectionEnabled = true,
            BbcodeEnabled = false,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _log.AddThemeFontOverride("normal_font", Ui.Font);
        _log.AddThemeFontSizeOverride("normal_font_size", Ui.FontSize - 2);
        logPanel.AddChild(_log);
    }

    private static Label Heading(string text)
    {
        Label label = Ui.Label(Ui.FontSize + 3);
        label.Text = text;
        return label;
    }

    private static Label FieldLabel(string text)
    {
        Label label = Ui.Label();
        label.Text = text;
        return label;
    }
}
