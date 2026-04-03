using System.Windows;
using System.Windows.Media;
using LilAgents.Windows.Sessions;

namespace LilAgents.Windows.Themes;

/// <summary>
/// Popover, terminal, and bubble styling presets ported from PopoverTheme.swift.
/// </summary>
public record PopoverTheme
{
    public string Name { get; init; } = "Peach";
    public TitleFormat TitleFormat { get; init; } = TitleFormat.Capitalized;

    // Popover
    public Color BackgroundColor { get; init; }
    public Color BorderColor { get; init; }
    public double BorderWidth { get; init; }
    public double CornerRadius { get; init; }
    public Color ShadowColor { get; init; }
    public double ShadowRadius { get; init; }

    // Title bar
    public Color TitleBarBackground { get; init; }
    public Color TitleBarTextColor { get; init; }
    public Color TitleBarButtonColor { get; init; }
    public Color TitleBarButtonHoverBackground { get; init; }
    public Color TitleBarSeparatorColor { get; init; }
    public FontFamily TitleBarFont { get; init; } = new("Segoe UI");
    public double TitleBarFontSize { get; init; }
    public FontWeight TitleBarFontWeight { get; init; }

    // Terminal
    public Color TerminalBackground { get; init; }
    public Color TerminalTextColor { get; init; }
    public Color TerminalSecondaryTextColor { get; init; }
    public Color TerminalLinkColor { get; init; }
    public Color TerminalErrorColor { get; init; }
    public Color TerminalSuccessColor { get; init; }
    public Color TerminalCodeBackground { get; init; }
    public Color TerminalCodeTextColor { get; init; }
    public Color TerminalSelectionColor { get; init; }
    public Color TerminalToolCallBackground { get; init; }
    public Color TerminalToolCallBorderColor { get; init; }
    public Color TerminalToolCallTextColor { get; init; }
    public FontFamily TerminalFont { get; init; } = new("Consolas");
    public FontFamily TerminalMonoFont { get; init; } = new("Consolas");
    public double TerminalFontSize { get; init; }
    public double TerminalCodeFontSize { get; init; }

    // Input
    public Color InputBackground { get; init; }
    public Color InputTextColor { get; init; }
    public Color InputPlaceholderColor { get; init; }
    public Color InputBorderColor { get; init; }
    public double InputCornerRadius { get; init; }
    public FontFamily InputFont { get; init; } = new("Segoe UI");
    public double InputFontSize { get; init; }

    // Bubble
    public Color BubbleBackground { get; init; }
    public Color BubbleTextColor { get; init; }
    public Color BubbleBorderColor { get; init; }
    public Color BubbleCompletionBorderColor { get; init; }
    public Color BubbleCompletionTextColor { get; init; }
    public double BubbleCornerRadius { get; init; }
    public FontFamily BubbleFont { get; init; } = new("Segoe UI");
    public double BubbleFontSize { get; init; }

    // Scrollbar
    public Color ScrollbarThumbColor { get; init; }
    public Color ScrollbarTrackColor { get; init; }

    public string TitleString(AgentProvider provider) => provider.TitleString(TitleFormat);

    public PopoverTheme WithCharacterColor(Color color)
    {
        if (!Name.Equals("Peach", StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var border = Color.FromArgb(153, color.R, color.G, color.B);
        var tint = Color.FromArgb(255,
            (byte)Math.Min(255, color.R * 0.3 + 179),
            (byte)Math.Min(255, color.G * 0.3 + 179),
            (byte)Math.Min(255, color.B * 0.3 + 179));
        var bubbleBg = Color.FromArgb(242,
            (byte)Math.Min(255, color.R * 0.15 + 217),
            (byte)Math.Min(255, color.G * 0.15 + 217),
            (byte)Math.Min(255, color.B * 0.15 + 217));

        return this with
        {
            BorderColor = border,
            TitleBarBackground = tint,
            TitleBarTextColor = color,
            TitleBarButtonColor = color,
            TitleBarSeparatorColor = Color.FromArgb(64, color.R, color.G, color.B),
            TerminalLinkColor = color,
            BubbleBackground = bubbleBg,
            BubbleBorderColor = border,
        };
    }

    public PopoverTheme WithCustomFont()
    {
        if (Name.Equals("Midnight", StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var fontFamily = new FontFamily(CustomFontFamilyName);
        var baseSize = CustomFontSize;

        return this with
        {
            TerminalFont = fontFamily,
            TerminalFontSize = baseSize,
            InputFont = fontFamily,
            InputFontSize = baseSize,
            BubbleFont = fontFamily,
            BubbleFontSize = Math.Max(10, baseSize - 1),
            TerminalCodeFontSize = Math.Max(10, baseSize - 1),
        };
    }

    // Presets from PopoverTheme.swift
    public static PopoverTheme Peach => new()
    {
        Name = "Peach",
        TitleFormat = TitleFormat.LowercaseTilde,
        BackgroundColor = Rgba(255, 247, 235, 247),
        BorderColor = Rgba(242, 140, 166, 204),
        BorderWidth = 2.5,
        CornerRadius = 24,
        ShadowColor = Rgba(0, 0, 0, 80),
        ShadowRadius = 20,
        TitleBarBackground = Rgba(250, 237, 224, 255),
        TitleBarTextColor = Rgba(217, 89, 115, 255),
        TitleBarButtonColor = Rgba(217, 89, 115, 200),
        TitleBarButtonHoverBackground = Rgba(255, 255, 255, 30),
        TitleBarSeparatorColor = Rgba(242, 140, 166, 64),
        TitleBarFont = new FontFamily("Segoe UI Semibold"),
        TitleBarFontSize = 12,
        TitleBarFontWeight = FontWeights.ExtraBold,
        TerminalBackground = Rgba(255, 247, 235, 247),
        TerminalTextColor = Rgba(51, 46, 56, 255),
        TerminalSecondaryTextColor = Rgba(128, 120, 133, 255),
        TerminalLinkColor = Rgba(217, 89, 115, 255),
        TerminalErrorColor = Rgba(230, 77, 51, 255),
        TerminalSuccessColor = Rgba(77, 184, 128, 255),
        TerminalCodeBackground = Rgba(255, 250, 242, 255),
        TerminalCodeTextColor = Rgba(51, 46, 56, 255),
        TerminalSelectionColor = Rgba(217, 89, 115, 70),
        TerminalToolCallBackground = Rgba(250, 237, 224, 255),
        TerminalToolCallBorderColor = Rgba(242, 140, 166, 90),
        TerminalToolCallTextColor = Rgba(128, 120, 133, 255),
        TerminalFont = new FontFamily("Segoe UI"),
        TerminalMonoFont = new FontFamily("Consolas"),
        TerminalFontSize = 12,
        TerminalCodeFontSize = 11,
        InputBackground = Rgba(255, 250, 242, 255),
        InputTextColor = Rgba(51, 46, 56, 255),
        InputPlaceholderColor = Rgba(128, 120, 133, 200),
        InputBorderColor = Rgba(242, 140, 166, 120),
        InputCornerRadius = 14,
        InputFont = new FontFamily("Segoe UI"),
        InputFontSize = 12,
        BubbleBackground = Rgba(255, 242, 230, 242),
        BubbleTextColor = Rgba(140, 128, 133, 255),
        BubbleBorderColor = Rgba(242, 140, 166, 153),
        BubbleCompletionBorderColor = Rgba(77, 191, 128, 179),
        BubbleCompletionTextColor = Rgba(51, 153, 102, 255),
        BubbleCornerRadius = 14,
        BubbleFont = new FontFamily("Segoe UI Semibold"),
        BubbleFontSize = 11,
        ScrollbarThumbColor = Rgba(217, 89, 115, 80),
        ScrollbarTrackColor = Colors.Transparent,
    };

    public static PopoverTheme Midnight => new()
    {
        Name = "Midnight",
        TitleFormat = TitleFormat.Uppercase,
        BackgroundColor = Rgba(18, 18, 18, 245),
        BorderColor = Rgba(255, 102, 0, 179),
        BorderWidth = 1.5,
        CornerRadius = 12,
        ShadowColor = Rgba(0, 0, 0, 100),
        ShadowRadius = 20,
        TitleBarBackground = Rgba(26, 26, 26, 255),
        TitleBarTextColor = Rgba(255, 102, 0, 255),
        TitleBarButtonColor = Rgba(255, 102, 0, 190),
        TitleBarButtonHoverBackground = Rgba(255, 255, 255, 30),
        TitleBarSeparatorColor = Rgba(255, 102, 0, 77),
        TitleBarFont = new FontFamily("Consolas"),
        TitleBarFontSize = 10,
        TitleBarFontWeight = FontWeights.Bold,
        TerminalBackground = Rgba(18, 18, 18, 245),
        TerminalTextColor = Colors.White,
        TerminalSecondaryTextColor = Rgba(153, 153, 153, 255),
        TerminalLinkColor = Rgba(255, 102, 0, 255),
        TerminalErrorColor = Rgba(255, 77, 51, 255),
        TerminalSuccessColor = Rgba(102, 166, 102, 255),
        TerminalCodeBackground = Rgba(31, 31, 31, 255),
        TerminalCodeTextColor = Colors.White,
        TerminalSelectionColor = Rgba(255, 102, 0, 80),
        TerminalToolCallBackground = Rgba(26, 26, 26, 255),
        TerminalToolCallBorderColor = Rgba(255, 102, 0, 102),
        TerminalToolCallTextColor = Rgba(255, 102, 0, 255),
        TerminalFont = new FontFamily("Consolas"),
        TerminalMonoFont = new FontFamily("Consolas"),
        TerminalFontSize = 11.5,
        TerminalCodeFontSize = 10.5,
        InputBackground = Rgba(31, 31, 31, 255),
        InputTextColor = Colors.White,
        InputPlaceholderColor = Rgba(153, 153, 153, 190),
        InputBorderColor = Rgba(255, 102, 0, 80),
        InputCornerRadius = 4,
        InputFont = new FontFamily("Consolas"),
        InputFontSize = 11.5,
        BubbleBackground = Rgba(26, 26, 26, 235),
        BubbleTextColor = Rgba(179, 179, 179, 255),
        BubbleBorderColor = Rgba(255, 102, 0, 153),
        BubbleCompletionBorderColor = Rgba(77, 204, 77, 179),
        BubbleCompletionTextColor = Rgba(77, 217, 77, 255),
        BubbleCornerRadius = 12,
        BubbleFont = new FontFamily("Consolas"),
        BubbleFontSize = 10,
        ScrollbarThumbColor = Rgba(255, 102, 0, 90),
        ScrollbarTrackColor = Colors.Transparent,
    };

    public static PopoverTheme Cloud => new()
    {
        Name = "Cloud",
        TitleFormat = TitleFormat.LowercaseTilde,
        BackgroundColor = Rgba(240, 242, 245, 250),
        BorderColor = Rgba(199, 204, 214, 153),
        BorderWidth = 1,
        CornerRadius = 16,
        ShadowColor = Rgba(0, 0, 0, 45),
        ShadowRadius = 18,
        TitleBarBackground = Rgba(224, 230, 237, 255),
        TitleBarTextColor = Rgba(77, 77, 89, 255),
        TitleBarButtonColor = Rgba(77, 77, 89, 190),
        TitleBarButtonHoverBackground = Rgba(0, 0, 0, 20),
        TitleBarSeparatorColor = Rgba(204, 209, 217, 102),
        TitleBarFont = new FontFamily("Segoe UI Semibold"),
        TitleBarFontSize = 12,
        TitleBarFontWeight = FontWeights.SemiBold,
        TerminalBackground = Rgba(240, 242, 245, 250),
        TerminalTextColor = Rgba(38, 38, 51, 255),
        TerminalSecondaryTextColor = Rgba(128, 128, 140, 255),
        TerminalLinkColor = Rgba(0, 120, 214, 255),
        TerminalErrorColor = Rgba(217, 51, 38, 255),
        TerminalSuccessColor = Rgba(51, 166, 77, 255),
        TerminalCodeBackground = Rgba(255, 255, 255, 255),
        TerminalCodeTextColor = Rgba(38, 38, 51, 255),
        TerminalSelectionColor = Rgba(0, 120, 214, 60),
        TerminalToolCallBackground = Rgba(224, 230, 237, 255),
        TerminalToolCallBorderColor = Rgba(199, 204, 214, 120),
        TerminalToolCallTextColor = Rgba(77, 77, 89, 255),
        TerminalFont = new FontFamily("Segoe UI"),
        TerminalMonoFont = new FontFamily("Consolas"),
        TerminalFontSize = 12,
        TerminalCodeFontSize = 11,
        InputBackground = Colors.White,
        InputTextColor = Rgba(38, 38, 51, 255),
        InputPlaceholderColor = Rgba(128, 128, 140, 200),
        InputBorderColor = Rgba(199, 204, 214, 170),
        InputCornerRadius = 8,
        InputFont = new FontFamily("Segoe UI"),
        InputFontSize = 12,
        BubbleBackground = Rgba(240, 242, 247, 242),
        BubbleTextColor = Rgba(115, 120, 133, 255),
        BubbleBorderColor = Rgba(0, 120, 214, 102),
        BubbleCompletionBorderColor = Rgba(51, 179, 77, 153),
        BubbleCompletionTextColor = Rgba(38, 140, 51, 255),
        BubbleCornerRadius = 12,
        BubbleFont = new FontFamily("Segoe UI Semibold"),
        BubbleFontSize = 10,
        ScrollbarThumbColor = Rgba(0, 120, 214, 70),
        ScrollbarTrackColor = Colors.Transparent,
    };

    public static PopoverTheme Moss => new()
    {
        Name = "Moss",
        TitleFormat = TitleFormat.Capitalized,
        BackgroundColor = Rgba(209, 214, 199, 250),
        BorderColor = Rgba(140, 148, 128, 204),
        BorderWidth = 2,
        CornerRadius = 10,
        ShadowColor = Rgba(0, 0, 0, 60),
        ShadowRadius = 16,
        TitleBarBackground = Rgba(184, 191, 173, 255),
        TitleBarTextColor = Rgba(38, 43, 31, 255),
        TitleBarButtonColor = Rgba(38, 43, 31, 190),
        TitleBarButtonHoverBackground = Rgba(0, 0, 0, 20),
        TitleBarSeparatorColor = Rgba(140, 148, 128, 128),
        TitleBarFont = new FontFamily("Segoe UI Semibold"),
        TitleBarFontSize = 11,
        TitleBarFontWeight = FontWeights.Bold,
        TerminalBackground = Rgba(209, 214, 199, 250),
        TerminalTextColor = Rgba(26, 31, 20, 255),
        TerminalSecondaryTextColor = Rgba(89, 97, 77, 255),
        TerminalLinkColor = Rgba(51, 56, 38, 255),
        TerminalErrorColor = Rgba(153, 38, 26, 255),
        TerminalSuccessColor = Rgba(38, 102, 38, 255),
        TerminalCodeBackground = Rgba(224, 230, 214, 255),
        TerminalCodeTextColor = Rgba(26, 31, 20, 255),
        TerminalSelectionColor = Rgba(51, 56, 38, 65),
        TerminalToolCallBackground = Rgba(184, 191, 173, 255),
        TerminalToolCallBorderColor = Rgba(140, 148, 128, 120),
        TerminalToolCallTextColor = Rgba(89, 97, 77, 255),
        TerminalFont = new FontFamily("Segoe UI"),
        TerminalMonoFont = new FontFamily("Consolas"),
        TerminalFontSize = 11,
        TerminalCodeFontSize = 10,
        InputBackground = Rgba(224, 230, 214, 255),
        InputTextColor = Rgba(26, 31, 20, 255),
        InputPlaceholderColor = Rgba(89, 97, 77, 190),
        InputBorderColor = Rgba(140, 148, 128, 130),
        InputCornerRadius = 3,
        InputFont = new FontFamily("Segoe UI"),
        InputFontSize = 11,
        BubbleBackground = Rgba(209, 214, 199, 242),
        BubbleTextColor = Rgba(102, 107, 97, 255),
        BubbleBorderColor = Rgba(140, 148, 128, 179),
        BubbleCompletionBorderColor = Rgba(51, 128, 51, 179),
        BubbleCompletionTextColor = Rgba(38, 102, 38, 255),
        BubbleCornerRadius = 8,
        BubbleFont = new FontFamily("Segoe UI Semibold"),
        BubbleFontSize = 10,
        ScrollbarThumbColor = Rgba(140, 148, 128, 90),
        ScrollbarTrackColor = Colors.Transparent,
    };

    public static PopoverTheme[] AllThemes => [Peach, Midnight, Cloud, Moss];

    public static string CustomFontFamilyName { get; set; } = "Segoe UI";
    public static double CustomFontSize { get; set; } = 13;

    private static PopoverTheme _current = Peach;
    public static PopoverTheme Current
    {
        get => _current;
        set
        {
            _current = value;
            ThemeChanged?.Invoke(value);
        }
    }

    public static event Action<PopoverTheme>? ThemeChanged;

    public static PopoverTheme FromName(string? name) => name switch
    {
        "Midnight" => Midnight,
        "Cloud" => Cloud,
        "Moss" => Moss,
        _ => Peach,
    };

    public static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static Color Rgba(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);
}
