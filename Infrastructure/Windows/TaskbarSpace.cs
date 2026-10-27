using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;

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
    private bool _refreshAgainForced, _retryForced;
    private DateTimeOffset _retryAt;
    private int _retryGeneration;
    private int _failedScans;
    private readonly CancellationTokenSource _lifetime = new();
    public double? OccupiedRight { get; private set; }
    public IReadOnlyList<System.Windows.Rect> Buttons { get; private set; } = Array.Empty<System.Windows.Rect>();
    public bool Valid { get; private set; }
    public double LastScanMilliseconds { get; private set; }
    public bool LastScanSucceeded { get; private set; }
    internal string LastScanError { get; private set; } = "";
    internal int RawButtonCount { get; private set; }
    public event Action? Changed;
    public string MonitorDevice { get; set; } = "";
    public async Task RefreshAsync(bool force = false)
    {
        if (_disposed) return;
        if (_busy) { _refreshAgain = true; _refreshAgainForced |= force; return; }
        var wait = RefreshDelay(force, _checked, DateTimeOffset.UtcNow);
        // Coalesce requests instead of dropping the last foreground/layout
        // change while throttled; there may be no further shell event to retry it.
        if (wait > TimeSpan.Zero) { QueueRetry((int)wait.TotalMilliseconds + 20, force); return; }
        // This scan also fulfills requests queued before it actually started.
        _retryQueued = false; _retryGeneration++;
        _busy = true; _checked = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var hwnd = NativeWindow.TaskbarHandle(MonitorDevice);
        var snapshot = NativeWindow.Taskbar(hwnd);
        LastScanError = ""; RawButtonCount = 0;
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
                    RawButtonCount = buttons.Count;
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
            if (_disposed) return;
            if (found is not { Count: > 0 }) LastScanError = $"No usable taskbar buttons (raw: {RawButtonCount}, handle: {hwnd}).";
            // Explorer may replace the taskbar while UIA is running.
            var currentHandle = NativeWindow.TaskbarHandle(MonitorDevice);
            var current = NativeWindow.Taskbar(currentHandle);
            if (hwnd != currentHandle || !EqualBounds(taskbar.bounds, current.bounds) || taskbar.trayLeft != current.trayLeft) Invalidate();
            else AcceptScan(hwnd, taskbar.bounds, taskbar.trayLeft, found, DateTimeOffset.UtcNow);
        }
        catch (Exception error) { if (!_disposed) { LastScanError = error.GetType().Name; AcceptScan(hwnd, snapshot.bounds, snapshot.trayLeft, null, DateTimeOffset.UtcNow); } }
        finally
        {
            LastScanMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; _busy = false; if (!_disposed) Changed?.Invoke();
            var retryFailure = !LastScanSucceeded && _failedScans < 5;
            if (!_disposed && (_refreshAgain || retryFailure))
            {
                var retryForced = _refreshAgainForced || retryFailure;
                _refreshAgain = false; _refreshAgainForced = false;
                QueueRetry(retryFailure && !Valid ? 2000 : 550, retryForced);
            }
        }
    }
    internal static TimeSpan RefreshDelay(bool force, DateTimeOffset lastScan, DateTimeOffset now) =>
        (force ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5)) - (now - lastScan);
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
    private void QueueRetry(int milliseconds, bool force)
    {
        var due = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        if (_retryQueued && ((!force && _retryForced) || (force == _retryForced && due >= _retryAt))) return;
        _retryQueued = true;
        _retryAt = due; _retryForced = force;
        _ = RetrySoonAsync(milliseconds, force, ++_retryGeneration);
    }
    private async Task RetrySoonAsync(int milliseconds, bool force, int generation)
    {
        try
        {
            await Task.Delay(milliseconds, _lifetime.Token);
            if (_disposed || generation != _retryGeneration) return;
            _retryQueued = false;
            await RefreshAsync(force);
        }
        catch (OperationCanceledException) when (_disposed) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _retryQueued = false; _retryGeneration++; Changed = null; _lifetime.Cancel(); _lifetime.Dispose(); }
}
