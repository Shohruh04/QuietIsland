using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace QuietIsland;

public partial class IslandWindow : Window
{
    private readonly IslandViewModel _viewModel;
    private readonly TrayMenuController _tray;
    private readonly TaskbarPlacementController _placement;
    private readonly UpdateController? _updates;
    private string _monitorDevice;
    private bool _closing, _moving, _moved, _seeking;
    private int _moveCursorX;
    private double _moveLeft;
    private string _seekTrack = "";
    private static readonly Geometry PlayGeometry = Geometry.Parse("M 0,0 L 10,6 L 0,12 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M 0,0 L 3.5,0 L 3.5,12 L 0,12 Z M 7,0 L 10.5,0 L 10.5,12 L 7,12 Z");
    internal IntPtr Handle => _placement.Handle;
    internal IslandViewModel ViewModel => _viewModel;
    internal TaskbarPlacementController PlacementController => _placement;

    public IslandWindow() : this(App.CreateViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher), new TaskbarSpace()) { }
    internal IslandWindow(IslandViewModel viewModel, TaskbarSpace space, UpdateController? updates = null)
    {
        InitializeComponent();
        _viewModel = viewModel; DataContext = viewModel; _updates = updates;
        _monitorDevice = viewModel.Settings.MonitorDevice;
        _tray = new TrayMenuController(viewModel, OpenPanel, ToggleHidden, ImportLyrics, CustomizeWidth, CustomizePosition, ExitApp, updates, () => Handle);
        _placement = new TaskbarPlacementController(this, viewModel.Settings, space,
            () => viewModel.Media.State.Available, () => _tray.IsMenuVisible, ApplyPlacement);
        _placement.OpenRequested += OpenPanel;
        _placement.HideRequested += ToggleHidden;
        _placement.VisibilityChanged += OnVisibilityChanged;
        _placement.SessionStateChanged += viewModel.SetSessionState;
        viewModel.SettingsChanged += OnSettingsChanged;
        viewModel.MediaChanged += OnMediaChanged;
        viewModel.PropertyChanged += OnPresentationChanged;
        viewModel.NotificationRequested += _tray.Notify;
        SourceInitialized += InitializeNative;
        Closed += Cleanup;
        MouseWheel += OnMouseWheel;
        MouseRightButtonUp += ShowContextMenu;
        ApplyTheme(); ApplyLanguage(); UpdatePresentation();
    }
    private async void InitializeNative(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _placement.Initialize(handle, HwndSource.FromHwnd(handle));
        await _viewModel.StartAsync();
    }
    private void ApplyPlacement(Rect bounds)
    {
        var scale = _placement.Scale;
        Width = bounds.Width / scale; Height = bounds.Height / scale;
        NativeWindow.PlaceWithoutFocus(Handle, bounds);
        var size = Math.Clamp(Height - 10, 24, 40);
        ArtBorder.Width = ArtBorder.Height = size;
        ArtworkClip.Rect = new Rect(0, 0, size - 2, size - 2);
        Previous.Visibility = Next.Visibility = Width >= 360 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSettingsChanged()
    {
        if (_closing) return;
        ApplyTheme(); ApplyLanguage();
        if (_monitorDevice != _viewModel.Settings.MonitorDevice)
        { _monitorDevice = _viewModel.Settings.MonitorDevice; _placement.SetMonitor(_monitorDevice); }
        else _placement.RefreshPlacement();
        _placement.RefreshVisibility();
    }
    private void OnMediaChanged() => _placement.RefreshVisibility();
    private void OnVisibilityChanged() => _viewModel.SetVisibility(_placement.IsVisible);
    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IslandViewModel.IsPlaying) or nameof(IslandViewModel.HasArtwork) or nameof(IslandViewModel.ShowLyrics)) UpdatePresentation();
    }
    private void UpdatePresentation()
    {
        PlayPath.Data = _viewModel.IsPlaying ? PauseGeometry : PlayGeometry;
        FallbackIcon.Visibility = _viewModel.HasArtwork ? Visibility.Collapsed : Visibility.Visible;
        LyricsButton.Opacity = _viewModel.ShowLyrics ? 1 : .55;
    }
    private void ApplyTheme()
    {
        var palette = Array.Find(ThemePalette.All, item => item.Id == _viewModel.Settings.Theme) ?? ThemePalette.All[0];
        palette.Apply(this, Shell, _viewModel.Settings.BackgroundOpacityPercent);
    }
    private void ApplyLanguage()
    {
        Previous.ToolTip = UiText.T("Предыдущий трек"); Next.ToolTip = UiText.T("Следующий трек");
        PlayPause.ToolTip = UiText.T("Воспроизвести / пауза"); LyricsButton.ToolTip = UiText.T("Текст песни / исполнитель");
        foreach (var button in new[] { Previous, Next, PlayPause, LyricsButton }) System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
        System.Windows.Automation.AutomationProperties.SetName(SeekSlider, UiText.T("Позиция воспроизведения"));
    }
    public void OpenPanel() { _viewModel.Settings.Hidden = false; _viewModel.SaveSettings(); _placement.Open(); }
    private void ToggleHidden() { _viewModel.Settings.Hidden = !_viewModel.Settings.Hidden; _viewModel.SaveSettings(); }
    private void OnMouseWheel(object sender, MouseWheelEventArgs args) { _viewModel.SetVolumeDelta(args.Delta > 0 ? .02f : -.02f); args.Handled = true; }
    private void ShowContextMenu(object sender, MouseButtonEventArgs args) { _tray.ShowContextMenu(); args.Handled = true; }
    private void MoveStart(object sender, MouseButtonEventArgs args)
    {
        if (!_placement.HasSpace || args.OriginalSource is not DependencyObject element) return;
        for (var node = element; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Slider) return;
        if (!NativeWindow.GetCursorPos(out var cursor) || !Shell.CaptureMouse()) return;
        _moving = true; _moved = false; _moveCursorX = cursor.X; _moveLeft = _placement.Placement.Left; args.Handled = true;
    }
    private void MovePointer(object sender, MouseEventArgs args)
    {
        if (!_moving)
        {
            var point = args.GetPosition(Shell);
            Shell.Cursor = point.X <= 5 || point.X >= Shell.ActualWidth - 5 ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        if (args.LeftButton != MouseButtonState.Pressed || !NativeWindow.GetCursorPos(out var cursor)) return;
        var delta = cursor.X - _moveCursorX;
        if (!_moved && Math.Abs(delta) < 4 * _placement.Scale) return;
        if (!_placement.TryMove(_moveLeft + delta)) { _moving = false; Shell.ReleaseMouseCapture(); return; }
        _moved = true; args.Handled = true;
    }
    private void MoveEnd(object sender, MouseButtonEventArgs args)
    {
        if (!_moving) return;
        _moving = false; Shell.ReleaseMouseCapture();
        if (_moved) { _placement.CompleteMove(); _viewModel.SaveSettings(); }
        args.Handled = true;
    }
    private void MoveCaptureLost(object sender, MouseEventArgs args)
    {
        if (!_moving) return;
        _moving = false;
        if (_moved && !_closing) { _placement.CompleteMove(); _viewModel.SaveSettings(); }
    }
    internal static double SeekPosition(double x, double width, double duration) => IslandViewModel.SeekPosition(x, width, duration);
    private void SetSeekPointer(MouseEventArgs args) => _viewModel.PreviewSeek(SeekPosition(args.GetPosition(SeekSlider).X, SeekSlider.ActualWidth, _viewModel.Duration));
    private void SeekStart(object sender, MouseButtonEventArgs args)
    {
        if (!_viewModel.CanSeek) return;
        args.Handled = true;
        if (!SeekSlider.CaptureMouse()) return;
        _seeking = true; _seekTrack = _viewModel.CurrentSeekTrack; _viewModel.BeginSeek(); SetSeekPointer(args);
    }
    private void SeekMove(object sender, MouseEventArgs args) { if (_seeking && args.LeftButton == MouseButtonState.Pressed) { SetSeekPointer(args); args.Handled = true; } }
    private async void SeekEnd(object sender, MouseButtonEventArgs args)
    {
        if (!_seeking) return;
        args.Handled = true; SetSeekPointer(args); var target = _viewModel.Position;
        _seeking = false; SeekSlider.ReleaseMouseCapture();
        await _viewModel.CompleteSeekAsync(target, _seekTrack);
    }
    private void SeekCaptureLost(object sender, MouseEventArgs args) { if (_seeking) { _seeking = false; _viewModel.CancelSeek(); } }
    private void ImportLyrics()
    {
        var dialog = new OpenFileDialog { Filter = UiText.T("Синхронный текст (*.lrc)|*.lrc"), Title = UiText.T("Текст для текущей песни") };
        if (dialog.ShowDialog(this) == true) _viewModel.ImportLyrics(dialog.FileName);
    }
    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;
    private void CustomizeWidth() => CustomizeNumber("Ширина виджета", "Ширина (280–1200 DIP)", 280, 1200, Math.Clamp(_viewModel.Settings.WidthDip, 280, 1200), _viewModel.SetWidth);
    private void CustomizePosition() => CustomizeNumber("Положение виджета", "Положение (0% слева — 100% справа)", 0, 100, (int)Math.Round(Math.Clamp(_viewModel.Settings.PositionFraction, 0, 1) * 100), value => _viewModel.SetPosition(value.ToString(CultureInfo.InvariantCulture)));
    private void CustomizeNumber(string title, string description, int minimum, int maximum, int current, Action<int> accept)
    {
        using var dialog = new Forms.Form { Text = UiText.T(title), ClientSize = new System.Drawing.Size(300, 130), FormBorderStyle = Forms.FormBorderStyle.FixedDialog, StartPosition = Forms.FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        using var label = new Forms.Label { Text = UiText.T(description), AutoSize = true, Left = 16, Top = 14 };
        using var value = new Forms.NumericUpDown { Minimum = minimum, Maximum = maximum, Value = current, Left = 16, Top = 40, Width = 268 };
        using var apply = new Forms.Button { Text = UiText.T("Применить"), DialogResult = Forms.DialogResult.OK, Left = 108, Top = 86, Width = 86 };
        using var cancel = new Forms.Button { Text = UiText.T("Отмена"), DialogResult = Forms.DialogResult.Cancel, Left = 200, Top = 86, Width = 84 };
        dialog.Controls.AddRange(new Forms.Control[] { label, value, apply, cancel }); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(new DialogOwner(Handle)) == Forms.DialogResult.OK) accept((int)value.Value);
    }
    public void HandleCommand(string command)
    {
        switch (command)
        {
            case "--exit": ExitApp(); break;
            case "--status": WriteStatus(); break;
            case "--export-preview": WindowDiagnostics.ExportPreview(Shell); break;
            case "--verify-media": _ = VerifyMediaAsync(); break;
            case "--check-updates": if (_updates != null) _ = _updates.CheckManuallyAsync(Handle); break;
            default: if (!_viewModel.HandleCommand(command) && command is not ("--startup" or "--collapse")) OpenPanel(); break;
        }
    }
    internal void WriteStatus() => WindowDiagnostics.WriteStatus(this, _viewModel, _placement);
    private async Task VerifyMediaAsync() { await _viewModel.Media.SeekAsync(_viewModel.Media.PositionSeconds); if (!_closing) WriteStatus(); }
    private void ExitApp() { Close(); Application.Current.Shutdown(); }
    private void Cleanup(object? sender, EventArgs args)
    {
        if (_closing) return;
        _closing = true;
        SourceInitialized -= InitializeNative; Closed -= Cleanup; MouseWheel -= OnMouseWheel; MouseRightButtonUp -= ShowContextMenu;
        _viewModel.SettingsChanged -= OnSettingsChanged; _viewModel.MediaChanged -= OnMediaChanged; _viewModel.PropertyChanged -= OnPresentationChanged; _viewModel.NotificationRequested -= _tray.Notify;
        _placement.OpenRequested -= OpenPanel; _placement.HideRequested -= ToggleHidden; _placement.VisibilityChanged -= OnVisibilityChanged; _placement.SessionStateChanged -= _viewModel.SetSessionState;
        _updates?.Dispose(); _placement.Dispose(); _tray.Dispose(); _viewModel.Dispose();
    }
}
