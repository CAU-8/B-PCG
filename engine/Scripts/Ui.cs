using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 화면 글·버튼을 같은 모양으로 만드는 도우미 (Hud, GlobeHud, StartMenu 가 같이 씀).
/// 버튼은 키보드 초점을 받지 않아 Space 같은 키가 버튼을 누르지 않습니다.
/// </summary>
public static class Ui
{
    public static readonly string[] FontNames =
    [
        "Apple SD Gothic Neo", "AppleGothic", "Malgun Gothic", "Noto Sans CJK KR",
        "Noto Sans KR", "NanumGothic", "sans-serif",
    ];

    public const int FontSize = 15;

    private static SystemFont? CachedFont;

    /// <summary>한글이 나오는 시스템 글꼴 (한 번만 만듭니다).</summary>
    public static SystemFont Font => CachedFont ??= new SystemFont { FontNames = FontNames };

    public static Label Label(int fontSize = FontSize)
    {
        var label = new Label();
        label.AddThemeFontOverride("font", Font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("outline_size", 5);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    public static Button Button(string text, int fontSize = FontSize - 1)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontOverride("font", Font);
        button.AddThemeFontSizeOverride("font_size", fontSize);
        return button;
    }

    public static CheckButton Check(string text, int fontSize = FontSize - 1)
    {
        var check = new CheckButton { Text = text, FocusMode = Control.FocusModeEnum.None };
        check.AddThemeFontOverride("font", Font);
        check.AddThemeFontSizeOverride("font_size", fontSize);
        return check;
    }

    /// <summary>둥근 모서리의 어두운 반투명 패널.</summary>
    public static PanelContainer Panel(float alpha = 0.80f)
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, alpha) };
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }
}
