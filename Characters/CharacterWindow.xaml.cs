using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LilAgents.Windows.Characters;

/// <summary>
/// Transparent window hosting the character sprite.
/// </summary>
public partial class CharacterWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const byte AlphaHitThreshold = 12;
    private const double FallbackHitInsetXRatio = 0.2;
    private const double FallbackHitInsetYRatio = 0.15;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private BitmapSource? _currentFrame;
    private HwndSource? _hwndSource;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    public event Action? OnCharacterClicked;

    public bool IsFacingLeft
    {
        get => FlipTransform.ScaleX < 0;
        set => FlipTransform.ScaleX = value ? -1 : 1;
    }

    public CharacterWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    public void SetFrame(BitmapSource frame)
    {
        _currentFrame = frame;
        CharacterImage.Source = frame;
    }

    public void SetSize(double characterWidth, double characterHeight)
    {
        var snappedWidth = Math.Max(1.0, Math.Round(characterWidth));
        var snappedHeight = Math.Max(1.0, Math.Round(characterHeight));
        Width = snappedWidth;
        Height = snappedHeight;
        CharacterImage.Width = snappedWidth;
        CharacterImage.Height = snappedHeight;
        FlipTransform.CenterX = snappedWidth / 2.0;
        FlipTransform.CenterY = snappedHeight / 2.0;
    }

    public void SetFrameOrigin(double x, double y)
    {
        Left = Math.Round(x);
        Top = Math.Round(y);
    }

    public void SetPosition(double x, double y)
    {
        Left = Math.Round(x - Width / 2.0);
        Top = Math.Round(y - Height);
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsOpaqueAt(e.GetPosition(this)))
        {
            OnCharacterClicked?.Invoke();
            e.Handled = true;
        }

        base.OnMouseLeftButtonDown(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            var raw = lParam.ToInt64();
            var screenX = (short)(raw & 0xFFFF);
            var screenY = (short)((raw >> 16) & 0xFFFF);
            var screenPoint = new Point(screenX, screenY);
            var windowPoint = PointFromScreen(screenPoint);

            if (_hwndSource?.CompositionTarget != null)
            {
                var dipPoint = _hwndSource.CompositionTarget.TransformFromDevice.Transform(screenPoint);
                windowPoint = new Point(dipPoint.X - Left, dipPoint.Y - Top);
            }

            if (!IsOpaqueAt(windowPoint))
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
        }

        return IntPtr.Zero;
    }

    private bool IsOpaqueAt(Point windowPoint)
    {
        if (_currentFrame == null)
        {
            return false;
        }

        if (windowPoint.X < 0 || windowPoint.Y < 0 || windowPoint.X >= ActualWidth || windowPoint.Y >= ActualHeight)
        {
            return false;
        }

        var containerWidth = CharacterImage.ActualWidth;
        var containerHeight = CharacterImage.ActualHeight;
        if (containerWidth <= 0 || containerHeight <= 0)
        {
            return false;
        }

        var scale = Math.Min(containerWidth / _currentFrame.PixelWidth, containerHeight / _currentFrame.PixelHeight);
        if (scale <= 0)
        {
            return false;
        }

        var drawnWidth = _currentFrame.PixelWidth * scale;
        var drawnHeight = _currentFrame.PixelHeight * scale;
        var offsetX = (containerWidth - drawnWidth) / 2.0;
        var offsetY = containerHeight - drawnHeight;

        var imageX = windowPoint.X - offsetX;
        var imageY = windowPoint.Y - offsetY;

        if (imageX < 0 || imageY < 0 || imageX >= drawnWidth || imageY >= drawnHeight)
        {
            return false;
        }

        var pixelX = Math.Clamp((int)(imageX / scale), 0, _currentFrame.PixelWidth - 1);
        var pixelY = Math.Clamp((int)(imageY / scale), 0, _currentFrame.PixelHeight - 1);

        // Keep hit-testing aligned with the rendered frame when visually flipped.
        if (IsFacingLeft)
        {
            pixelX = _currentFrame.PixelWidth - 1 - pixelX;
        }

        var pixel = new byte[4];
        _currentFrame.CopyPixels(new Int32Rect(pixelX, pixelY, 1, 1), pixel, 4, 0);
        if (pixel[3] > AlphaHitThreshold)
        {
            return true;
        }

        // Fallback click area to avoid frustrating misses on semi-transparent edges.
        var coreLeft = offsetX + (drawnWidth * FallbackHitInsetXRatio);
        var coreRight = offsetX + drawnWidth - (drawnWidth * FallbackHitInsetXRatio);
        var coreTop = offsetY + (drawnHeight * FallbackHitInsetYRatio);
        var coreBottom = offsetY + drawnHeight - (drawnHeight * FallbackHitInsetYRatio);

        return windowPoint.X >= coreLeft &&
               windowPoint.X <= coreRight &&
               windowPoint.Y >= coreTop &&
               windowPoint.Y <= coreBottom;
    }
}
