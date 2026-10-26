using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace QuietIsland;

// Reads taskbar button bounds on a worker. It does not modify or inject into Explorer.
internal sealed class TaskbarSpace : IDisposable
{
    private bool _busy;
    private DateTimeOffset _checked;
    private NativeWindow.Rect _bounds;
    private IntPtr _taskbar;
    private int _trayLeft;
    private DateTimeOffset? _failureSince;
    private bool _disposed;
    private bool _retryQueued;
    private bool _refreshAgain;
    private int _failedScans;
    public double? OccupiedRight { get; private set; }
    public IReadOnlyList<System.Windows.Rect> Buttons { get; private set; } = Array.Empty<System.Windows.Rect>();
    public bool Valid { get; private set; }
    public double LastScanMilliseconds { get; private set; }
    public bool LastScanSucceeded { get; private set; }
    public event Action? Changed;
    public string MonitorDevice { get; set; } = "";
    public async void Refresh(bool force = false)
    {
        if (_disposed) return;
        if (_busy) { _refreshAgain |= force; return; }
        var wait = (force ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5)) - (DateTimeOffset.UtcNow - _checked);
        if (wait > TimeSpan.Zero) { if (force && !_retryQueued) RetrySoon((int)wait.TotalMilliseconds + 20); return; }
        _busy = true; _checked = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var hwnd = NativeWindow.TaskbarHandle(MonitorDevice);
        var snapshot = NativeWindow.Taskbar(hwnd);
        if (!SameGeometry(hwnd, snapshot.bounds, snapshot.trayLeft))
        {
            Invalidate(); Changed?.Invoke();
        }
        try
        {
            var taskbar = snapshot;
            var found = await Task.Run(() =>
            {
                if (hwnd == IntPtr.Zero) return (List<System.Windows.Rect>?)null;
                var element = AutomationElement.FromHandle(hwnd);
                var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
                request.Add(AutomationElement.BoundingRectangleProperty);
                using (request.Activate())
                {
                    var buttons = element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    var rectangles = new List<System.Windows.Rect>();
                    for (var i = 0; i < buttons.Count; i++)
                    {
                        var r = buttons[i].Cached.BoundingRectangle;
                        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || r.Top < taskbar.bounds.Top - 2 || r.Bottom > taskbar.bounds.Bottom + 2 || r.Left >= taskbar.trayLeft) continue;
                        rectangles.Add(r);
                    }
                    return rectangles;
                }
            });
            // Explorer may replace the taskbar while UIA is running.
            var currentHandle = NativeWindow.TaskbarHandle(MonitorDevice);
            var current = NativeWindow.Taskbar(currentHandle);
            if (hwnd != currentHandle || !EqualBounds(taskbar.bounds, current.bounds) || taskbar.trayLeft != current.trayLeft) Invalidate();
            else AcceptScan(hwnd, taskbar.bounds, taskbar.trayLeft, found, DateTimeOffset.UtcNow);
        }
        catch { AcceptScan(hwnd, snapshot.bounds, snapshot.trayLeft, null, DateTimeOffset.UtcNow); }
        finally
        {
            LastScanMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; _busy = false; if (!_disposed) Changed?.Invoke();
            if (_refreshAgain) { _refreshAgain = false; if (!_retryQueued) RetrySoon(550); }
            else if (!LastScanSucceeded && _failedScans < 5 && !_retryQueued) RetrySoon(Valid ? 550 : 2000);
        }
    }
    internal bool Matches(IntPtr handle, NativeWindow.Rect bounds, int trayLeft) => Valid && SameGeometry(handle, bounds, trayLeft);
    private static bool EqualBounds(NativeWindow.Rect a, NativeWindow.Rect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    private bool SameGeometry(IntPtr handle, NativeWindow.Rect bounds, int trayLeft) => handle == _taskbar && trayLeft == _trayLeft && EqualBounds(bounds, _bounds);
    private void Invalidate() { Valid = false; LastScanSucceeded = false; OccupiedRight = null; Buttons = Array.Empty<System.Windows.Rect>(); }
    internal void AcceptScan(IntPtr handle, NativeWindow.Rect bounds, int trayLeft, IReadOnlyList<System.Windows.Rect>? found, DateTimeOffset now)
    {
        var same = SameGeometry(handle, bounds, trayLeft);
        _taskbar = handle; _bounds = bounds; _trayLeft = trayLeft;
        LastScanSucceeded = found is { Count: > 0 };
        if (LastScanSucceeded)
        {
            _failedScans = 0;
            Buttons = found!; OccupiedRight = Buttons.Max(r => r.Right); _failureSince = null; Valid = true;
        }
        else { _failedScans++; _failureSince ??= now; if (!same || now - _failureSince.Value >= TimeSpan.FromSeconds(2)) Invalidate(); }
    }
    private async void RetrySoon(int milliseconds)
    {
        _retryQueued = true;
        await Task.Delay(milliseconds);
        _retryQueued = false; Refresh(true);
    }
    public void Dispose() { _disposed = true; Changed = null; }
}
