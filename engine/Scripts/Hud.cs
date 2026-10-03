using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 회랑 장면의 화면 위 정보와 레이어 패널.
/// </summary>
/// <remarks>
/// 왼쪽 위: 지금 상태 (모드, 속력, 위치, 발밑 암석, 단면). 왼쪽 아래: 조작 도움말 (H 로 숨김).
/// 오른쪽: 레이어 패널. 줄마다 켜기·끄기 버튼과 '만 보기' 버튼이 있고, 아래에 '모두 보기',
/// 색 바꾸기, 손전등, 지구본, 처음 화면 버튼이 있습니다 (Tab 으로 숨김). 버튼을 누르려면 Esc 로 마우스를 놓습니다.
/// </remarks>
public partial class Hud : CanvasLayer
{
    /// <summary>패널의 버튼을 누를 때 알립니다. name: "color_mode", "lamp", "globe", "start".</summary>
    [Signal]
    public delegate void ActionRequestedEventHandler(string name);

    public const string HelpText = """
        [V] 날기(노클립) ↔ 걷기    [WASD] 이동    [Space·E] 위    [Q·Ctrl] 아래
        [Shift] 빠르게    [휠] 나는 속력    [클릭] 마우스로 둘러보기    [Esc] 마우스 놓기
        {layer_keys}
        [X] 단면 켜기·끄기 (눈앞에 자르는 면)    [ [ ] ] 단면 당기기·밀기
        [T] 다음 동굴 입구로    [Shift+T] 이전 입구    [G] 색: 자연색 ↔ 지질도    [F] 손전등
        [M] 지구본 (행성 전체)    [N] 처음 화면 (새 행성)    [Tab] 레이어 패널 숨기기    [H] 도움말 숨기기
        """;

    private Label _status = null!;
    private Label _help = null!;
    private PanelContainer _panel = null!;
    private VBoxContainer _rows = null!;
    private BakedLayers? _layers;
    private readonly Dictionary<string, Button> _toggles = [];
    private Button? _colorButton;
    private Button? _lampButton;

    public override void _Ready()
    {
        _status = Ui.Label();
        _status.Position = new Vector2(16, 12);
        AddChild(_status);

        _help = Ui.Label();
        _help.Text = HelpText.Replace("{layer_keys}", "[1~7] 레이어 켜기·끄기    [0] 모두 보기");
        _help.AnchorTop = 1.0f;
        _help.AnchorBottom = 1.0f;
        _help.GrowVertical = Control.GrowDirection.Begin;
        _help.OffsetLeft = 16;
        _help.OffsetTop = -16;
        _help.OffsetBottom = -16;
        AddChild(_help);

        _panel = Ui.Panel(0.78f);
        _panel.AnchorLeft = 1.0f;
        _panel.AnchorRight = 1.0f;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.OffsetLeft = -16;
        _panel.OffsetRight = -16;
        _panel.OffsetTop = 12;
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 4);
        _panel.AddChild(_rows);
        AddChild(_panel);
    }

    /// <summary>레이어 패널을 만듭니다. 레이어가 바뀌면 버튼 상태를 다시 맞춥니다.</summary>
    public void BindLayers(BakedLayers layers)
    {
        _layers = layers;
        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }
        _toggles.Clear();
        Label title = Ui.Label();
        title.Text = "레이어";
        _rows.AddChild(title);
        int index = 1;
        foreach (string id in layers.LayerIds())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            Button toggle = Ui.Button($"{index}  {layers.LayerLabel(id)}");
            toggle.ToggleMode = true;
            toggle.CustomMinimumSize = new Vector2(200, 0);
            toggle.Alignment = HorizontalAlignment.Left;
            toggle.TooltipText = layers.LayerHint(id);
            toggle.Toggled += on => layers.SetLayerVisible(id, on);
            row.AddChild(toggle);
            _toggles[id] = toggle;
            if (layers.CanSolo(id))
            {
                Button solo = Ui.Button("만 보기");
                solo.TooltipText = $"이 레이어만 보이고 나머지는 숨깁니다 (Shift+{index})";
                solo.Pressed += () => layers.Solo(id);
                row.AddChild(solo);
            }
            _rows.AddChild(row);
            index++;
        }
        _help.Text = HelpText.Replace("{layer_keys}", LayerKeysLine(layers));
        Button all = Ui.Button("0  모두 보기");
        all.Pressed += layers.ShowAll;
        _rows.AddChild(all);
        _colorButton = Ui.Button("");
        _colorButton.Pressed += () => EmitSignal(SignalName.ActionRequested, "color_mode");
        _rows.AddChild(_colorButton);
        _lampButton = Ui.Button("");
        _lampButton.Pressed += () => EmitSignal(SignalName.ActionRequested, "lamp");
        _rows.AddChild(_lampButton);
        Button globe = Ui.Button("M  지구본 (행성 전체)");
        globe.TooltipText = "행성 전체를 지구본으로 봅니다. 지구본에서 M 을 누르면 돌아옵니다";
        globe.Pressed += () => EmitSignal(SignalName.ActionRequested, "globe");
        _rows.AddChild(globe);
        Button start = Ui.Button("N  처음 화면 (새 행성 만들기)");
        start.TooltipText = "프로필과 시드를 골라 행성을 새로 만들거나 지난 결과를 엽니다";
        start.Pressed += () => EmitSignal(SignalName.ActionRequested, "start");
        _rows.AddChild(start);
        layers.LayersChanged += RefreshLayers;
        RefreshLayers();
    }

    /// <summary>도움말의 레이어 키 줄. 번호는 지금 있는 레이어 순서(LayerIds)를 따릅니다.</summary>
    private static string LayerKeysLine(BakedLayers layers)
    {
        List<string> ids = layers.LayerIds();
        var solo = new List<int>();
        var extra = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (layers.CanSolo(ids[i]))
            {
                solo.Add(i + 1);
            }
            else
            {
                extra.Add($"{i + 1} = {layers.LayerLabel(ids[i])}");
            }
        }
        string line = $"[1~{ids.Count}] 레이어 켜기·끄기";
        if (extra.Count > 0)
        {
            line += $" ({string.Join(", ", extra)})";
        }
        if (solo.Count > 0)
        {
            string range = solo.Count == 1 ? $"{solo[0]}" : $"{solo[0]}~{solo.Last()}";
            line += $"    [Shift+{range}] 그 레이어만 보기";
        }
        return line + "    [0] 모두 보기";
    }

    /// <summary>버튼 눌림 상태를 레이어 상태에 맞춥니다 (신호를 다시 내지 않음).</summary>
    public void RefreshLayers()
    {
        if (_layers is null)
        {
            return;
        }
        foreach ((string id, Button toggle) in _toggles)
        {
            toggle.SetPressedNoSignal(_layers.IsLayerVisible(id));
        }
    }

    /// <summary>'색' 버튼과 '손전등' 버튼의 글자.</summary>
    public void SetOptionLabels(string colorLabel, bool lampOn)
    {
        if (_colorButton is not null)
        {
            _colorButton.Text = "G  색: " + colorLabel;
        }
        if (_lampButton is not null)
        {
            _lampButton.Text = "F  손전등: " + (lampOn ? "켜짐" : "꺼짐");
        }
    }

    public void SetStatus(string text) => _status.Text = text;

    public void ToggleHelp() => _help.Visible = !_help.Visible;

    public void TogglePanel() => _panel.Visible = !_panel.Visible;

    /// <summary>레이어 이름 → 켜기·끄기 버튼 (검사용).</summary>
    public Button? LayerButton(string id) => _toggles.GetValueOrDefault(id);
}
