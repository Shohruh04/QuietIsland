using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietIsland;

internal static class WindowDiagnostics
{
    public static void WriteStatus(IslandWindow window, IslandViewModel model, TaskbarPlacementController placement)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        NativeWindow.GetWindowRect(window.Handle, out var bounds);
        var covering = NativeWindow.GetAncestor(NativeWindow.WindowFromPoint(new NativeWindow.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }), 2);
        var settings = model.Settings; var media = model.Media;
        var status = new
        {
            ProcessId = Environment.ProcessId, settings.Hidden, Startup = model.Startup.IsEnabled,
            NativeVisible = NativeWindow.IsWindowVisible(window.Handle), window.Opacity, Handle = window.Handle.ToInt64(), window.Left, window.Top, window.Width, window.Height,
            PhysicalBounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, DpiScale = placement.Scale,
            ActualMonitor = System.Windows.Forms.Screen.FromHandle(window.Handle).DeviceName, settings.PositionFraction,
            CoveredByTaskbar = covering == placement.TaskbarHandle, CoveringClass = NativeWindow.WindowClass(covering), ForegroundClass = NativeWindow.WindowClass(NativeWindow.GetForegroundWindow()),
            placement.TaskbarCoverageRepairs, placement.Fullscreen, placement.HasSpace, settings.Placement, settings.MonitorDevice, settings.Language, settings.WidthDip,
            placement.VisibilityReason, placement.VisibilityTransitions, settings.BackgroundOpacityPercent,
            NoActivate = (NativeWindow.GetWindowLong(window.Handle, NativeWindow.ExStyle) & NativeWindow.NoActivate) != 0,
            placement.HotkeyOpen, placement.HotkeyHide, TaskbarOccupiedRight = placement.OccupiedRight,
            Media = new { media.State.Title, media.State.Artist, media.State.Available, media.State.Playing, Artwork = media.State.Artwork != null, media.State.Duration, Position = media.PositionSeconds, media.State.CanSeek, media.LastSeekSucceeded, media.LastSeekConfirmed },
            Lyrics = new { settings.OnlineLyrics, model.Lyrics.Document.Status, SyncedLines = model.Lyrics.Document.Lines.Count },
            Volume = model.Audio.Volume, Timer = model.Focus.Display, TimerActive = model.Focus.Active,
            placement.LongestPulseMs, LastTaskbarScanMs = placement.LastTaskbarScanMs
        };
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void ExportPreview(FrameworkElement surface)
    {
        surface.UpdateLayout();
        var image = new RenderTargetBitmap((int)(surface.ActualWidth * 2), (int)(surface.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(surface); Directory.CreateDirectory(AppPaths.DataDirectory);
        using var stream = File.Create(Path.Combine(AppPaths.DataDirectory, "preview.png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
}
