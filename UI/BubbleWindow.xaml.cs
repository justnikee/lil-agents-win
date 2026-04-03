using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using LilAgents.Windows.Themes;

namespace LilAgents.Windows.UI;

/// <summary>
/// Text bubble shown above a character for thinking/completion phrases.
/// </summary>
public partial class BubbleWindow : Window
{
    private const double BubbleHeight = 26.0;
    private const double BubbleHorizontalPadding = 16.0;
    private const double MinBubbleWidth = 48.0;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    private bool _isCompletionStyle;
    private Color? _characterColor;

    public BubbleWindow()
    {
        InitializeComponent();
        ApplyTheme(PopoverTheme.Current, false);
        PopoverTheme.ThemeChanged += OnThemeChanged;
    }

    public void ShowText(string text, bool isCompletion)
    {
        _isCompletionStyle = isCompletion;
        ApplyTheme(ResolveTheme(PopoverTheme.Current), _isCompletionStyle);
        BubbleText.Text = text;
        UpdateBubbleGeometry(text);
        FadeIn();
    }

    public void SetCharacterColor(Color color)
    {
        _characterColor = color;
        ApplyTheme(ResolveTheme(PopoverTheme.Current), _isCompletionStyle);
    }

    public void PositionAboveFrame(double characterLeft, double characterTop, double characterWidth, double characterHeight)
    {
        var x = characterLeft + (characterWidth - Width) / 2.0;
        var y = characterTop + (characterHeight * 0.12) - Height;
        Left = Math.Round(x);
        Top = Math.Round(y);
    }

    public void RaiseToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void FadeOut(Action? onComplete = null)
    {
        if (!IsVisible)
        {
            onComplete?.Invoke();
            return;
        }

        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        anim.Completed += (_, _) =>
        {
            Hide();
            onComplete?.Invoke();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void FadeIn()
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        var anim = new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(180));
        BeginAnimation(OpacityProperty, anim);
        RaiseToTop();
    }

    private void ApplyTheme(PopoverTheme theme, bool completion)
    {
        BubbleBorder.Background = PopoverTheme.Brush(theme.BubbleBackground);
        BubbleBorder.BorderBrush = completion
            ? PopoverTheme.Brush(theme.BubbleCompletionBorderColor)
            : PopoverTheme.Brush(theme.BubbleBorderColor);
        BubbleBorder.CornerRadius = new CornerRadius(theme.BubbleCornerRadius);
        BubbleText.Foreground = completion
            ? PopoverTheme.Brush(theme.BubbleCompletionTextColor)
            : PopoverTheme.Brush(theme.BubbleTextColor);
        BubbleText.FontFamily = theme.BubbleFont;
        BubbleText.FontSize = theme.BubbleFontSize;
        BubbleText.FontWeight = FontWeights.Medium;
    }

    private void UpdateBubbleGeometry(string text)
    {
        var typeface = new Typeface(
            BubbleText.FontFamily,
            BubbleText.FontStyle,
            BubbleText.FontWeight,
            BubbleText.FontStretch);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            BubbleText.FontSize,
            Brushes.Transparent,
            dpi);

        var bubbleWidth = Math.Max(Math.Ceiling(formatted.Width) + (BubbleHorizontalPadding * 2.0), MinBubbleWidth);
        bubbleWidth = Math.Round(bubbleWidth);

        Width = bubbleWidth;
        Height = BubbleHeight;
        BubbleBorder.Width = bubbleWidth;
        BubbleBorder.Height = BubbleHeight;
    }

    private void OnThemeChanged(PopoverTheme theme)
    {
        ApplyTheme(ResolveTheme(theme), _isCompletionStyle);
    }

    protected override void OnClosed(EventArgs e)
    {
        PopoverTheme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    private PopoverTheme ResolveTheme(PopoverTheme baseTheme)
    {
        var themed = _characterColor.HasValue
            ? baseTheme.WithCharacterColor(_characterColor.Value)
            : baseTheme;
        return themed.WithCustomFont();
    }
}
