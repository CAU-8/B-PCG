using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 지구본 화면의 글과 패널.
/// </summary>
/// <remarks>
/// 왼쪽 위: 지금 상태와 마우스가 가리키는 자리의 위도·경도, 고도, 고른 레이어 값.
/// 클릭하면 그 자리의 모든 레이어 값을 아래 상자에 고정해 보여 줍니다 (Esc 로 풀기).
/// 오른쪽 위: 레이어 패널 (Tab 으로 숨김). 오른쪽 아래: 범례. 왼쪽 아래: 조작 도움말 (H 로 숨김).
/// </remarks>
public partial class GlobeHud : CanvasLayer
{
    /// <summary>패널의 'M 회랑으로 들어가기' 버튼.</summary>
    [Signal]
    public delegate void CorridorRequestedEventHandler();

    /// <summary>패널의 'F 히어로 유역으로' 버튼.</summary>
    [Signal]
    public delegate void HeroRequestedEventHandler();

    /// <summary>패널의 'N 처음 화면' 버튼.</summary>
    [Signal]
    public delegate void StartRequestedEventHandler();

    public const int FontSize = Ui.FontSize;
    public const int PanelWidth = 380;

    /// <summary>패널 안 글의 너비 (여백과 스크롤 막대 자리 빼고).</summary>
    public const int InnerWidth = PanelWidth - 40;

    /// <summary>범주가 이보다 많으면 범례 색 목록을 스크롤 상자에 넣습니다.</summary>
    public const int ManyCategories = 12;
    public const int ChipListHeight = 150;
    public const int LegendBarHeight = 18;
    public const int TickFontSize = FontSize - 2;
    public static readonly Color DimColor = new(0.72f, 0.76f, 0.82f);

    /// <summary>자료 폴더에 globe.json 이 없을 때의 알림.</summary>
    public const string MissingText =
        "지구본 자료가 없습니다. N을 눌러 처음 화면에서 행성을 만들면 지구본도 함께 만듭니다. "
        + "명령줄에서는 uv run bpcg all 이 지구본까지 만듭니다. 평면 히어로나 bake --no-globe 로 만든 결과에는 지구본이 없습니다.";

    /// <summary>globe.json 은 있는데 형식이 틀리거나 파일이 모자랄 때의 알림.</summary>
    public const string BrokenText =
        "지구본 자료를 읽지 못했습니다. globe 폴더의 파일이 잘못됐거나 서로 크기가 맞지 않습니다. "
        + "N을 눌러 처음 화면에서 행성을 다시 만드세요.";

    public const string HelpText = """
        [왼쪽 끌기] 지구본 돌리기    [휠] 확대·축소    [Space] 자동 회전    [F] 히어로 유역으로
        [1~9] 레이어 고르기 (10번째부터 패널에서)    [R] 강    [Shift+R] 그릴 강 등급 바꾸기    [K] 호수
        [L] 경위선 (15° 간격)    [G] 그늘·대기 빛 (꾸밈)    [ [ · ] ] 고도 과장 줄이기·늘리기
        [클릭] 그 자리 값 모두 고정    [Esc] 고정 풀기    [Tab] 패널 숨기기    [H] 도움말 숨기기
        [M] 회랑으로 들어가 걷기    [N] 처음 화면 (새 행성 만들기)    [-·=] 글자·패널 작게·크게
        """;

    /// <summary>그늘·대기 빛 설명 (패널).</summary>
    public const string ShadingNote =
        "그늘은 비탈만 밝거나 어둡게 칠합니다(빛은 화면 왼쪽 위에서 옵니다). 평평한 곳은 어디서나 범례 색 그대로입니다. "
        + "끄면 모든 칸이 범례 색과 같아집니다. 지구본 둘레의 푸른 빛은 보기 좋게 그린 꾸밈이고 자료가 아닙니다.";

    private GlobeView? _globe;
    private Label _status = null!;
    private PanelContainer _pinnedPanel = null!;
    private Label _pinned = null!;
    private Label _help = null!;
    private Label _missing = null!;
    private VBoxContainer _side = null!;
    private PanelContainer _layersPanel = null!;
    private VBoxContainer _rows = null!;
    private PanelContainer _legendPanel = null!;
    private Label _legendTitle = null!;
    private TextureRect _legendBar = null!;
    private ColorRect _barMarker = null!;
    private Control _ticks = null!;
    private Label _tickMin = null!;
    private Label _tickMid = null!;
    private Label _tickMax = null!;
    private HBoxContainer _noDataChip = null!;
    private GridContainer _chips = null!;
    private ScrollContainer _chipsScroll = null!;
    private GridContainer _chipsScrolled = null!;
    private Label _areaNote = null!;
    private Label _description = null!;
    private Label _howToRead = null!;
    private readonly Dictionary<string, Button> _fieldButtons = [];
    private readonly Dictionary<string, CheckButton> _overlayButtons = [];
    private Button? _riverClassButton;

    /// <summary>강 등급 번호 → 범례 줄 (문턱 아래 등급은 흐리게).</summary>
    private readonly Dictionary<int, Control> _riverChips = [];
    private CheckButton? _gridButton;
    private CheckButton? _shadingButton;
    private Label? _exaggerationLabel;

    public override void _Ready()
    {
        var left = new VBoxContainer { Position = new Vector2(16, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        left.AddThemeConstantOverride("separation", 8);
        _status = Ui.Label();
        left.AddChild(_status);
        _pinnedPanel = Ui.Panel();
        _pinnedPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _pinnedPanel.Visible = false;
        _pinned = Ui.Label(FontSize - 1);
        _pinnedPanel.AddChild(_pinned);
        left.AddChild(_pinnedPanel);
        AddChild(left);

        _help = Ui.Label();
        _help.Text = HelpText;
        _help.AnchorTop = 1.0f;
        _help.AnchorBottom = 1.0f;
        _help.GrowVertical = Control.GrowDirection.Begin;
        _help.OffsetLeft = 16;
        _help.OffsetTop = -16;
        _help.OffsetBottom = -16;
        AddChild(_help);

        _side = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _side.AnchorLeft = 1.0f;
        _side.AnchorRight = 1.0f;
        _side.AnchorBottom = 1.0f;
        _side.GrowHorizontal = Control.GrowDirection.Begin;
        _side.OffsetLeft = -16 - PanelWidth;
        _side.OffsetRight = -16;
        _side.OffsetTop = 12;
        _side.OffsetBottom = -12;
        _side.AddThemeConstantOverride("separation", 8);
        AddChild(_side);

        _layersPanel = Ui.Panel();
        _layersPanel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rows);
        _layersPanel.AddChild(scroll);
        _side.AddChild(_layersPanel);

        _legendPanel = Ui.Panel();
        BuildLegend();
        _side.AddChild(_legendPanel);
        _side.Visible = false;

        _missing = Ui.Label(20);
        _missing.AnchorRight = 1.0f;
        _missing.AnchorBottom = 1.0f;
        _missing.OffsetLeft = 40;
        _missing.OffsetRight = -40;
        _missing.HorizontalAlignment = HorizontalAlignment.Center;
        _missing.VerticalAlignment = VerticalAlignment.Center;
        _missing.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _missing.Visible = false;
        AddChild(_missing);
    }

    /// <summary>지구본에 묶어 레이어 패널과 범례를 만듭니다.</summary>
    public void Bind(GlobeView globe)
    {
        _globe = globe;
        _missing.Visible = false;
        _side.Visible = true;
        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
        _fieldButtons.Clear();
        _overlayButtons.Clear();
        _riverChips.Clear();
        _riverClassButton = null;
        GlobeData data = globe.Data!;

        Label title = Ui.Label(FontSize + 2);
        title.Text = Json.Str(data.Meta, "title", "행성 지구본");
        _rows.AddChild(title);
        Label about = Paragraph(FontSize - 3);
        about.Modulate = DimColor;
        about.Text = AboutText(data);
        _rows.AddChild(about);

        _rows.AddChild(Section("레이어 (누르면 그 값으로 지구본을 칠합니다)"));
        var group = new ButtonGroup();
        List<string> ordered = data.OrderedFieldNames();
        string currentGroup = "";
        HFlowContainer? flow = null;
        for (int i = 0; i < ordered.Count; i++)
        {
            string name = ordered[i];
            string g = data.GroupOf(name);
            if (g != currentGroup || flow is null)
            {
                currentGroup = g;
                Label groupLabel = Ui.Label(FontSize - 2);
                groupLabel.Text = g;
                groupLabel.Modulate = DimColor;
                _rows.AddChild(groupLabel);
                flow = new HFlowContainer { CustomMinimumSize = new Vector2(InnerWidth, 0) };
                flow.AddThemeConstantOverride("h_separation", 4);
                flow.AddThemeConstantOverride("v_separation", 4);
                _rows.AddChild(flow);
            }
            string key = i < 9 ? $"{i + 1}  " : "";
            Button button = Ui.Button(key + data.LabelOf(name));
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.TooltipText = Json.Str(data.Field(name), "description", "");
            button.Pressed += () => _globe?.SetField(name);
            flow.AddChild(button);
            _fieldButtons[name] = button;
        }

        _rows.AddChild(new HSeparator());
        _rows.AddChild(Section("겹쳐 보기"));
        foreach (OrderedDictionary<string, object?> o in data.Overlays)
        {
            AddOverlayRows(o);
        }
        _gridButton = Ui.Check($"L  경위선 ({(int)GlobeView.GridStepDeg}° 간격, 적도와 경도 0° 선은 굵게)");
        _gridButton.Toggled += on => _globe?.SetGridVisible(on);
        _rows.AddChild(_gridButton);
        _shadingButton = Ui.Check("G  그늘·대기 빛 (꾸밈, 자료 아님)");
        _shadingButton.TooltipText = ShadingNote;
        _shadingButton.Toggled += on => _globe?.SetShadingVisible(on);
        _rows.AddChild(_shadingButton);
        Label shadingNote = Paragraph(FontSize - 3);
        shadingNote.Modulate = DimColor;
        shadingNote.Text = ShadingNote;
        _rows.AddChild(shadingNote);

        var exRow = new HBoxContainer();
        exRow.AddThemeConstantOverride("separation", 6);
        Button down = Ui.Button("[  −");
        down.TooltipText = "고도 과장을 한 단계 줄입니다 ([)";
        down.Pressed += () => _globe?.StepExaggeration(-1);
        exRow.AddChild(down);
        _exaggerationLabel = Ui.Label();
        _exaggerationLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _exaggerationLabel.HorizontalAlignment = HorizontalAlignment.Center;
        exRow.AddChild(_exaggerationLabel);
        Button up = Ui.Button("+  ]");
        up.TooltipText = "고도 과장을 한 단계 늘립니다 (])";
        up.Pressed += () => _globe?.StepExaggeration(1);
        exRow.AddChild(up);
        _rows.AddChild(exRow);
        Label exNote = Paragraph(FontSize - 3);
        exNote.Modulate = DimColor;
        exNote.Text = "땅 높이만 이 배율만큼 부풀려 그립니다. ×1 이 실제 비율인데, 그러면 가장 높은 산도 "
            + "지구본 반지름의 0.1 % 남짓이라 거의 보이지 않습니다. 바다는 해수면 높이로 평평하게 두고 깊이는 색으로만 보입니다.";
        _rows.AddChild(exNote);

        _rows.AddChild(new HSeparator());
        if (globe.HeroDirection() != Vector3.Zero)
        {
            Button hero = Ui.Button("F  히어로 유역으로 날아가기");
            hero.TooltipText = Json.Str(data.Hero, "description", "");
            hero.Pressed += () => EmitSignal(SignalName.HeroRequested);
            _rows.AddChild(hero);
        }
        Button corridor = Ui.Button("M  회랑으로 들어가기 (걷는 장면)");
        corridor.Pressed += () => EmitSignal(SignalName.CorridorRequested);
        _rows.AddChild(corridor);
        Button start = Ui.Button("N  처음 화면으로 (새 행성 만들기)");
        start.Pressed += () => EmitSignal(SignalName.StartRequested);
        _rows.AddChild(start);

        globe.FieldChanged -= OnFieldChanged;
        globe.FieldChanged += OnFieldChanged;
        globe.DisplayChanged -= Refresh;
        globe.DisplayChanged += Refresh;
        Refresh();
        OnFieldChanged(globe.ActiveField);
    }

    /// <summary>자료가 없거나(missing) 읽지 못했을 때 가운데에 알림을 보이고 패널을 숨깁니다.</summary>
    public void ShowMissing(string reason, bool missing = true)
    {
        _side.Visible = false;
        _pinnedPanel.Visible = false;
        _missing.Text = (missing ? MissingText : BrokenText) + "\n\n까닭: " + reason;
        _missing.Visible = true;
        _status.Text = (missing ? "지구본 자료 없음" : "지구본 자료를 읽지 못함")
            + " · M을 누르면 회랑으로, N을 누르면 처음 화면으로 갑니다";
    }

    /// <summary>버튼 상태와 고도 과장·강 등급 글을 지구본 상태에 맞춥니다 (신호를 다시 내지 않음).</summary>
    public void Refresh()
    {
        if (_globe is null)
        {
            return;
        }
        foreach ((string name, Button button) in _fieldButtons)
        {
            button.SetPressedNoSignal(name == _globe.ActiveField);
        }
        foreach ((string name, CheckButton check) in _overlayButtons)
        {
            check.SetPressedNoSignal(_globe.IsOverlayVisible(name));
        }
        _gridButton?.SetPressedNoSignal(_globe.ShowGrid);
        _shadingButton?.SetPressedNoSignal(_globe.ShowShading);
        if (_exaggerationLabel is not null)
        {
            _exaggerationLabel.Text = $"고도 과장 ×{FormatFactor(_globe.Exaggeration)}";
        }
        if (_riverClassButton is not null)
        {
            _riverClassButton.Text = $"Shift+R  {_globe.RiverMinClass} 등급 이상 강만 그림 (누르면 바꿈)";
        }
        foreach ((int k, Control chip) in _riverChips)
        {
            bool drawn = _globe.ShowRivers && k >= _globe.RiverMinClass;
            chip.Modulate = new Color(1, 1, 1, drawn ? 1.0f : 0.35f);
        }
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>고정한 자리의 값 목록을 보입니다. 빈 글이면 상자를 숨깁니다.</summary>
    public void SetPinned(string text)
    {
        _pinned.Text = text;
        _pinnedPanel.Visible = text.Length > 0;
    }

    public string PinnedText() => _pinnedPanel.Visible ? _pinned.Text : "";

    public void ToggleHelp() => _help.Visible = !_help.Visible;

    public void TogglePanel()
    {
        if (_globe is not null && _globe.IsReady())
        {
            _side.Visible = !_side.Visible;
        }
    }

    public bool IsPanelVisible() => _side.Visible;

    public bool IsMissingVisible() => _missing.Visible;

    public string MissingTextShown() => _missing.Text;

    /// <summary>범례 제목 (검사용).</summary>
    public string LegendTitle() => _legendTitle.Text;

    /// <summary>범례에 보이는 범주 수 (연속 필드면 0, 검사용). 지운 줄은 다음 프레임에 사라지므로 뺍니다.</summary>
    public int LegendChipCount()
    {
        int n = 0;
        foreach (GridContainer grid in new[] { _chips, _chipsScrolled })
        {
            foreach (Node child in grid.GetChildren())
            {
                if (!child.IsQueuedForDeletion())
                {
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>범례 색 막대가 보이는지 (검사용).</summary>
    public bool LegendBarVisible() => _legendBar.Visible;

    /// <summary>범례 막대 아래 글 [최소, 가운데, 최대] (검사용).</summary>
    public string[] LegendTicks() => [_tickMin.Text, _tickMid.Text, _tickMax.Text];

    public Button? FieldButton(string name) => _fieldButtons.GetValueOrDefault(name);

    public CheckButton? OverlayButton(string name) => _overlayButtons.GetValueOrDefault(name);

    public CheckButton? GridButton() => _gridButton;

    public CheckButton? ShadingButton() => _shadingButton;

    /// <summary>범례의 넓이 몫 잣대 글 (보이지 않으면 빈 글, 검사용).</summary>
    public string LegendAreaNote() => _areaNote.Visible ? _areaNote.Text : "";

    /// <summary>마우스 자리 읽기: 위도·경도, 고도, 고른 레이어 값, 그 칸의 강·호수.</summary>
    public string DescribePoint(Vector3 dir)
    {
        GlobeView globe = _globe!;
        GlobeData data = globe.Data!;
        var lines = new List<string> { "위도·경도: " + GlobeData.FormatLatLon(GlobeData.LatLon(dir)) };
        string active = globe.ActiveField;
        if (data.HasField(GlobeData.ElevationField))
        {
            lines.Add($"{data.LabelOf(GlobeData.ElevationField)}: "
                + data.FormatValue(GlobeData.ElevationField, data.ValueAt(GlobeData.ElevationField, dir)));
        }
        else
        {
            lines.Add($"고도: {GlobeData.FormatNumber(data.CornerElevationAt(dir))} m");
        }
        if (active != GlobeData.ElevationField && data.HasField(active))
        {
            lines.Add($"{data.LabelOf(active)}: {data.FormatValue(active, data.ValueAt(active, dir))}");
        }
        foreach (OrderedDictionary<string, object?> o in data.Overlays)
        {
            string oname = Json.S(o["name"]);
            int v = data.OverlayValueAt(oname, dir);
            if (v != 0 && globe.IsOverlayVisible(oname))
            {
                lines.Add(OverlayLine(o, v));
            }
        }
        lines.Add("클릭하면 이 자리의 모든 레이어 값을 아래 상자에 고정합니다");
        return string.Join("\n", lines);
    }

    /// <summary>고정 상자: 그 자리의 모든 레이어 값 (무리별, 이름: 값 단위).</summary>
    public string DescribeAll(Vector3 dir)
    {
        GlobeData data = _globe!.Data!;
        GlobeData.Cell cell = data.DirectionToCell(dir);
        var lines = new List<string>
        {
            "고정한 자리: " + GlobeData.FormatLatLon(GlobeData.LatLon(dir)) + "  (Esc로 풀기)",
            $"칸: 면 {cell.Face}, 행 {cell.Row}, 열 {cell.Col} (면당 {data.FaceRes} × {data.FaceRes} 칸)",
        };
        string group = "";
        foreach (string name in data.OrderedFieldNames())
        {
            string g = data.GroupOf(name);
            if (g != group)
            {
                group = g;
                lines.Add($"[{g}]");
            }
            double v = data.RawValue(name, cell.Face, cell.Row, cell.Col);
            lines.Add($"  {data.LabelOf(name)}: {data.FormatValue(name, v)}");
        }
        if (data.Overlays.Count > 0)
        {
            lines.Add("[겹쳐 보기]");
            foreach (OrderedDictionary<string, object?> o in data.Overlays)
            {
                lines.Add("  " + OverlayLine(o, data.OverlayValueAt(Json.S(o["name"]), dir)));
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 겹쳐 보기 한 줄: "강: 강 (10~100 m³/s)". 값이 있는데 지금 그리지 않으면 까닭을 덧붙입니다
    /// (예: 강 등급 문턱 아래). 화면에 없는 것을 있는 것처럼 읽지 않게 하려는 것입니다.
    /// </summary>
    private string OverlayLine(OrderedDictionary<string, object?> o, int value)
    {
        string oname = Json.S(o["name"]);
        string line = $"{Json.Str(o, "label", oname)}: {_globe!.Data!.OverlayValueLabel(oname, value)}";
        if (value != 0)
        {
            string why = _globe.OverlayHiddenReason(oname, value);
            if (why.Length > 0)
            {
                line += $" ({why})";
            }
        }
        return line;
    }

    /// <summary>1.0 → "1", 2.5 → "2.5".</summary>
    public static string FormatFactor(float x) =>
        Mathf.IsEqualApprox(x, Mathf.Round(x))
            ? ((int)x).ToString(CultureInfo.InvariantCulture)
            : x.ToString("F1", CultureInfo.InvariantCulture);

    private void AddOverlayRows(OrderedDictionary<string, object?> o)
    {
        GlobeData data = _globe!.Data!;
        string oname = Json.S(o["name"]);
        string olabel = Json.Str(o, "label", oname);
        string key = oname switch
        {
            GlobeView.OverlayRivers => "R",
            GlobeView.OverlayLakes => "K",
            _ => "",
        };
        CheckButton check = Ui.Check(key.Length == 0 ? olabel : $"{key}  {olabel}");
        check.TooltipText = Json.Str(o, "description", "");
        check.Toggled += on => _globe?.SetOverlayVisible(oname, on);
        _rows.AddChild(check);
        _overlayButtons[oname] = check;
        string how = Json.Str(o, "how_to_read", "");
        if (oname == GlobeView.OverlayRivers)
        {
            how += " 행성 지도(L0)는 칸이 커서 육지 대부분이 강으로 잡힙니다. "
                + $"그래서 처음에는 {GlobeView.DefaultRiverMinClass} 등급 이상인 큰 강만 그립니다.";
        }
        if (how.Length > 0)
        {
            Label note = Paragraph(FontSize - 3);
            note.Modulate = DimColor;
            note.Text = how.Trim();
            _rows.AddChild(note);
        }
        if (oname == GlobeView.OverlayRivers)
        {
            _riverClassButton = Ui.Button("");
            _riverClassButton.TooltipText = "그릴 강의 가장 낮은 유량 등급을 바꿉니다 (Shift+R)";
            _riverClassButton.Pressed += () => _globe?.CycleRiverMinClass();
            _rows.AddChild(_riverClassButton);
            foreach (object? cat in Json.Arr(Json.Get(o, "categories")) ?? [])
            {
                OrderedDictionary<string, object?>? c = Json.Obj(cat);
                int id = Json.Get(c, "id") is object cid ? Json.I(cid) : 0;
                if (id <= 0)
                {
                    continue;
                }
                HBoxContainer chip = Chip(
                    data.OverlayValueColor(oname, id, GlobeView.RiverFallbackColor),
                    $"{id} 등급: {data.OverlayValueLabel(oname, id)}", InnerWidth - 20);
                _rows.AddChild(chip);
                _riverChips[id] = chip;
            }
        }
        else
        {
            _rows.AddChild(Chip(
                data.OverlayValueColor(oname, 1, GlobeView.LakeFallbackColor), data.OverlayValueLabel(oname, 1), InnerWidth - 20));
        }
    }

    private void OnFieldChanged(string name)
    {
        Refresh();
        if (_globe is null || !_globe.Data!.HasField(name))
        {
            return;
        }
        GlobeData data = _globe.Data;
        string unit = data.DisplayUnitOf(name);
        string title = data.LabelOf(name);
        var extra = new List<string>();
        if (unit.Length > 0)
        {
            extra.Add(unit);
        }
        if (data.IsLog(name))
        {
            extra.Add("로그 눈금");
        }
        if (data.IsCategorical(name))
        {
            extra.Add("범주");
        }
        if (extra.Count > 0)
        {
            title += " (" + string.Join(", ", extra) + ")";
        }
        _legendTitle.Text = title;

        foreach (GridContainer grid in new[] { _chips, _chipsScrolled })
        {
            foreach (Node child in grid.GetChildren())
            {
                grid.RemoveChild(child);
                child.QueueFree();
            }
        }
        if (data.IsCategorical(name))
        {
            _legendBar.Visible = false;
            _ticks.Visible = false;
            _noDataChip.Visible = false;
            List<object?> cats = data.Categories(name);
            bool many = cats.Count > ManyCategories;
            GridContainer grid = many ? _chipsScrolled : _chips;
            foreach (object? cat in cats)
            {
                OrderedDictionary<string, object?>? c = Json.Obj(cat);
                int id = Json.Get(c, "id") is object cid ? Json.I(cid) : -1;
                string text = data.CategoryLabel(name, id);
                double frac = data.AreaFraction(name, id);
                if (!double.IsNaN(frac))
                {
                    text += $" ({Percent(frac)})";
                }
                grid.AddChild(Chip(data.CategoryColor(name, id), text, (InnerWidth - 12) / 2.0f));
            }
            _chips.Visible = !many;
            _chipsScroll.Visible = many;
            _areaNote.Text = data.AreaFractionNote(name);
            _areaNote.Visible = _areaNote.Text.Length > 0;
        }
        else
        {
            _chips.Visible = false;
            _chipsScroll.Visible = false;
            _areaNote.Visible = false;
            _legendBar.Visible = true;
            _ticks.Visible = true;
            _legendBar.Texture = ImageTexture.CreateFromImage(data.LegendImage(name, 256));
            SetTicks(name);
            object? nanLabel = Json.Get(data.Field(name), "nan_label");
            _noDataChip.Visible = nanLabel is not null;
            if (nanLabel is not null)
            {
                ((ColorRect)_noDataChip.GetChild(0)).Color = data.NoDataColorValue;
                ((Label)_noDataChip.GetChild(1)).Text = data.NoDataTextOf(name);
            }
        }
        string desc = Json.Str(data.Field(name), "description", "");
        string how = Json.Str(data.Field(name), "how_to_read", "");
        _description.Text = "무엇인가: " + (desc.Length > 0 ? desc : "(설명 없음)");
        _howToRead.Text = "읽는 법: " + (how.Length > 0 ? how : "(설명 없음)");
    }

    /// <summary>
    /// 막대 아래 글: 왼쪽 끝 = 최소, 오른쪽 끝 = 최대, 가운데 = 0 (범위가 0 을 걸치고 로그가 아니면, 그 자리에 표시선)
    /// 또는 가운데 값. 자료가 막대 범위 밖까지 있으면 '이하'·'이상'을 붙입니다.
    /// </summary>
    private void SetTicks(string name)
    {
        GlobeData data = _globe!.Data!;
        Vector2 r = data.ValueRange(name);
        double span = r.Y - r.X;
        Vector2 real = data.DataRange(name);
        double eps = Math.Abs(span) * 1e-6;
        _tickMin.Text = data.FormatValue(name, r.X);
        if (!float.IsNaN(real.X) && real.X < r.X - eps)
        {
            _tickMin.Text += " 이하";
        }
        _tickMax.Text = data.FormatValue(name, r.Y);
        if (!float.IsNaN(real.Y) && real.Y > r.Y + eps)
        {
            _tickMax.Text += " 이상";
        }
        double mid = 0.5 * (r.X + r.Y);
        if (!data.IsLog(name) && r.X < 0.0f && r.Y > 0.0f)
        {
            mid = 0.0;
        }
        _tickMid.Text = data.FormatValue(name, mid);
        float f = span <= 0.0 ? 0.5f : (float)Math.Clamp((mid - r.X) / span, 0.0, 1.0);

        float width = InnerWidth;
        float lineH = TextSize("가").Y;
        float wMin = TextSize(_tickMin.Text).X;
        float wMid = TextSize(_tickMid.Text).X;
        float wMax = TextSize(_tickMax.Text).X;
        _tickMin.Position = Vector2.Zero;
        _tickMax.Position = new Vector2(width - wMax, 0.0f);
        float x = (f * width) - (0.5f * wMid);
        int rows = 1;
        if (x < wMin + 8.0f || x + wMid > width - wMax - 8.0f)
        {
            // 양 끝 글과 겹치면 둘째 줄에 둡니다.
            rows = 2;
            x = Math.Clamp(x, 0.0f, Math.Max(width - wMid, 0.0f));
        }
        _tickMid.Position = new Vector2(x, lineH * (rows - 1));
        _ticks.CustomMinimumSize = new Vector2(width, (lineH * rows) + 2.0f);
        _barMarker.Position = new Vector2((f * width) - 1.0f, 0.0f);
    }

    private void BuildLegend()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        Label caption = Ui.Label(FontSize - 2);
        caption.Text = "범례 (지금 칠한 레이어)";
        caption.Modulate = DimColor;
        box.AddChild(caption);
        _legendTitle = Ui.Label(FontSize + 1);
        box.AddChild(_legendTitle);
        _legendBar = new TextureRect
        {
            CustomMinimumSize = new Vector2(InnerWidth, LegendBarHeight),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _barMarker = new ColorRect
        {
            Color = new Color(1, 1, 1, 0.9f),
            Size = new Vector2(2, LegendBarHeight),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _legendBar.AddChild(_barMarker);
        box.AddChild(_legendBar);
        _ticks = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _tickMin = Ui.Label(TickFontSize);
        _tickMid = Ui.Label(TickFontSize);
        _tickMax = Ui.Label(TickFontSize);
        _ticks.AddChild(_tickMin);
        _ticks.AddChild(_tickMid);
        _ticks.AddChild(_tickMax);
        box.AddChild(_ticks);
        _noDataChip = Chip(GlobeData.NoDataColor, GlobeData.NoDataText, InnerWidth - 20);
        box.AddChild(_noDataChip);
        _chips = new GridContainer { Columns = 2 };
        box.AddChild(_chips);
        _chipsScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(InnerWidth, ChipListHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _chipsScrolled = new GridContainer { Columns = 2 };
        _chipsScroll.AddChild(_chipsScrolled);
        box.AddChild(_chipsScroll);
        _areaNote = Paragraph(FontSize - 3);
        _areaNote.Modulate = DimColor;
        _areaNote.Visible = false;
        box.AddChild(_areaNote);
        _description = Paragraph(FontSize - 2);
        box.AddChild(_description);
        _howToRead = Paragraph(FontSize - 2);
        box.AddChild(_howToRead);
        _legendPanel.AddChild(box);
    }

    private static string AboutText(GlobeData data)
    {
        var parts = new List<string>();
        string desc = Json.Str(data.Meta, "description", "");
        if (desc.Length > 0)
        {
            parts.Add(desc);
        }
        // 칸 크기는 globe.json 의 설명과 같은 잣대(면 가운데 칸 한 변 = 2πR / 4N)입니다.
        double cellKm = data.RadiusM * (Math.PI / 2.0) / data.FaceRes / 1000.0;
        var facts = new List<string>();
        if (Json.Get(data.Meta, "seed") is object seed)
        {
            facts.Add($"시드 {Json.S(seed)}");
        }
        facts.Add($"면당 {data.FaceRes} × {data.FaceRes} 칸 (면 가운데 칸 한 변 약 {cellKm:F1} km)");
        facts.Add($"반지름 {GlobeData.FormatNumber(data.RadiusM / 1000.0)} km");
        parts.Add(string.Join(" · ", facts));
        return string.Join("\n", parts);
    }

    private static Label Section(string text)
    {
        Label label = Ui.Label(FontSize - 1);
        label.Text = text;
        return label;
    }

    private static HBoxContainer Chip(Color color, string text, float width)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        var swatch = new ColorRect
        {
            Color = color,
            CustomMinimumSize = new Vector2(14, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(swatch);
        Label label = Ui.Label(FontSize - 2);
        label.Text = text;
        label.CustomMinimumSize = new Vector2(Math.Max(width - 20.0f, 40.0f), 0);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(label);
        return row;
    }

    private static Vector2 TextSize(string text) =>
        Ui.Font.GetStringSize(text, HorizontalAlignment.Left, -1, TickFontSize);

    private static string Percent(double frac)
    {
        double p = frac * 100.0;
        if (p >= 10.0)
        {
            return $"{p:F0} %";
        }
        if (p >= 0.1)
        {
            return $"{p:F1} %";
        }
        return p > 0.0 ? "0.1 % 미만" : "0 %";
    }

    private static Label Paragraph(int fontSize)
    {
        Label label = Ui.Label(fontSize);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(InnerWidth, 0);
        return label;
    }
}
