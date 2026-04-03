using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LilAgents.Windows.Sessions;
using LilAgents.Windows.Themes;

namespace LilAgents.Windows.UI;

/// <summary>
/// Chat popover anchored above a character.
/// </summary>
public partial class PopoverWindow : Window
{
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

    private AgentProvider _currentProvider;
    private bool _suppressDeactivateClose;
    private Color? _characterColor;

    public event Action<string>? OnMessageSubmitted;
    public event Action? OnRefreshSession;
    public event Action<AgentProvider>? OnProviderChanged;
    public event Action? OnRequestClose;

    public TerminalControl TerminalView => Terminal;

    public PopoverWindow(AgentProvider provider)
    {
        InitializeComponent();
        _currentProvider = provider;
        Terminal.OnMessageSubmitted += message => OnMessageSubmitted?.Invoke(message);
        Terminal.OnClearRequested += () => OnRefreshSession?.Invoke();
        PopoverTheme.ThemeChanged += ApplyTheme;
        SetProvider(provider);
        ApplyTheme(PopoverTheme.Current);
    }

    public void SetCharacterColor(Color color)
    {
        _characterColor = color;
        ApplyTheme(PopoverTheme.Current);
    }

    public void SetProvider(AgentProvider provider)
    {
        _currentProvider = provider;
        ProviderNameText.Text = PopoverTheme.Current.TitleString(provider);
        Terminal.SetProvider(provider);
    }

    public void PositionAboveCharacter(double characterCenterX, double characterTopY)
    {
        var left = characterCenterX - Width / 2.0;
        var top = characterTopY - Height + 15;

        var minX = SystemParameters.VirtualScreenLeft + 4;
        var minY = SystemParameters.VirtualScreenTop + 4;
        var maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width - 4;
        var maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height - 4;

        Left = Math.Round(Math.Max(minX, Math.Min(left, maxX)));
        Top = Math.Round(Math.Max(minY, Math.Min(top, maxY)));
    }

    public void ShowWithAnimation()
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        var anim = new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
        RaiseToTop();
        Activate();
        Terminal.FocusInput();
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

    public void HideWithAnimation(Action? onHidden = null)
    {
        if (!IsVisible)
        {
            onHidden?.Invoke();
            return;
        }

        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) =>
        {
            Hide();
            onHidden?.Invoke();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void ApplyTheme(PopoverTheme theme)
    {
        var resolved = ResolveTheme(theme);

        MainBorder.Background = PopoverTheme.Brush(resolved.BackgroundColor);
        MainBorder.BorderBrush = PopoverTheme.Brush(resolved.BorderColor);
        MainBorder.BorderThickness = new Thickness(resolved.BorderWidth);
        MainBorder.CornerRadius = new CornerRadius(resolved.CornerRadius);

        if (MainBorder.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
        {
            shadow.Color = resolved.ShadowColor;
            shadow.BlurRadius = resolved.ShadowRadius;
            shadow.Opacity = Math.Clamp(resolved.ShadowColor.A / 255.0, 0.15, 0.7);
        }

        TitleBar.Background = PopoverTheme.Brush(resolved.TitleBarBackground);
        TitleBar.CornerRadius = new CornerRadius(resolved.CornerRadius, resolved.CornerRadius, 0, 0);
        ProviderNameText.Foreground = PopoverTheme.Brush(resolved.TitleBarTextColor);
        ProviderNameText.FontFamily = resolved.TitleBarFont;
        ProviderNameText.FontSize = resolved.TitleBarFontSize;
        ProviderNameText.FontWeight = resolved.TitleBarFontWeight;
        Separator.Background = PopoverTheme.Brush(resolved.TitleBarSeparatorColor);

        var buttonColor = PopoverTheme.Brush(resolved.TitleBarButtonColor);
        RefreshButton.Foreground = buttonColor;
        CopyButton.Foreground = buttonColor;
        Resources["TitleIconHoverBrush"] = PopoverTheme.Brush(resolved.TitleBarButtonHoverBackground);
        Resources["TitleIconPressedBrush"] = PopoverTheme.Brush(WithAlpha(resolved.TitleBarButtonHoverBackground, 1.25));

        Terminal.ApplyTheme(resolved);
        ProviderNameText.Text = resolved.TitleString(_currentProvider);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void ProviderName_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _suppressDeactivateClose = true;

        var menu = new ContextMenu
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = ProviderNameText,
            StaysOpen = false
        };

        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            var item = new MenuItem
            {
                Header = provider.DisplayName(),
                IsChecked = provider == _currentProvider,
                IsCheckable = true,
                IsEnabled = provider.IsAvailable()
            };

            var selected = provider;
            item.Click += (_, _) =>
            {
                _currentProvider = selected;
                SetProvider(selected);
                OnProviderChanged?.Invoke(selected);
            };
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            _suppressDeactivateClose = false;
            Activate();
        };
        menu.IsOpen = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        OnRefreshSession?.Invoke();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Terminal.CopyLastAssistantToClipboard();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnRequestClose?.Invoke();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible && !_suppressDeactivateClose)
        {
            OnRequestClose?.Invoke();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        PopoverTheme.ThemeChanged -= ApplyTheme;
        base.OnClosed(e);
    }

    private PopoverTheme ResolveTheme(PopoverTheme baseTheme)
    {
        var themed = _characterColor.HasValue
            ? baseTheme.WithCharacterColor(_characterColor.Value)
            : baseTheme;
        return themed.WithCustomFont();
    }

    private static Color WithAlpha(Color color, double multiplier)
    {
        var alpha = (byte)Math.Clamp(color.A * multiplier, 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
