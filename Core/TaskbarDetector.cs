using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace LilAgents.Windows.Core;

/// <summary>
/// Detects Windows taskbar position, size, and auto-hide state.
/// Replaces DockVisibility.swift — uses Win32 Shell API.
/// </summary>
public static class TaskbarDetector
{
    // Win32 constants
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABM_GETSTATE = 0x00000004;
    private const int ABS_AUTOHIDE = 0x01;

    public sealed record DisplayInfo
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public Rect Bounds { get; init; }
        public bool IsPrimary { get; init; }
    }

    public enum TaskbarEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    public record TaskbarInfo
    {
        public Rect Bounds { get; init; }
        public TaskbarEdge Edge { get; init; }
        public bool IsAutoHide { get; init; }
        public double VirtualLeft { get; init; }
        public double VirtualTop { get; init; }
        public double VirtualWidth { get; init; }
        public double VirtualHeight { get; init; }

        /// <summary>
        /// The Y position where characters should walk (just above the taskbar).
        /// </summary>
        public double CharacterWalkY => Edge switch
        {
            TaskbarEdge.Bottom => Bounds.Top,
            TaskbarEdge.Top => Bounds.Bottom,
            _ => VirtualTop + VirtualHeight - Bounds.Height
        };

        /// <summary>
        /// The walkable range for characters (horizontal).
        /// </summary>
        public double WalkAreaLeft => Edge is TaskbarEdge.Left ? Bounds.Right : VirtualLeft;
        public double WalkAreaRight => Edge is TaskbarEdge.Right ? Bounds.Left : VirtualLeft + VirtualWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    /// <summary>
    /// Gets the current taskbar information.
    /// </summary>
    public static TaskbarInfo GetTaskbarInfo()
    {
        var data = new APPBARDATA();
        data.cbSize = (uint)Marshal.SizeOf(data);
        var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not locate the Windows taskbar window.");
        }

        data.hWnd = taskbarHwnd;

        // Get taskbar position
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0)
        {
            throw new InvalidOperationException("Failed to query taskbar position.");
        }

        // Get auto-hide state
        var state = SHAppBarMessage(ABM_GETSTATE, ref data);
        bool isAutoHide = (state & ABS_AUTOHIDE) != 0;

        // Use virtual screen dimensions to support non-primary taskbar placements.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;

        var bounds = new Rect(
            data.rc.Left,
            data.rc.Top,
            data.rc.Right - data.rc.Left,
            data.rc.Bottom - data.rc.Top
        );

        // Convert from physical pixels to WPF DIPs
        var dpiScale = GetDpiScale(taskbarHwnd);
        bounds = new Rect(
            bounds.X / dpiScale,
            bounds.Y / dpiScale,
            bounds.Width / dpiScale,
            bounds.Height / dpiScale
        );

        return new TaskbarInfo
        {
            Bounds = bounds,
            Edge = (TaskbarEdge)data.uEdge,
            IsAutoHide = isAutoHide,
            VirtualLeft = virtualLeft,
            VirtualTop = virtualTop,
            VirtualWidth = virtualWidth,
            VirtualHeight = virtualHeight,
        };
    }

    /// <summary>
    /// Whether characters should be visible (hide if taskbar is auto-hidden).
    /// </summary>
    public static bool ShouldShowCharacters()
    {
        var info = GetTaskbarInfo();
        if (!info.IsAutoHide)
        {
            return true;
        }

        var thickness = info.Edge is TaskbarEdge.Left or TaskbarEdge.Right
            ? info.Bounds.Width
            : info.Bounds.Height;
        return thickness > 4;
    }

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var screens = Screen.AllScreens;
        var scale = GetSystemDpi() / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var displays = new List<DisplayInfo>(screens.Length);
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var b = screen.Bounds;
            displays.Add(new DisplayInfo
            {
                Index = i,
                Name = BuildDisplayName(i, screen.Primary),
                IsPrimary = screen.Primary,
                Bounds = new Rect(
                    b.Left / scale,
                    b.Top / scale,
                    b.Width / scale,
                    b.Height / scale)
            });
        }

        return displays;
    }

    private static string BuildDisplayName(int index, bool primary)
    {
        var baseName = $"Display {index + 1}";
        return primary ? $"{baseName} (Primary)" : baseName;
    }

    private static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return dpi / 96.0;
                }
            }
        }
        catch { }

        return GetSystemDpi() / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();

    private static double GetSystemDpi()
    {
        try { return GetDpiForSystem(); }
        catch { return 96.0; }
    }
}
