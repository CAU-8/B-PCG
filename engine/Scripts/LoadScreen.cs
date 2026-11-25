using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 회랑을 불러오는 동안 화면 가운데에 띄우는 진행 막대와, 다 불러온 뒤 화면 가운데에 띄우는 '불러오기 문제' 상자.
/// </summary>
/// <remarks>
/// <see cref="Hud"/> 가 자식으로 하나 둡니다. 불러오는 동안에는 반투명한 막이 화면을 덮어 3D 화면 클릭(마우스 잡기)을
/// 막습니다. 문제 상자는 왼쪽 위 상태 글이나 오른쪽 레이어 패널과 겹치지 않게 가운데에 두고, '닫기' 단추를 누를 때까지
/// 남습니다 (단추를 누르려면 Esc 로 마우스를 놓습니다).
/// </remarks>
public partial class LoadScreen : Control
{
    public static readonly Color WarnColor = new(1.0f, 0.78f, 0.35f);

    /// <summary>문제 상자의 폭 (px, 화면 배율 적용 전).</summary>
    public const float ProblemsWidth = 620.0f;

    private ColorRect _shade = null!;
    private Label _dir = null!;
    private ProgressBar _bar = null!;
    private Label _stage = null!;
    private PanelContainer _problems = null!;
    private Label _problemsTitle = null!;
    private Label _problemsText = null!;

    /// <summary>진행 막대가 떠 있는지 (불러오는 중).</summary>
    public bool IsLoadingVisible => _shade.Visible;

    /// <summary>문제 상자가 떠 있는지.</summary>
    public bool IsProblemsVisible => _problems.Visible;

    /// <summary>지금 진행 글 (검사용).</summary>
    public string StageText => _stage.Text;

    /// <summary>문제 상자의 제목과 본문 (떠 있지 않으면 빈 글, 검사용).</summary>
    public string ProblemsText => _problems.Visible ? _problemsTitle.Text + "\n" + _problemsText.Text : "";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _shade = new ColorRect { Color = new Color(0.02f, 0.03f, 0.04f, 0.72f), Visible = false };
        _shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_shade);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        _shade.AddChild(center);
        PanelContainer panel = Ui.Panel(0.92f);
        center.AddChild(panel);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);
        Label title = Ui.Label(Ui.FontSize + 7);
        title.Text = "회랑을 불러오는 중";
        box.AddChild(title);
        _dir = Ui.Label(Ui.FontSize - 2);
        _dir.Modulate = GlobeHud.DimColor;
        _dir.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _dir.CustomMinimumSize = new Vector2(540, 0);
        box.AddChild(_dir);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, CustomMinimumSize = new Vector2(0, 20) };
        box.AddChild(_bar);
        _stage = Ui.Label();
        box.AddChild(_stage);
        Label hint = Ui.Label(Ui.FontSize - 2);
        hint.Modulate = GlobeHud.DimColor;
        hint.Text = "기다리지 않고 처음 화면으로 가려면 N을 누르세요";
        box.AddChild(hint);

        var problemsCenter = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        problemsCenter.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(problemsCenter);
        _problems = Ui.Panel(0.92f);
        _problems.Visible = false;
        problemsCenter.AddChild(_problems);
        var pbox = new VBoxContainer { CustomMinimumSize = new Vector2(ProblemsWidth, 0) };
        pbox.AddThemeConstantOverride("separation", 6);
        _problems.AddChild(pbox);
        _problemsTitle = Ui.Label(Ui.FontSize + 1);
        _problemsTitle.Modulate = WarnColor;
        pbox.AddChild(_problemsTitle);
        _problemsText = Ui.Label(Ui.FontSize - 1);
        _problemsText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _problemsText.CustomMinimumSize = new Vector2(ProblemsWidth, 0);
        pbox.AddChild(_problemsText);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        Button close = Ui.Button("닫기");
        close.Pressed += () => _problems.Visible = false;
        row.AddChild(close);
        Label closeHint = Ui.Label(Ui.FontSize - 2);
        closeHint.Modulate = GlobeHud.DimColor;
        closeHint.Text = "단추를 누르려면 Esc로 마우스를 놓습니다. 같은 글이 콘솔에도 나옵니다";
        row.AddChild(closeHint);
        pbox.AddChild(row);
    }

    /// <summary>진행 막대를 띄웁니다. dir: 읽는 회랑 폴더.</summary>
    public void Begin(string dir)
    {
        _problems.Visible = false;
        _dir.Text = "폴더: " + dir;
        _bar.Value = 0;
        _shade.Visible = true;
    }

    /// <summary>
    /// 진행 글을 바꿉니다: '<paramref name="label"/> (step/total단계) · 걸린 초'. 막대는 끝난 단계(step − 1)의 몫입니다.
    /// </summary>
    public void SetStage(int step, int total, string label, double seconds)
    {
        _bar.Value = 100.0 * (step - 1) / total;
        _stage.Text = $"{label} ({step}/{total}단계) · {seconds:F1} s";
    }

    /// <summary>진행 막대를 닫고, 문제가 있으면 문제 상자를 띄웁니다.</summary>
    public void Finish(IReadOnlyCollection<string> problems)
    {
        _shade.Visible = false;
        if (problems.Count == 0)
        {
            _problems.Visible = false;
            return;
        }
        _problemsTitle.Text = $"불러오기 문제 {problems.Count}개";
        _problemsText.Text = string.Join("\n", problems.Select(p => "• " + p));
        _problems.Visible = true;
    }
}
