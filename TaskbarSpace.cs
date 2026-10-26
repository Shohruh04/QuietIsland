using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace QuietIsland;

// Reads taskbar button bounds on a worker. It does not modify or inject into Explorer.
internal sealed class TaskbarSpace
{
    private bool _busy;
    private DateTimeOffset _checked;
    private NativeWindow.Rect _bounds;
    public double? OccupiedRight { get; private set; }
    public IReadOnlyList<System.Windows.Rect> Buttons { get; private set; } = Array.Empty<System.Windows.Rect>();
    public bool Valid { get; private set; }
    public double LastScanMilliseconds { get; private set; }
    public event Action? Changed;
    public async void Refresh(bool force = false)
    {
        if (_busy || DateTimeOffset.UtcNow - _checked < (force ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5))) return;
        _busy = true; _checked = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var taskbar = NativeWindow.Taskbar();
            if (taskbar.bounds.Width != _bounds.Width || taskbar.bounds.Height != _bounds.Height) OccupiedRight = null;
            _bounds = taskbar.bounds;
            var found = await Task.Run(() =>
            {
                var hwnd = NativeWindow.TaskbarHandle();
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
            Valid = found is { Count: > 0 };
            Buttons = found ?? new List<System.Windows.Rect>();
            OccupiedRight = Valid ? Buttons.Max(r => r.Right) : null;
        }
        catch { Valid = false; OccupiedRight = null; Buttons = Array.Empty<System.Windows.Rect>(); }
        finally { LastScanMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; _busy = false; Changed?.Invoke(); }
    }
}
