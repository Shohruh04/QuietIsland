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
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>();
        var bar = new NativeWindow.Rect { Left = 0, Top = 1056, Right = 1920, Bottom = 1080 };
        var buttons = new[] { new Rect(700, 1056, 550, 24), new Rect(2, 1056, 50, 24) };
        var left = TaskbarLayout.Choose(bar, 1700, buttons, 1, "left");
        var right = TaskbarLayout.Choose(bar, 1700, buttons, 1, "right");
        checks["LeftPlacementAvoidsAllButtons"] = left.HasValue && buttons.All(b => !b.IntersectsWith(left.Value));
        checks["RightPlacementAvoidsAllButtons"] = right.HasValue && buttons.All(b => !b.IntersectsWith(right.Value));
        checks["UsesFullTaskbarHeight"] = left?.Height == bar.Height && left?.Top == bar.Top;
        checks["CrowdedTaskbarHidesInsteadOfCoveringApps"] = TaskbarLayout.Choose(bar, 1700, new[] { new Rect(0, 1056, 1700, 24) }, 1, "left") == null;
        var offsetBar = new NativeWindow.Rect { Left = -1920, Top = 1056, Right = 0, Bottom = 1080 };
        checks["NegativeMonitorCoordinates"] = TaskbarLayout.Choose(offsetBar, -200, new[] { new Rect(-1200, 1056, 500, 24) }, 1, "left")?.Left < 0;
        var lyrics = LyricsService.Parse("[offset:100]\n[00:01.50][00:03.25]Test line\n[00:02,00]Another line");
        checks["LyricsMultipleStampsAndOffset"] = lyrics.Count == 3 && Math.Abs(lyrics[0].Seconds - 1.4) < .001 && Math.Abs(lyrics[2].Seconds - 3.15) < .001;
        checks["LyricsSorted"] = lyrics[1].Text == "Another line";
        var timer = new FocusTimer(); timer.Start(TimeSpan.FromSeconds(1)); timer.TogglePause();
        var remaining = timer.Remaining; await Task.Delay(100);
        checks["PausedTimerDoesNotAdvance"] = timer.Remaining == remaining;
        timer.TogglePause(); await Task.Delay(1100);
        checks["TimerExpiresOnce"] = timer.Tick() && !timer.Tick() && !timer.Active;
        var foreground = NativeWindow.GetForegroundWindow();
        var island = new IslandWindow(); island.Show();
        NativeWindow.ShowWindow(island.Handle, 4);
        await Task.Delay(1500);
        var style = NativeWindow.GetWindowLong(island.Handle, NativeWindow.ExStyle);
        checks["LayeredWindow"] = (style & NativeWindow.Layered) != 0;
        checks["NonActivatingStyle"] = (style & NativeWindow.NoActivate) != 0;
        checks["DoesNotActivateOnShow"] = NativeWindow.GetForegroundWindow() == foreground;
        checks["NoTaskbarOrAltTabEntry"] = (style & NativeWindow.ToolWindow) != 0 && !island.ShowInTaskbar;
        island.OpenPanel(); await Task.Delay(100);
        checks["ShowCommandDoesNotStealFocus"] = NativeWindow.GetForegroundWindow() == foreground;
        checks["ControlsAreInteractive"] = (NativeWindow.GetWindowLong(island.Handle, NativeWindow.ExStyle) & NativeWindow.Transparent) == 0;
        var monitor = NativeWindow.PrimaryMonitor();
        checks["FullscreenMatchesMonitor"] = NativeWindow.CoversMonitor(monitor.Monitor, monitor.Monitor);
        checks["MaximizedWorkAreaIsNotFullscreen"] = monitor.Work.Height == monitor.Monitor.Height || !NativeWindow.CoversMonitor(monitor.Work, monitor.Monitor);
        island.Close();
        Directory.CreateDirectory(AppSettings.DataDirectory);
        File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "verification.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
