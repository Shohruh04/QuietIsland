using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuietIsland;

internal static class NativeWindow
{
    public const int ExStyle = -20;
    public const int Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000, Layered = 0x80000;
    public const int HotkeyMessage = 0x0312, MouseActivate = 0x0021;
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool Contains(Point p, int gap = 0) => p.X >= Left - gap && p.X < Right + gap && p.Y >= Top - gap && p.Y < Bottom + gap;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct AppbarData { public int Size; public IntPtr Window; public uint Callback, Edge; public Rect Bounds; public IntPtr Param; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    public static void RaiseWithoutFocus(IntPtr h) => SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr h, int n, int value);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? caption);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? caption);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppbarData data);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out Rect rect, int size);

    public static MonitorInfo PrimaryMonitor()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(taskbar, 1), ref info);
        return info;
    }
    public static IntPtr TaskbarHandle() => FindWindow("Shell_TrayWnd", null);

    // Pixels, not WPF DIPs. Query only on placement changes, never every frame.
    public static (Rect bounds, int trayLeft, bool autoHide) Taskbar()
    {
        var h = FindWindow("Shell_TrayWnd", null);
        var data = new AppbarData { Size = Marshal.SizeOf<AppbarData>(), Window = h };
        var ok = SHAppBarMessage(5, ref data) != UIntPtr.Zero;
        var rect = data.Bounds;
        if (!ok) GetWindowRect(h, out rect);
        var tray = FindWindowEx(h, IntPtr.Zero, "TrayNotifyWnd", null);
        var left = tray != IntPtr.Zero && GetWindowRect(tray, out var tr) ? tr.Left : rect.Right - 260;
        var state = SHAppBarMessage(4, ref data).ToUInt64();
        return (rect, left, (state & 1) != 0);
    }

    public static bool IsFullscreen(IntPtr foreground, IntPtr ownWindow, IntPtr monitor)
    {
        if (foreground == IntPtr.Zero || foreground == ownWindow || IsIconic(foreground) || MonitorFromWindow(foreground, 0) != monitor) return false;
        var name = new StringBuilder(128);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        if (DwmGetWindowAttribute(foreground, 9, out var rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(foreground, out rect)) return false;
        return CoversMonitor(rect, info.Monitor);
    }
    internal static bool CoversMonitor(Rect r, Rect m) => r.Left <= m.Left + 1 && r.Top <= m.Top + 1 && r.Right >= m.Right - 1 && r.Bottom >= m.Bottom - 1;

    public static void SetPassive(IntPtr h, bool passive)
    {
        var style = GetWindowLong(h, ExStyle) | NoActivate | ToolWindow;
        style = passive ? style | Transparent : style & ~Transparent;
        SetWindowLong(h, ExStyle, style);
    }
}
