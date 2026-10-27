using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuietIsland;

// Owns native placement/lifecycle. The view owns mouse capture; callers persist settings.
internal sealed class TaskbarPlacementController : IDisposable
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly TaskbarSpace _space;
    private readonly Func<bool> _mediaAvailable, _menuOpen;
    private readonly Action<Rect> _applyBounds;
    private readonly DispatcherTimer _pulse;
    private readonly HashSet<IntPtr> _seenWindows = new();
    private HwndSource? _source;
    private NativeWindow.ForegroundWatch? _watch;
    private NativeWindow.Rect _bar, _monitor;
    private IntPtr _foreground;
    private int _trayLeft, _ticks;
    private bool _autoHide, _locked, _suspended, _disposed, _queued, _layoutChanged;
    private bool _awaitingMonitorScan;

    public IntPtr Handle { get; private set; }
    public IntPtr TaskbarHandle { get; private set; }
    public Rect Placement { get; private set; }
    public double Scale { get; private set; } = 1;
    public bool HasSpace { get; private set; }
    public bool Fullscreen { get; private set; }
    public bool IsVisible { get; private set; }
    public bool HotkeyOpen { get; private set; }
    public bool HotkeyHide { get; private set; }
    public string VisibilityReason { get; private set; } = "initializing";
    public int VisibilityTransitions { get; private set; }
    public int TaskbarCoverageRepairs { get; private set; }
    public double LongestPulseMs { get; private set; }
    public double LastTaskbarScanMs => _space.LastScanMilliseconds;
    public double? OccupiedRight => _space.OccupiedRight;
    public event Action? OpenRequested;
    public event Action? HideRequested;
    public event Action? VisibilityChanged;
    public event Action? PositionChanged;
    public event Action<bool, bool>? SessionStateChanged;

    public TaskbarPlacementController(Window window, AppSettings settings, TaskbarSpace space,
        Func<bool> mediaAvailable, Func<bool> menuOpen, Action<Rect> applyBounds)
    {
        _window = window; _settings = settings; _space = space;
        _mediaAvailable = mediaAvailable; _menuOpen = menuOpen; _applyBounds = applyBounds;
        _space.MonitorDevice = settings.MonitorDevice;
        _space.Changed += SpaceChanged;
        _pulse = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _pulse.Tick += Pulse;
    }

    public void Initialize(IntPtr handle, HwndSource source)
    {
        if (_disposed || Handle != IntPtr.Zero) return;
        Handle = handle; _source = source; source.AddHook(WindowMessage);
        NativeWindow.SetPassive(handle, false);
        HotkeyOpen = NativeWindow.RegisterHotKey(handle, 1, 0x4003, 0x49);
        HotkeyHide = NativeWindow.RegisterHotKey(handle, 2, 0x4003, 0x44);
        _watch = new NativeWindow.ForegroundWatch(QueueShellUpdate, () => _layoutChanged = true);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        _window.Opacity = 0;
        _pulse.Start();
        _ = _space.RefreshAsync(true);
    }

    private void QueueShellUpdate()
    {
        if (_disposed || _queued || _window.Dispatcher.HasShutdownStarted) return;
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            _queued = false;
            if (_disposed) return;
            if (_layoutChanged) { _layoutChanged = false; _ = _space.RefreshAsync(true); }
            RefreshForeground();
        }));
    }
    private void OnUi(Action action)
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == NativeWindow.MouseActivate) { handled = true; return new IntPtr(3); }
        if (message == NativeWindow.HotkeyMessage)
        {
            handled = true;
            if (w.ToInt32() == 1) OpenRequested?.Invoke();
            else if (w.ToInt32() == 2) HideRequested?.Invoke();
        }
        if (message == 0x007E) OnUi(() => { RefreshPlacement(); RefreshVisibility(); _ = _space.RefreshAsync(true); });
        if (message == 0x02E0) OnUi(() => { RefreshPlacement(); RefreshVisibility(); });
        if (message == 0x001A) OnUi(() => { _ = _space.RefreshAsync(); });
        return IntPtr.Zero;
    }
    private void SpaceChanged()
    {
        if (_disposed) return;
        if (_space.LastScanSucceeded) _awaitingMonitorScan = false;
        RefreshPlacement(); RefreshVisibility();
    }
    public void RefreshPlacement()
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        TaskbarHandle = NativeWindow.TaskbarHandle(_settings.MonitorDevice);
        Scale = Math.Max(96, NativeWindow.GetDpiForWindow(TaskbarHandle)) / 96.0;
        var bar = NativeWindow.Taskbar(TaskbarHandle);
        _bar = bar.bounds; _trayLeft = bar.trayLeft; _autoHide = bar.autoHide;
        _monitor = NativeWindow.PrimaryMonitor(TaskbarHandle).Monitor;
        var chosen = !_awaitingMonitorScan && _space.Matches(TaskbarHandle, _bar, _trayLeft)
            ? TaskbarLayout.Choose(_bar, _trayLeft, _space.Buttons, Scale, _settings.Placement, _settings.WidthDip, _settings.PositionFraction) : null;
        HasSpace = chosen.HasValue;
        if (chosen.HasValue) Apply(chosen.Value);
    }
    private void Apply(Rect bounds)
    {
        Placement = bounds;
        _applyBounds(bounds);
    }
    private void Pulse(object? sender, EventArgs args)
    {
        if (_disposed || _settings.Hidden) return;
        var start = Stopwatch.GetTimestamp();
        RefreshForeground();
        if (_ticks++ % 60 == 0) _ = _space.RefreshAsync();
        LongestPulseMs = Math.Max(LongestPulseMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    private void RefreshForeground()
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        var foreground = NativeWindow.GetForegroundWindow();
        if (foreground != _foreground)
        {
            _foreground = foreground;
            if (_seenWindows.Count > 128) _seenWindows.Clear();
            if (_seenWindows.Add(foreground)) _ = _space.RefreshAsync();
            NativeWindow.RaiseWithoutFocus(Handle);
        }
        Fullscreen = _settings.HideInFullscreen && NativeWindow.IsFullscreen(foreground, Handle, NativeWindow.MonitorFromWindow(Handle, 1));
        RefreshVisibility();
        if (!IsVisible || _menuOpen() || _locked || _suspended) return;
        if (NativeWindow.GetWindowRect(Handle, out var bounds) && NativeWindow.CoveredByTaskbar(bounds, TaskbarHandle))
        {
            TaskbarCoverageRepairs++;
            NativeWindow.RaiseWithoutFocus(Handle);
        }
    }
    public void RefreshVisibility()
    {
        if (_disposed) return;
        var barVisible = !_autoHide || (NativeWindow.GetWindowRect(TaskbarHandle, out var actual) && actual.Top < _monitor.Bottom - 4);
        var media = _mediaAvailable();
        var show = media && HasSpace && !_awaitingMonitorScan && barVisible && !_settings.Hidden && !Fullscreen && !_locked && !_suspended;
        VisibilityReason = !media ? "no-media" : !HasSpace || _awaitingMonitorScan ? "no-taskbar-space" : !barVisible ? "taskbar-hidden" : _settings.Hidden ? "user-hidden" : Fullscreen ? "fullscreen" : _locked ? "locked" : _suspended ? "suspended" : "visible";
        if (IsVisible == show && _window.Opacity == (show ? 1 : 0)) return;
        IsVisible = show; VisibilityTransitions++; _window.Opacity = show ? 1 : 0;
        VisibilityChanged?.Invoke();
    }
    public void Open()
    {
        if (_disposed) return;
        _settings.Hidden = false;
        _ = _space.RefreshAsync(true);
        RefreshPlacement(); RefreshForeground();
        if (Handle != IntPtr.Zero) { NativeWindow.ShowWindow(Handle, 4); NativeWindow.RaiseWithoutFocus(Handle); }
    }
    public void SetMonitor(string device)
    {
        if (_disposed) return;
        _settings.MonitorDevice = device; _space.MonitorDevice = device;
        _awaitingMonitorScan = true; HasSpace = false; RefreshVisibility();
        _ = _space.RefreshAsync(true);
    }
    public void SetSessionState(bool locked, bool suspended)
    {
        if (_disposed) return;
        var changed = _locked != locked || _suspended != suspended;
        _locked = locked; _suspended = suspended;
        if (locked || suspended) _pulse.Stop();
        else { _pulse.Start(); _ = _space.RefreshAsync(true); }
        RefreshVisibility();
        if (changed) SessionStateChanged?.Invoke(locked, suspended);
    }
    public bool TryMove(double desiredLeft)
    {
        if (_disposed || !HasSpace || !double.IsFinite(desiredLeft)) return false;
        // Pointer updates must not send synchronous shell messages to Explorer.
        // UIA/native events invalidate the cache; GetWindowRect cheaply catches a moved/replaced bar.
        if (!_space.Matches(TaskbarHandle, _bar, _trayLeft) || !NativeWindow.GetWindowRect(TaskbarHandle, out var current) ||
            current.Left != _bar.Left || current.Top != _bar.Top || current.Right != _bar.Right || current.Bottom != _bar.Bottom)
        {
            HasSpace = false; RefreshVisibility(); _ = _space.RefreshAsync(true); return false;
        }
        var scale = Math.Max(96, NativeWindow.GetDpiForWindow(TaskbarHandle)) / 96.0;
        if (scale != Scale) { RefreshPlacement(); return false; }
        var end = Math.Min(_bar.Right, _trayLeft) - 8 * Scale;
        var fraction = Math.Clamp((desiredLeft - _bar.Left) / Math.Max(1, end - _bar.Left - Math.Clamp(_settings.WidthDip, 280, 1200) * Scale), 0, 1);
        var chosen = TaskbarLayout.Choose(_bar, _trayLeft, _space.Buttons, Scale, "custom", _settings.WidthDip, fraction);
        if (!chosen.HasValue) return false;
        var left = TaskbarLayout.Choose(_bar, _trayLeft, _space.Buttons, Scale, "left", _settings.WidthDip);
        var right = TaskbarLayout.Choose(_bar, _trayLeft, _space.Buttons, Scale, "right", _settings.WidthDip);
        _settings.Placement = TaskbarLayout.GetPinMode(desiredLeft, left, right, Scale);
        if (_settings.Placement == "left" && left.HasValue) chosen = left;
        else if (_settings.Placement == "right" && right.HasValue) chosen = right;
        _settings.PositionFraction = fraction;
        if (!chosen.HasValue) return false;
        Apply(chosen.Value); PositionChanged?.Invoke(); return true;
    }
    // The parent saves through its view model on release; the last validated mode is already stored.
    public void CompleteMove() { if (!_disposed) { RefreshPlacement(); RefreshVisibility(); } }
    private void DisplayChanged(object? sender, EventArgs args) => OnUi(() => { RefreshPlacement(); RefreshVisibility(); _ = _space.RefreshAsync(true); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs args) => OnUi(() =>
    {
        if (args.Mode == PowerModes.Suspend) SetSessionState(_locked, true);
        else if (args.Mode == PowerModes.Resume) SetSessionState(_locked, false);
    });
    private void SessionChanged(object sender, SessionSwitchEventArgs args) => OnUi(() =>
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) SetSessionState(true, _suspended);
        else if (args.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon) SetSessionState(false, _suspended);
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pulse.Stop(); _pulse.Tick -= Pulse;
        _watch?.Dispose(); _source?.RemoveHook(WindowMessage);
        if (Handle != IntPtr.Zero) { NativeWindow.UnregisterHotKey(Handle, 1); NativeWindow.UnregisterHotKey(Handle, 2); }
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        _space.Changed -= SpaceChanged; _space.Dispose();
    }
}
