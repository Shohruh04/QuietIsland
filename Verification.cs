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
        checks["NoEmptyMarginAtLeftEdge"] = TaskbarLayout.Choose(bar, 1700, new[] { new Rect(700, 1056, 550, 24) }, 1, "left")?.Left == 0;
        var largeBar = new NativeWindow.Rect { Left = 0, Top = 2064, Right = 3840, Bottom = 2160 };
        var scaled = TaskbarLayout.Choose(largeBar, 3400, new[] { new Rect(1800, 2064, 800, 96) }, 2, "left");
        checks["ScaledTaskbarUsesPhysicalPixels"] = scaled?.Height == 96 && scaled?.Width == 960;
        checks["CustomWidthUsesAvailableSpace"] = TaskbarLayout.Choose(bar, 1700, new[] { new Rect(700, 1056, 550, 24) }, 1, "left", 600)?.Width == 600;
        var clippedWidth = TaskbarLayout.Choose(bar, 1700, new[] { new Rect(700, 1056, 550, 24) }, 1, "left", 1200);
        checks["LargeCustomWidthDoesNotCoverButtons"] = clippedWidth?.Width < 700 && clippedWidth?.Height == bar.Height;
        checks["CrowdedTaskbarHidesInsteadOfCoveringApps"] = TaskbarLayout.Choose(bar, 1700, new[] { new Rect(0, 1056, 1700, 24) }, 1, "left") == null;
        var offsetBar = new NativeWindow.Rect { Left = -1920, Top = 1056, Right = 0, Bottom = 1080 };
        checks["NegativeMonitorCoordinates"] = TaskbarLayout.Choose(offsetBar, -200, new[] { new Rect(-1200, 1056, 500, 24) }, 1, "left")?.Left < 0;
        var scan = new TaskbarSpace(); var scanTime = DateTimeOffset.UtcNow;
        scan.AcceptScan(new IntPtr(1), bar, 1700, buttons, scanTime);
        scan.AcceptScan(new IntPtr(1), bar, 1700, null, scanTime.AddSeconds(30));
        checks["TransientTaskbarScanKeepsSafePlacement"] = scan.Valid && scan.Buttons.Count == buttons.Length;
        scan.AcceptScan(new IntPtr(1), bar, 1700, null, scanTime.AddSeconds(32));
        checks["UncertainTaskbarPlacementExpires"] = !scan.Valid;
        scan.AcceptScan(new IntPtr(1), bar, 1700, buttons, scanTime.AddSeconds(3));
        scan.AcceptScan(new IntPtr(2), bar, 1700, null, scanTime.AddSeconds(3.1));
        checks["ExplorerReplacementInvalidatesCachedPlacement"] = !scan.Valid;
        scan.AcceptScan(new IntPtr(1), bar, 1700, buttons, scanTime.AddSeconds(4));
        scan.AcceptScan(new IntPtr(1), largeBar, 3400, null, scanTime.AddSeconds(4.1));
        checks["DisplayChangeInvalidatesCachedPlacement"] = !scan.Valid;
        checks["SeekPositionClampsOutsideTrack"] = IslandWindow.SeekPosition(-100, 400, 180) == 0 && IslandWindow.SeekPosition(500, 400, 180) == 180;
        checks["SeekPositionMatchesPointerAcrossTrack"] = new[] { .1, .5, .9 }.All(fraction => Math.Abs(IslandWindow.SeekPosition(2.5 + fraction * 395, 400, 180) - fraction * 180) < .001);
        var requested = DateTimeOffset.UtcNow;
        checks["SeekRejectsCachedOldTimeline"] = !MediaService.ConfirmsSeek(16, requested.AddSeconds(-1), 78, requested.AddSeconds(-1), requested, requested.AddMilliseconds(100), 1);
        checks["SeekConfirmsBeforeApiCompletion"] = MediaService.ConfirmsSeek(78.1, requested.AddMilliseconds(100), 78, requested.AddSeconds(-1), requested, requested.AddMilliseconds(300), 1);
        checks["SeekConfirmsBackwardWhilePaused"] = MediaService.ConfirmsSeek(10, requested.AddMilliseconds(50), 10, requested.AddSeconds(-1), requested, requested.AddMilliseconds(300), 0);
        checks["EnglishAndRussianLabels"] = UiText.Translate("Прозрачность фона", "en") == "Background transparency" && UiText.Translate("Прозрачность фона", "ru") == "Прозрачность фона";
        var lyrics = LyricsService.Parse("[offset:100]\n[00:01.50][00:03.25]Test line\n[00:02,00]Another line");
        checks["LyricsMultipleStampsAndOffset"] = lyrics.Count == 3 && Math.Abs(lyrics[0].Seconds - 1.4) < .001 && Math.Abs(lyrics[2].Seconds - 3.15) < .001;
        checks["LyricsSorted"] = lyrics[1].Text == "Another line";
        var track = new MediaState(Title: "Test Song!", Artist: "By Artist, Guest", Available: true, Duration: 180);
        checks["LyricsMatchNormalizesMetadata"] = LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Artist", 182), track);
        checks["LyricsRejectWrongDuration"] = !LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Artist", 210), track);
        checks["LyricsRejectWrongArtist"] = !LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Someone Else", 180), track);
        var timer = new FocusTimer(); timer.Start(TimeSpan.FromSeconds(1)); timer.TogglePause();
        var remaining = timer.Remaining; await Task.Delay(100);
        checks["PausedTimerDoesNotAdvance"] = timer.Remaining == remaining;
        timer.TogglePause(); await Task.Delay(1100);
        checks["TimerExpiresOnce"] = timer.Tick() && !timer.Tick() && !timer.Active;
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
        var actualBar = NativeWindow.Taskbar(NativeWindow.TaskbarHandle(AppSettings.Load().MonitorDevice)).bounds;
        checks["VisibleWindowStaysInsideTaskbar"] = island.Opacity == 0 || (NativeWindow.GetWindowRect(island.Handle, out var actualWindow) && actualWindow.Top >= actualBar.Top && actualWindow.Bottom <= actualBar.Bottom);
        var monitor = NativeWindow.PrimaryMonitor();
        checks["FullscreenMatchesMonitor"] = NativeWindow.CoversMonitor(monitor.Monitor, monitor.Monitor);
        checks["MaximizedWorkAreaIsNotFullscreen"] = monitor.Work.Height == monitor.Monitor.Height || !NativeWindow.CoversMonitor(monitor.Work, monitor.Monitor);
        island.Close();
        Directory.CreateDirectory(AppSettings.DataDirectory);
        File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "verification.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
