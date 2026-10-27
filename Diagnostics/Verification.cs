using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace QuietIsland;
internal static class Verification
{
    public static async Task<bool> RunAsync(bool writeReport = true)
    {
        var checks = new Dictionary<string, bool>();
        var island = new IslandWindow();
        var foreground = NativeWindow.GetForegroundWindow();
        island.Show();
        NativeWindow.ShowWindow(island.Handle, 4);
        var foregroundAfterShow = NativeWindow.GetForegroundWindow();
        await Task.Delay(1500);
        var style = NativeWindow.GetWindowLong(island.Handle, NativeWindow.ExStyle);
        checks["LayeredWindow"] = (style & NativeWindow.Layered) != 0;
        checks["NonActivatingStyle"] = (style & NativeWindow.NoActivate) != 0;
        // Compare around the synchronous operation. The user can switch apps
        // while asynchronous media/taskbar initialization is being awaited.
        checks["DoesNotActivateOnShow"] = foregroundAfterShow == foreground && NativeWindow.GetForegroundWindow() != island.Handle;
        checks["NoTaskbarOrAltTabEntry"] = (style & NativeWindow.ToolWindow) != 0 && !island.ShowInTaskbar;
        foreground = NativeWindow.GetForegroundWindow();
        island.OpenPanel();
        checks["ShowCommandDoesNotStealFocus"] = NativeWindow.GetForegroundWindow() == foreground && foreground != island.Handle;
        checks["ControlsAreInteractive"] = (NativeWindow.GetWindowLong(island.Handle, NativeWindow.ExStyle) & NativeWindow.Transparent) == 0;
        var actualBar = NativeWindow.Taskbar(NativeWindow.TaskbarHandle(new JsonSettingsStore().Load().MonitorDevice)).bounds;
        checks["VisibleWindowStaysInsideTaskbar"] = island.Opacity == 0 || (NativeWindow.GetWindowRect(island.Handle, out var actualWindow) && actualWindow.Top >= actualBar.Top && actualWindow.Bottom <= actualBar.Bottom);
        var monitor = NativeWindow.PrimaryMonitor();
        checks["FullscreenMatchesMonitor"] = NativeWindow.CoversMonitor(monitor.Monitor, monitor.Monitor);
        checks["MaximizedWorkAreaIsNotFullscreen"] = monitor.Work.Height == monitor.Monitor.Height || !NativeWindow.CoversMonitor(monitor.Work, monitor.Monitor);
        var marquee = new MarqueeText { Text = new string('W', 80), FontSize = 13 };
        var host = new Window { Content = marquee, Width = 100, Height = 30, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        NativeWindow.SetPassive(new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle(), false);
        host.Show(); await Task.Delay(150);
        checks["LongTextScrollsWithoutExpandingWindow"] = marquee.IsScrolling && host.ActualWidth == 100;
        marquee.IsActive = false; await Task.Delay(50);
        checks["HiddenMarqueeStopsAnimation"] = !marquee.IsScrolling;
        marquee.Text = "Short"; marquee.IsActive = true; await Task.Delay(50);
        checks["FittingTextDoesNotAnimate"] = !marquee.IsScrolling;
        host.Close(); island.Close();
        var json = JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (writeReport)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "verification.json"), json);
        }
        return checks.Values.All(passed => passed);
    }
}
