using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace QuietIsland;

public partial class IslandWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MediaService _media;
    private readonly AudioService _audio;
    private readonly LyricsService _lyrics = new();
    private readonly TaskbarSpace _space = new();
    private readonly FocusTimer _focus = new();
    private readonly HashSet<IntPtr> _seenWindows = new();
    private readonly DispatcherTimer _pulse, _clock;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu = new();
    private sealed record MenuText(string Text, string Tooltip);
    private readonly ConditionalWeakTable<Forms.ToolStripItem, MenuText> _menuText = new();
    private readonly Forms.ToolStripMenuItem _hiddenItem, _startupItem, _onlineItem, _lyricsItem, _volumeItem, _fullscreenItem;
    private readonly Forms.ToolStripMenuItem[] _placementItems;
    private HwndSource? _source;
    private NativeWindow.ForegroundWatch? _foregroundWatch;
    private IntPtr _handle, _foreground;
    private bool _closing, _fullscreen, _locked, _suspended, _hasSpace, _hotkeyOpen, _hotkeyHide, _seeking, _syncSeek;
    private bool _seekPending;
    private bool _shellUpdateQueued;
    private int _taskbarCoverageRepairs;
    private int _seekGesture, _visibilityTransitions;
    private string _visibilityReason = "initializing", _seekTrack = "";
    private DateTimeOffset _alertUntil;
    private string _alert = "";
    private int _ticks;
    private Rect _placement;
    private double _scale = 1;
    private NativeWindow.Rect _barBounds, _monitorBounds;
    private bool _autoHide;
    private IntPtr _taskbarHandle;
    private double _longestPulseMs;
    private static readonly Geometry PlayGeometry = Geometry.Parse("M 0,0 L 10,6 L 0,12 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M 0,0 L 3.5,0 L 3.5,12 L 0,12 Z M 7,0 L 10.5,0 L 10.5,12 L 7,12 Z");
    internal IntPtr Handle => _handle;

    public IslandWindow()
    {
        InitializeComponent();
        UiText.Language = _settings.Language == "en" ? "en" : "ru";
        _space.MonitorDevice = _settings.MonitorDevice;
        ApplyTheme();
        _media = new MediaService(Dispatcher);
        _audio = new AudioService(Dispatcher);
        _media.Changed += () => { _lyrics.Update(_media.State, _settings.OnlineLyrics); RefreshContent(); UpdateVisibility(); };
        _lyrics.Changed += RefreshContent;
        _audio.Changed += () => { if (_settings.VolumeAlerts) Alert(_audio.Muted ? "Звук выключен" : "Громкость: " + Math.Round(_audio.Volume * 100) + "%"); };
        _space.Changed += () => { if (!_closing) { UpdatePlacement(); UpdateVisibility(); } };
        _menu.Items.Add("Показать виджет                  Ctrl+Alt+I", null, (_, _) => OnUi(OpenPanel));
        _hiddenItem = AddToggle("Скрыть                                 Ctrl+Alt+D", ToggleHidden);
        var positions = new Forms.ToolStripMenuItem("Расположение");
        var names = new[] { "Слева", "Справа", "Автоматически: больше свободного места" };
        var values = new[] { "left", "right", "auto" };
        _placementItems = new Forms.ToolStripMenuItem[3];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            _placementItems[i] = new Forms.ToolStripMenuItem(names[i], null, (_, _) => OnUi(() => { _settings.Placement = value; Save(); UpdatePlacement(); }));
            positions.DropDownItems.Add(_placementItems[i]);
        }
        _menu.Items.Add(positions);
        var sizes = new Forms.ToolStripMenuItem("Размер");
        foreach (var width in new[] { 280, 360, 480, 600, 720, 960 })
        {
            var selectedWidth = width;
            sizes.DropDownItems.Add(new Forms.ToolStripMenuItem(width + " DIP", null, (_, _) => OnUi(() => SetWidth(selectedWidth))) { Tag = width });
        }
        sizes.DropDownItems.Add("Своя ширина…", null, (_, _) => OnUi(CustomizeWidth));
        sizes.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in sizes.DropDownItems) item.Checked = item.Tag is int width && width == _settings.WidthDip; };
        _menu.Items.Add(sizes);
        var monitors = new Forms.ToolStripMenuItem("Монитор");
        monitors.DropDownOpening += (_, _) =>
        {
            monitors.DropDownItems.Clear();
            monitors.DropDownItems.Add(new Forms.ToolStripMenuItem(UiText.T("Основной монитор"), null, (_, _) => OnUi(() => SetMonitor(""))) { Checked = _settings.MonitorDevice.Length == 0 });
            var index = 0;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var device = screen.DeviceName;
                monitors.DropDownItems.Add(new Forms.ToolStripMenuItem(UiText.T("Монитор") + " " + ++index + " · " + screen.Bounds.Width + " × " + screen.Bounds.Height,
                    null, (_, _) => OnUi(() => SetMonitor(device))) { Checked = _settings.MonitorDevice == device });
            }
        };
        _menu.Items.Add(monitors);
        var languages = new Forms.ToolStripMenuItem("Язык");
        foreach (var (code, label) in new[] { ("ru", "Русский"), ("en", "English") })
        {
            var language = code;
            languages.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => OnUi(() => SetLanguage(language))) { Tag = code });
        }
        languages.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in languages.DropDownItems) item.Checked = (string)item.Tag! == _settings.Language; };
        _menu.Items.Add(languages);
        var themes = new Forms.ToolStripMenuItem("Цветовая тема");
        foreach (var theme in ThemePalette.All)
        {
            var selected = theme;
            themes.DropDownItems.Add(new Forms.ToolStripMenuItem(theme.Name, null, (_, _) => OnUi(() => { _settings.Theme = selected.Id; Save(); ApplyTheme(); })) { Tag = theme.Id });
        }
        themes.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in themes.DropDownItems) item.Checked = (string?)item.Tag == _settings.Theme; };
        _menu.Items.Add(themes);
        var transparency = new Forms.ToolStripMenuItem("Прозрачность фона");
        foreach (var percent in new[] { 0, 10, 25, 40, 60, 80 })
        {
            var opacity = 100 - percent;
            transparency.DropDownItems.Add(new Forms.ToolStripMenuItem(percent == 0 ? "Непрозрачный фон" : percent + "% прозрачности", null,
                (_, _) => OnUi(() => { _settings.BackgroundOpacityPercent = opacity; Save(); ApplyTheme(); })) { Tag = opacity });
        }
        transparency.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in transparency.DropDownItems) item.Checked = (int)item.Tag! == _settings.BackgroundOpacityPercent; };
        _menu.Items.Add(transparency);
        var sources = new Forms.ToolStripMenuItem("Источник музыки");
        sources.DropDownOpening += (_, _) =>
        {
            _media.RefreshSources(); sources.DropDownItems.Clear();
            var automatic = new Forms.ToolStripMenuItem("Автоматически", null, (_, _) => OnUi(() => _media.SelectSource(null))) { Checked = _media.SelectedSourceId == null };
            sources.DropDownItems.Add(automatic);
            foreach (var source in _media.Sources)
            {
                var id = source.Id;
                sources.DropDownItems.Add(new Forms.ToolStripMenuItem(source.Name, null, (_, _) => OnUi(() => _media.SelectSource(id))) { Checked = source.IsSelected });
            }
            LocalizeMenu(sources.DropDownItems);
        };
        _menu.Items.Add(sources);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _lyricsItem = AddToggle("Показывать строку песни", () => { _settings.ShowLyrics = !_settings.ShowLyrics; Save(); RefreshContent(); });
        _onlineItem = AddToggle("Онлайн-текст · LRCLIB", () => { _settings.OnlineLyrics = !_settings.OnlineLyrics; Save(); _lyrics.Update(_media.State, _settings.OnlineLyrics); RefreshContent(); });
        _onlineItem.ToolTipText = "Для поиска в LRCLIB передаются название, исполнитель, альбом и длительность песни.";
        _menu.Items.Add("Обновить текст песни", null, (_, _) => OnUi(() => _lyrics.Retry(_media.State, _settings.OnlineLyrics)));
        var lyricVersions = new Forms.ToolStripMenuItem("Выбрать версию текста");
        lyricVersions.DropDownOpening += (_, _) =>
        {
            lyricVersions.DropDownItems.Clear();
            for (var i = 0; i < _lyrics.Candidates.Count; i++)
            {
                var index = i; var candidate = _lyrics.Candidates[i];
                lyricVersions.DropDownItems.Add(candidate.Artist + " — " + candidate.Title + " (" + TimeSpan.FromSeconds(candidate.Duration).ToString(@"m\:ss") + ")",
                    null, (_, _) => OnUi(() => { _lyrics.SelectCandidate(index); _settings.ShowLyrics = true; Save(); RefreshContent(); }));
            }
            if (lyricVersions.DropDownItems.Count == 0) lyricVersions.DropDownItems.Add(new Forms.ToolStripMenuItem("Нет вариантов: обновите текст песни") { Enabled = false });
            LocalizeMenu(lyricVersions.DropDownItems);
        };
        _menu.Items.Add(lyricVersions);
        var syncMenu = new Forms.ToolStripMenuItem("Синхронизация текста");
        syncMenu.DropDownItems.Add("Показать строку раньше на 0,5 с", null, (_, _) => OnUi(() => { _settings.LyricsOffsetMs = Math.Clamp(_settings.LyricsOffsetMs + 500, -30000, 30000); Save(); RefreshContent(); }));
        syncMenu.DropDownItems.Add("Показать строку позже на 0,5 с", null, (_, _) => OnUi(() => { _settings.LyricsOffsetMs = Math.Clamp(_settings.LyricsOffsetMs - 500, -30000, 30000); Save(); RefreshContent(); }));
        syncMenu.DropDownItems.Add("Сбросить сдвиг", null, (_, _) => OnUi(() => { _settings.LyricsOffsetMs = 0; Save(); RefreshContent(); }));
        _menu.Items.Add(syncMenu);
        _menu.Items.Add("Загрузить локальный файл .lrc…", null, (_, _) => OnUi(ImportLyrics));
        _volumeItem = AddToggle("Показывать изменение громкости", () => { _settings.VolumeAlerts = !_settings.VolumeAlerts; Save(); });
        _fullscreenItem = AddToggle("Скрывать в полноэкранном режиме", () => { _settings.HideInFullscreen = !_settings.HideInFullscreen; Save(); UpdateVisibility(); });
        var timer = new Forms.ToolStripMenuItem("Таймер фокусировки");
        foreach (var minutes in new[] { 5, 25, 50 })
        {
            var duration = minutes;
            timer.DropDownItems.Add(minutes + " минут", null, (_, _) => OnUi(() => { _focus.Start(TimeSpan.FromMinutes(duration)); RefreshContent(); }));
        }
        timer.DropDownItems.Add("Пауза / продолжить", null, (_, _) => OnUi(() => { _focus.TogglePause(); RefreshContent(); }));
        timer.DropDownItems.Add("Остановить", null, (_, _) => OnUi(() => { _focus.Stop(); RefreshContent(); }));
        _menu.Items.Add(timer);
        _startupItem = AddToggle("Запускать вместе с Windows", () => { AppSettings.SetStartup(!AppSettings.StartupEnabled); UpdateMenu(); });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Открыть папку программы", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true }));
        _menu.Items.Add("Выход", null, (_, _) => OnUi(ExitApp));
        _tray = new Forms.NotifyIcon { Text = "Quiet Island · Ctrl+Alt+I", Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "QuietIsland.ico"), 32, 32), ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => OnUi(OpenPanel);
        _menu.Opening += (_, _) => { UpdateMenu(); LocalizeMenu(_menu.Items); };
        ApplyLanguage();
        SourceInitialized += InitializeNative;
        Closed += Cleanup;
        MouseWheel += (_, e) => { _audio.SetVolume(_audio.Volume + (e.Delta > 0 ? 0.02f : -0.02f)); e.Handled = true; };
        MouseRightButtonUp += (_, e) => { _menu.Show(Forms.Cursor.Position); e.Handled = true; };
        _pulse = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _pulse.Tick += Pulse;
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            if (_focus.Tick()) { Alert("Таймер завершён. Пора отдохнуть.", 8); _tray.ShowBalloonTip(4000, "Quiet Island", UiText.T("Таймер завершён. Пора отдохнуть."), Forms.ToolTipIcon.Info); }
            _media.ReconcileSeek();
            if (Opacity > 0) RefreshDynamicContent();
            UpdateClockState();
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
    }

    private Forms.ToolStripMenuItem AddToggle(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => OnUi(action));
        _menu.Items.Add(item); return item;
    }
    private void ApplyTheme()
    {
        var palette = Array.Find(ThemePalette.All, p => p.Id == _settings.Theme) ?? ThemePalette.All[0];
        palette.Apply(this, Shell, _settings.BackgroundOpacityPercent);
    }
    private void LocalizeMenu(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            var original = _menuText.GetValue(item, value => new(value.Text ?? "", value.ToolTipText ?? ""));
            item.Text = UiText.T(original.Text); item.ToolTipText = UiText.T(original.Tooltip);
            if (item is Forms.ToolStripMenuItem child) LocalizeMenu(child.DropDownItems);
        }
    }
    private void ApplyLanguage()
    {
        LocalizeMenu(_menu.Items);
        Previous.ToolTip = UiText.T("Предыдущий трек"); Next.ToolTip = UiText.T("Следующий трек");
        PlayPause.ToolTip = UiText.T("Воспроизвести / пауза"); LyricsButton.ToolTip = UiText.T("Текст песни / исполнитель");
        foreach (var button in new[] { Previous, Next, PlayPause, LyricsButton }) System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
        System.Windows.Automation.AutomationProperties.SetName(SeekSlider, UiText.T("Позиция воспроизведения"));
    }
    private void SetLanguage(string language)
    {
        if (language is not ("ru" or "en")) return;
        _settings.Language = language; UiText.Language = language; Save(); ApplyLanguage(); RefreshContent();
    }
    private void SetMonitor(string device)
    {
        _settings.MonitorDevice = device; _space.MonitorDevice = device; Save();
        _hasSpace = false; UpdateVisibility(); _space.Refresh(true);
    }
    private void SetWidth(int width) { _settings.WidthDip = Math.Clamp(width, 280, 1200); Save(); UpdatePlacement(); UpdateVisibility(); }
    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;
    private void CustomizeWidth()
    {
        using var dialog = new Forms.Form { Text = UiText.T("Ширина виджета"), ClientSize = new System.Drawing.Size(300, 130),
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog, StartPosition = Forms.FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        using var label = new Forms.Label { Text = UiText.T("Ширина (280–1200 DIP)"), AutoSize = true, Left = 16, Top = 14 };
        using var value = new Forms.NumericUpDown { Minimum = 280, Maximum = 1200, Value = Math.Clamp(_settings.WidthDip, 280, 1200), Left = 16, Top = 40, Width = 268 };
        using var apply = new Forms.Button { Text = UiText.T("Применить"), DialogResult = Forms.DialogResult.OK, Left = 108, Top = 86, Width = 86 };
        using var cancel = new Forms.Button { Text = UiText.T("Отмена"), DialogResult = Forms.DialogResult.Cancel, Left = 200, Top = 86, Width = 84 };
        dialog.Controls.AddRange(new Forms.Control[] { label, value, apply, cancel }); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(new DialogOwner(_handle)) == Forms.DialogResult.OK) SetWidth((int)value.Value);
    }
    private void InitializeNative(object? sender, EventArgs args)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle); _source.AddHook(WindowMessage);
        _foregroundWatch = new NativeWindow.ForegroundWatch(() =>
        {
            if (_closing || _shellUpdateQueued || Dispatcher.HasShutdownStarted) return;
            _shellUpdateQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { _shellUpdateQueued = false; RefreshForeground(); }));
        });
        NativeWindow.SetPassive(_handle, false); // Only a verified empty taskbar gap is interactive.
        _hotkeyOpen = NativeWindow.RegisterHotKey(_handle, 1, 0x4003, 0x49);
        _hotkeyHide = NativeWindow.RegisterHotKey(_handle, 2, 0x4003, 0x44);
        _space.Refresh(true); Opacity = 0;
        _pulse.Start(); _clock.Start(); RefreshContent(); UpdateMenu();
        _ = _media.StartAsync();
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == NativeWindow.MouseActivate) { handled = true; return new IntPtr(3); }
        if (msg == NativeWindow.HotkeyMessage) { handled = true; if (w.ToInt32() == 1) OpenPanel(); else ToggleHidden(); }
        if (msg == 0x007E) OnUi(() => { UpdatePlacement(); UpdateVisibility(); _space.Refresh(true); });
        if (msg == 0x02E0) OnUi(() => { UpdatePlacement(); UpdateVisibility(); });
        if (msg == 0x001A) OnUi(() => _space.Refresh());
        return IntPtr.Zero;
    }
    private void UpdatePlacement()
    {
        if (_source?.CompositionTarget == null) return;
        _taskbarHandle = NativeWindow.TaskbarHandle(_settings.MonitorDevice);
        _scale = Math.Max(96, NativeWindow.GetDpiForWindow(_taskbarHandle)) / 96.0;
        var bar = NativeWindow.Taskbar(_taskbarHandle);
        _barBounds = bar.bounds; _autoHide = bar.autoHide;
        _monitorBounds = NativeWindow.PrimaryMonitor(_taskbarHandle).Monitor;
        var chosen = _space.Matches(_taskbarHandle, bar.bounds, bar.trayLeft) ? TaskbarLayout.Choose(bar.bounds, bar.trayLeft, _space.Buttons, _scale, _settings.Placement, _settings.WidthDip) : null;
        _hasSpace = chosen.HasValue;
        if (!chosen.HasValue) return;
        _placement = chosen.Value;
        Width = _placement.Width / _scale; Height = _placement.Height / _scale;
        NativeWindow.PlaceWithoutFocus(_handle, _placement);
        var artSize = Math.Clamp(Height - 10, 24, 40);
        ArtBorder.Width = ArtBorder.Height = artSize;
        ArtworkClip.Rect = new Rect(0, 0, artSize - 2, artSize - 2);
        Previous.Visibility = Next.Visibility = Width >= 360 ? Visibility.Visible : Visibility.Collapsed;
        NativeWindow.RaiseWithoutFocus(_handle);
    }
    private void Pulse(object? sender, EventArgs e)
    {
        if (_closing || _settings.Hidden) return;
        var started = Stopwatch.GetTimestamp();
        RefreshForeground();
        if (_ticks++ % 60 == 0) _space.Refresh();
        UpdateVisibility();
        _longestPulseMs = Math.Max(_longestPulseMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    private void RefreshForeground()
    {
        if (_closing) return;
        var foreground = NativeWindow.GetForegroundWindow();
        if (foreground != _foreground)
        {
            _foreground = foreground;
            if (_seenWindows.Count > 128) _seenWindows.Clear();
            if (_seenWindows.Add(foreground)) _space.Refresh();
            NativeWindow.RaiseWithoutFocus(_handle);
        }
        _fullscreen = _settings.HideInFullscreen && NativeWindow.IsFullscreen(foreground, _handle, NativeWindow.MonitorFromWindow(_handle, 1));
        UpdateVisibility();
        EnsureTaskbarCoverage();
    }
    private void EnsureTaskbarCoverage()
    {
        if (_closing || Opacity == 0 || !_hasSpace || _menu.Visible || _locked || _suspended) return;
        if (NativeWindow.GetWindowRect(_handle, out var bounds) && NativeWindow.CoveredByTaskbar(bounds, _taskbarHandle))
        {
            _taskbarCoverageRepairs++;
            NativeWindow.RaiseWithoutFocus(_handle);
        }
    }
    private void UpdateVisibility()
    {
        // Cached shell state: no synchronous Explorer messages in the frequent loop.
        var visibleBar = !_autoHide || (NativeWindow.GetWindowRect(_taskbarHandle, out var actualBar) && actualBar.Top < _monitorBounds.Bottom - 4);
        var show = _media.State.Available && _hasSpace && visibleBar && !_settings.Hidden && !_fullscreen && !_locked && !_suspended;
        _visibilityReason = !_media.State.Available ? "no-media" : !_hasSpace ? "no-taskbar-space" : !visibleBar ? "taskbar-hidden" : _settings.Hidden ? "user-hidden" : _fullscreen ? "fullscreen" : _locked ? "locked" : _suspended ? "suspended" : "visible";
        if (Opacity != (show ? 1 : 0))
        {
            _visibilityTransitions++; Opacity = show ? 1 : 0;
            if (show) RefreshDynamicContent();
            UpdateClockState();
        }
    }
    public void OpenPanel()
    {
        _settings.Hidden = false; Save();
        _space.Refresh(true); UpdatePlacement(); UpdateVisibility();
        NativeWindow.ShowWindow(_handle, 4); NativeWindow.RaiseWithoutFocus(_handle);
    }
    internal void Collapse() { } // Kept for the IPC command; this UI always stays in the taskbar.
    private void ToggleHidden() { _settings.Hidden = !_settings.Hidden; Save(); UpdateVisibility(); }
    private void Save() { _settings.Save(); UpdateMenu(); }
    private void UpdateMenu()
    {
        _hiddenItem.Checked = _settings.Hidden; _startupItem.Checked = AppSettings.StartupEnabled;
        _onlineItem.Checked = _settings.OnlineLyrics; _lyricsItem.Checked = _settings.ShowLyrics;
        _volumeItem.Checked = _settings.VolumeAlerts; _fullscreenItem.Checked = _settings.HideInFullscreen;
        var values = new[] { "left", "right", "auto" };
        for (var i = 0; i < values.Length; i++) _placementItems[i].Checked = _settings.Placement == values[i];
    }
    private void RefreshContent()
    {
        if (_closing) return;
        var state = _media.State;
        TrackTitle.Text = state.Available ? (state.Title == "Медиа" ? UiText.T(state.Title) : state.Title) : "Quiet Island";
        TrackDetail.ToolTip = _settings.ShowLyrics ? UiText.T(_lyrics.Document.Status) : state.Artist;
        if (!ReferenceEquals(Artwork.Source, state.Artwork)) Artwork.Source = state.Artwork;
        FallbackIcon.Visibility = state.Artwork == null ? Visibility.Visible : Visibility.Collapsed;
        PlayPath.Data = state.Playing ? PauseGeometry : PlayGeometry;
        PlayPause.IsEnabled = state.Available && state.CanPlayPause;
        Previous.IsEnabled = state.Available && state.CanPrevious; Next.IsEnabled = state.Available && state.CanNext;
        SeekSlider.IsEnabled = state.Available && state.CanSeek && state.Duration > 0;
        LyricsButton.Opacity = _settings.ShowLyrics ? 1 : 0.55;
        RefreshDynamicContent(); UpdateClockState();
    }
    private void RefreshDynamicContent()
    {
        if (_closing) return;
        var state = _media.State;
        var detail = _seeking || _seekPending ? UiText.T("Перемотка · ") + TimeSpan.FromSeconds(SeekSlider.Value).ToString(@"m\:ss") : DateTimeOffset.UtcNow < _alertUntil ? UiText.T(_alert) : _focus.Active ? UiText.T("Фокус · ") + _focus.Display :
            _settings.ShowLyrics && _lyrics.Document.Lines.Count > 0 ? _lyrics.At(_media.PositionSeconds + _settings.LyricsOffsetMs / 1000.0).current : state.Available ? state.Artist : UiText.T("Включите музыку в любимом плеере");
        if (TrackDetail.Text != detail) TrackDetail.Text = detail;
        if (!_seeking && !_seekPending)
        {
            _syncSeek = true; SeekSlider.Maximum = Math.Max(1, state.Duration); SeekSlider.Value = _media.PositionSeconds; _syncSeek = false;
        }
    }
    private void UpdateClockState()
    {
        if (_clock == null) return;
        var needed = !_closing && !_locked && !_suspended && (_focus.Running || _media.HasPendingSeek ||
            (Opacity > 0 && (_media.State.Playing || _seekPending || DateTimeOffset.UtcNow < _alertUntil)));
        if (needed) _clock.Start(); else _clock.Stop();
    }
    private void LyricsClick(object sender, RoutedEventArgs e) { _settings.ShowLyrics = !_settings.ShowLyrics; Save(); RefreshContent(); if (_settings.ShowLyrics && _lyrics.Document.Lines.Count == 0) Alert(_lyrics.Document.Status, 5); }
    private async void PlayPauseClick(object sender, RoutedEventArgs e) => await _media.ControlAsync("toggle");
    private async void PreviousClick(object sender, RoutedEventArgs e) => await _media.ControlAsync("previous");
    private async void NextClick(object sender, RoutedEventArgs e) => await _media.ControlAsync("next");
    private string CurrentSeekTrack => _media.SelectedSourceId + "\n" + _media.State.Title + "\n" + _media.State.Artist;
    internal static double SeekPosition(double x, double width, double duration) => width <= 0 || duration <= 0 ? 0 : Math.Clamp((x - 2.5) / Math.Max(1, width - 5), 0, 1) * duration;
    private void SetSeekPointer(MouseEventArgs e)
    {
        SeekSlider.Value = SeekPosition(e.GetPosition(SeekSlider).X, SeekSlider.ActualWidth, SeekSlider.Maximum);
    }
    private void SeekStart(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled) return;
        // Own the full gesture: Slider's move-to-point class handler marks down
        // handled before an instance handler can start tracking a track click.
        e.Handled = true;
        if (!SeekSlider.CaptureMouse()) return;
        _seeking = true; _seekTrack = CurrentSeekTrack; SetSeekPointer(e);
    }
    private void SeekMove(object sender, MouseEventArgs e)
    {
        if (_seeking && e.LeftButton == MouseButtonState.Pressed) { SetSeekPointer(e); e.Handled = true; }
    }
    private async void SeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        e.Handled = true; SetSeekPointer(e);
        var target = SeekSlider.Value; var sameTrack = _seekTrack == CurrentSeekTrack;
        _seeking = false; SeekSlider.ReleaseMouseCapture();
        if (!sameTrack) { RefreshContent(); return; }
        var gesture = ++_seekGesture; _seekPending = true;
        var succeeded = await _media.SeekAsync(target);
        if (_closing || gesture != _seekGesture) return;
        _seekPending = false;
        if (!succeeded) Alert("Плеер не разрешил перемотку", 3);
        RefreshContent();
    }
    private void SeekCaptureLost(object sender, MouseEventArgs e) { if (_seeking) { _seeking = false; RefreshContent(); } }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncSeek || !_seeking) return;
        TrackDetail.Text = UiText.T("Перемотка · ") + TimeSpan.FromSeconds(e.NewValue).ToString(@"m\:ss");
    }
    private void ImportLyrics()
    {
        var dialog = new OpenFileDialog { Filter = UiText.T("Синхронный текст (*.lrc)|*.lrc"), Title = UiText.T("Текст для текущей песни") };
        if (dialog.ShowDialog() == true) { _lyrics.Import(dialog.FileName); _settings.ShowLyrics = true; Save(); RefreshContent(); }
    }
    private void Alert(string text, int seconds = 2) { _alert = text; _alertUntil = DateTimeOffset.UtcNow.AddSeconds(seconds); RefreshContent(); }
    private void OnUi(Action action) { if (!_closing) Dispatcher.BeginInvoke(action); }
    private void DisplayChanged(object? sender, EventArgs e) => OnUi(() => { UpdatePlacement(); UpdateVisibility(); _space.Refresh(true); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => OnUi(() => { if (e.Mode == PowerModes.Suspend) _suspended = true; else if (e.Mode == PowerModes.Resume) _suspended = false; UpdateTimers(); });
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => OnUi(() => { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) _locked = true; if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon) _locked = false; UpdateTimers(); });
    private void UpdateTimers()
    {
        if (_locked || _suspended) { _pulse.Stop(); _clock.Stop(); Opacity = 0; }
        else { _pulse.Start(); UpdateClockState(); _space.Refresh(true); }
    }
    public void HandleCommand(string command)
    {
        if (command == "--exit") ExitApp();
        else if (command == "--toggle-hidden") ToggleHidden();
        else if (command == "--enable-startup") { AppSettings.SetStartup(true); UpdateMenu(); }
        else if (command == "--disable-startup") { AppSettings.SetStartup(false); UpdateMenu(); }
        else if (command == "--status") WriteStatus();
        else if (command == "--export-preview") ExportPreview();
        else if (command == "--lyrics-retry") _lyrics.Retry(_media.State, _settings.OnlineLyrics);
        else if (command == "--enable-lyrics") { _settings.OnlineLyrics = _settings.ShowLyrics = true; Save(); _lyrics.Retry(_media.State, true); }
        else if (command == "--verify-media") _ = VerifyMediaAsync();
        else if (command.StartsWith("--language=")) SetLanguage(command[11..]);
        else if (command.StartsWith("--width=") && int.TryParse(command[8..], out var width)) SetWidth(width);
        else if (command.StartsWith("--transparency=") && int.TryParse(command[15..], out var transparency)) { _settings.BackgroundOpacityPercent = 100 - Math.Clamp(transparency, 0, 80); Save(); ApplyTheme(); }
        else if (command == "--monitor=primary") SetMonitor("");
        else if (command.StartsWith("--monitor="))
        {
            foreach (var screen in Forms.Screen.AllScreens) if (screen.DeviceName == command[10..]) { SetMonitor(screen.DeviceName); break; }
        }
        else if (command.StartsWith("--timer=") && int.TryParse(command.Substring(8), out var seconds)) { _focus.Start(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400))); RefreshContent(); }
        else if (command != "--startup" && command != "--collapse") OpenPanel();
    }
    internal void WriteStatus()
    {
        Directory.CreateDirectory(AppSettings.DataDirectory);
        NativeWindow.GetWindowRect(_handle, out var nativeBounds);
        var covering = NativeWindow.GetAncestor(NativeWindow.WindowFromPoint(new NativeWindow.Point { X = nativeBounds.Left + nativeBounds.Width / 2, Y = nativeBounds.Top + nativeBounds.Height / 2 }), 2);
        File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "status.json"), JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, _settings.Hidden, Startup = AppSettings.StartupEnabled,
            NativeVisible = NativeWindow.IsWindowVisible(_handle), Opacity, Handle = _handle.ToInt64(), Left, Top, Width, Height,
            PhysicalBounds = new { nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom }, DpiScale = _scale,
            ActualMonitor = Forms.Screen.FromHandle(_handle).DeviceName,
            CoveredByTaskbar = covering == _taskbarHandle, CoveringClass = NativeWindow.WindowClass(covering), ForegroundClass = NativeWindow.WindowClass(NativeWindow.GetForegroundWindow()),
            TaskbarCoverageRepairs = _taskbarCoverageRepairs,
            Fullscreen = _fullscreen, HasSpace = _hasSpace, Placement = _settings.Placement, _settings.MonitorDevice, _settings.Language, _settings.WidthDip,
            VisibilityReason = _visibilityReason, VisibilityTransitions = _visibilityTransitions, _settings.BackgroundOpacityPercent, LastTaskbarScanSucceeded = _space.LastScanSucceeded,
            NoActivate = (NativeWindow.GetWindowLong(_handle, NativeWindow.ExStyle) & NativeWindow.NoActivate) != 0,
            HotkeyOpen = _hotkeyOpen, HotkeyHide = _hotkeyHide, TaskbarOccupiedRight = _space.OccupiedRight,
            Media = new { _media.State.Title, _media.State.Artist, _media.State.Available, _media.State.Playing, Artwork = _media.State.Artwork != null, _media.State.Duration, Position = _media.PositionSeconds, _media.State.CanSeek, _media.LastSeekSucceeded, _media.LastSeekConfirmed },
            Lyrics = new { _settings.OnlineLyrics, _lyrics.Document.Status, SyncedLines = _lyrics.Document.Lines.Count },
            Volume = _audio.Volume, Timer = _focus.Display, TimerActive = _focus.Active,
            LongestPulseMs = _longestPulseMs, LastTaskbarScanMs = _space.LastScanMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async System.Threading.Tasks.Task VerifyMediaAsync() { await _media.SeekAsync(_media.PositionSeconds); WriteStatus(); }
    private void ExportPreview()
    {
        UpdateLayout();
        var image = new RenderTargetBitmap((int)(ActualWidth * 2), (int)(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(Shell); Directory.CreateDirectory(AppSettings.DataDirectory);
        using var stream = File.Create(Path.Combine(AppSettings.DataDirectory, "preview.png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
    private void ExitApp() { _closing = true; Close(); Application.Current.Shutdown(); }
    private void Cleanup(object? sender, EventArgs e)
    {
        _closing = true; _pulse.Stop(); _clock.Stop();
        _foregroundWatch?.Dispose();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
        NativeWindow.UnregisterHotKey(_handle, 1); NativeWindow.UnregisterHotKey(_handle, 2); _source?.RemoveHook(WindowMessage);
        _space.Dispose(); _media.Dispose(); _audio.Dispose(); _lyrics.Dispose();
        _tray.Visible = false; var icon = _tray.Icon; _tray.Dispose(); icon?.Dispose(); _menu.Dispose();
    }
}
