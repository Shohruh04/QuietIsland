using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuietIsland;

/// <summary>A normal, focusable settings window. The taskbar island keeps its own window behavior.</summary>
public partial class SettingsWindow : Window
{
    private readonly IslandViewModel _model;
    private readonly UpdateController _updates;
    private readonly IntPtr _updateOwner;
    private readonly DispatcherTimer _sliderTimer;
    private readonly List<(RadioButton Button, ThemePalette Palette, TextBlock Label)> _themes = new();
    private bool _refreshing, _committing, _closed;
    private string _language = "";
    private int? _pendingWidth, _pendingTransparency, _pendingOffset;

    internal SettingsWindow(IslandViewModel model, UpdateController updates, IntPtr updateOwner = default)
    {
        _model = model;
        _updates = updates;
        _updateOwner = updateOwner;
        _refreshing = true;
        InitializeComponent();
        DataContext = model;
        _sliderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _sliderTimer.Tick += SliderTimerTick;
        foreach (var slider in new[] { WidthSlider, TransparencySlider, OffsetSlider })
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SliderDragCompleted));
        try
        {
            var icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "QuietIsland.ico")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (icon.CanFreeze) icon.Freeze();
            Icon = AppIcon.Source = icon;
        }
        catch (Exception exception) { AppLog.Write(exception); }
        CreateThemes();
        CreatePlacementOptions();
        RefreshMonitors();
        _model.SettingsChanged += SettingsChanged;
        _model.MediaChanged += MediaChanged;
        _model.PropertyChanged += ModelPropertyChanged;
        _updates.Changed += UpdatesChanged;
        Loaded += FitInitialWorkArea;
        _refreshing = false;
        RefreshValues();
    }

    private void FitInitialWorkArea(object sender, RoutedEventArgs e)
    {
        Loaded -= FitInitialWorkArea;
        var handle = new WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var workWidth = area.Width / dpi.DpiScaleX;
        var workHeight = area.Height / dpi.DpiScaleY;
        const double margin = 12;
        var availableWidth = Math.Max(1, workWidth - margin * 2);
        var availableHeight = Math.Max(1, workHeight - margin * 2);
        // Lower the minimum only when the display cannot accommodate it. Do not constrain later resizing.
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = area.Left / dpi.DpiScaleX + (workWidth - Width) / 2;
        Top = area.Top / dpi.DpiScaleY + (workHeight - Height) / 2;
    }

    private void CreateThemes()
    {
        foreach (var palette in ThemePalette.All)
        {
            var label = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            var swatch = new Border
            {
                Width = 23, Height = 23, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 9, 0),
                Background = Brush(palette.Background), BorderBrush = Brush("#7D98B6"), BorderThickness = new Thickness(1)
            };
            panel.Children.Add(swatch);
            Grid.SetColumn(label, 1); panel.Children.Add(label);
            var button = new RadioButton { GroupName = "IslandTheme", Tag = palette.Id, Content = panel, Style = (Style)FindResource("ThemeChoice") };
            button.Checked += ThemeChanged;
            ThemeChoices.Children.Add(button);
            _themes.Add((button, palette, label));
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        var result = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        result.Freeze();
        return result;
    }

    private void CreatePlacementOptions()
    {
        foreach (var value in new[] { "left", "center", "right", "auto", "custom" })
            PlacementPicker.Items.Add(new ComboBoxItem { Tag = value });
    }

    private void SettingsChanged()
    {
        if (_closed || _committing) return;
        OnUi(RefreshValues);
    }

    private void MediaChanged() => OnUi(UpdatePreview);
    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.AccentColor)) OnUi(UpdatePreview);
    }
    private void UpdatesChanged() => OnUi(RefreshUpdateStatus);
    private void OnUi(Action action)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(new Action(() => { if (!_closed) action(); }));
    }

    private void RefreshValues()
    {
        if (_closed) return;
        _refreshing = true;
        try
        {
            var settings = _model.Settings;
            if (_language != settings.Language) ApplyLanguage();
            foreach (var item in _themes) item.Button.IsChecked = item.Palette.Id == settings.Theme;
            AlbumAccentToggle.IsChecked = settings.AlbumAccent;
            PositionLockedToggle.IsChecked = settings.PositionLocked;
            LyricsToggle.IsChecked = settings.ShowLyrics;
            OnlineLyricsToggle.IsChecked = settings.OnlineLyrics;
            StartupToggle.IsChecked = _model.Startup.IsEnabled;
            FullscreenToggle.IsChecked = settings.HideInFullscreen;
            VolumeToggle.IsChecked = settings.VolumeAlerts;
            AutoUpdatesToggle.IsChecked = settings.AutoCheckUpdates;
            SelectValue(PlacementPicker, settings.Placement);
            SelectValue(MonitorPicker, settings.MonitorDevice);
            SelectValue(LanguagePicker, settings.Language);
            if (_pendingWidth is null && !WidthSlider.IsMouseCaptureWithin) WidthSlider.Value = settings.WidthDip;
            if (_pendingTransparency is null && !TransparencySlider.IsMouseCaptureWithin) TransparencySlider.Value = 100 - settings.BackgroundOpacityPercent;
            if (_pendingOffset is null && !OffsetSlider.IsMouseCaptureWithin) OffsetSlider.Value = settings.LyricsOffsetMs / 1000.0;
            UpdateSliderLabels();
            UpdatePreview();
            RefreshUpdateStatus();
        }
        finally { _refreshing = false; }
    }

    private void ApplyLanguage()
    {
        _language = _model.Settings.Language;
        Title = "QuietIsland · " + UiText.T("Настройки");
        TranslateLabels(this);
        foreach (var (button, palette, label) in _themes)
        {
            label.Text = UiText.T(palette.Name);
            button.ToolTip = label.Text;
            AutomationProperties.SetName(button, label.Text);
        }
        var placements = new[] { "Слева", "По центру", "Справа", "Автоматически", "Своя позиция" };
        for (var i = 0; i < placements.Length; i++) ((ComboBoxItem)PlacementPicker.Items[i]).Content = UiText.T(placements[i]);
        RefreshMonitors();
        VersionLabel.Text = UiText.T("Версия") + " " + GitHubUpdateService.CurrentVersion.ToString(3);
        CloseButton.ToolTip = UiText.T("Закрыть");
        foreach (var (element, text) in new (DependencyObject, string)[]
        {
            (CloseButton, "Закрыть"), (AlbumAccentToggle, "Использовать цвет обложки"),
            (PositionLockedToggle, "Закрепить положение"), (LyricsToggle, "Показывать строку песни"),
            (OnlineLyricsToggle, "Онлайн-текст · LRCLIB"), (StartupToggle, "Запускать вместе с Windows"),
            (FullscreenToggle, "Скрывать в полноэкранном режиме"), (VolumeToggle, "Показывать изменение громкости"),
            (AutoUpdatesToggle, "Автоматически проверять обновления"), (CheckUpdatesButton, "Проверить обновления…"),
            (LanguagePicker, "Язык"), (MonitorPicker, "Монитор"), (PlacementPicker, "Положение на панели задач"),
            (WidthSlider, "Ширина виджета"), (TransparencySlider, "Прозрачность фона"), (OffsetSlider, "Синхронизация текста")
        }) AutomationProperties.SetName(element, UiText.T(text));
    }

    private static void TranslateLabels(DependencyObject root)
    {
        if (root is TextBlock { Tag: string text } label) label.Text = UiText.T(text);
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element) TranslateLabels(element);
    }

    private static void SelectValue(ComboBox picker, string value)
    {
        foreach (ComboBoxItem item in picker.Items)
            if (item.Tag is string candidate && candidate == value) { picker.SelectedItem = item; return; }
        picker.SelectedIndex = -1;
    }

    private void RefreshMonitors()
    {
        var wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            MonitorPicker.Items.Clear();
            MonitorPicker.Items.Add(new ComboBoxItem { Tag = "", Content = UiText.T("Основной монитор") });
            var index = 0;
            var found = _model.Settings.MonitorDevice.Length == 0;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var content = UiText.T("Монитор") + " " + ++index + " · " + screen.Bounds.Width + " × " + screen.Bounds.Height;
                MonitorPicker.Items.Add(new ComboBoxItem { Tag = screen.DeviceName, Content = content });
                found |= screen.DeviceName == _model.Settings.MonitorDevice;
            }
            if (!found)
                MonitorPicker.Items.Add(new ComboBoxItem { Tag = _model.Settings.MonitorDevice, Content = UiText.T("Отключённый монитор") });
            SelectValue(MonitorPicker, _model.Settings.MonitorDevice);
        }
        finally { _refreshing = wasRefreshing; }
    }

    private void MonitorOpened(object? sender, EventArgs e) => RefreshMonitors();
    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && sender is RadioButton { Tag: string id, IsChecked: true }) _model.SetTheme(id);
    }
    private void PlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || PlacementPicker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _model.SetPosition(value == "custom" ? (_model.Settings.PositionFraction * 100).ToString(CultureInfo.InvariantCulture) : value);
    }
    private void MonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && MonitorPicker.SelectedItem is ComboBoxItem { Tag: string value }) _model.SetMonitor(value);
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && LanguagePicker.SelectedItem is ComboBoxItem { Tag: string value }) _model.SetLanguage(value);
    }

    private void WidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing) return;
        _pendingWidth = (int)Math.Round(WidthSlider.Value);
        ScheduleSliderCommit();
    }
    private void TransparencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing) return;
        _pendingTransparency = (int)Math.Round(TransparencySlider.Value);
        ScheduleSliderCommit();
    }
    private void OffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing) return;
        _pendingOffset = (int)Math.Round(OffsetSlider.Value * 1000);
        ScheduleSliderCommit();
    }
    private void ScheduleSliderCommit()
    {
        UpdateSliderLabels();
        UpdatePreview();
        _sliderTimer.Stop();
        _sliderTimer.Start();
    }
    private void SliderTimerTick(object? sender, EventArgs e) => FlushSliders();
    private void SliderDragCompleted(object sender, DragCompletedEventArgs e) => FlushSliders();
    private void FlushSliders()
    {
        _sliderTimer.Stop();
        var width = _pendingWidth; var transparency = _pendingTransparency; var offset = _pendingOffset;
        _pendingWidth = _pendingTransparency = _pendingOffset = null;
        _committing = true;
        try
        {
            if (width is { } w && w != _model.Settings.WidthDip) _model.SetWidth(w);
            if (transparency is { } t && t != 100 - _model.Settings.BackgroundOpacityPercent) _model.SetTransparency(t);
            if (offset is { } o && o != _model.Settings.LyricsOffsetMs) _model.AdjustLyricsOffset(o - _model.Settings.LyricsOffsetMs);
        }
        finally { _committing = false; }
    }

    private void UpdateSliderLabels()
    {
        WidthValue.Text = Math.Round(WidthSlider.Value).ToString(CultureInfo.InvariantCulture) + " DIP";
        TransparencyValue.Text = Math.Round(TransparencySlider.Value).ToString(CultureInfo.InvariantCulture) + "%";
        var culture = CultureInfo.GetCultureInfo(_language == "ru" ? "ru-RU" : "en-US");
        OffsetValue.Text = OffsetSlider.Value.ToString("+0.#;-0.#;0", culture) + " " + UiText.T("с");
    }

    private void UpdatePreview()
    {
        if (_closed) return;
        PreviewIsland.Width = WidthSlider.Value;
        var palette = Array.Find(ThemePalette.All, value => value.Id == _model.Settings.Theme) ?? ThemePalette.All[0];
        palette.Apply(this, PreviewIsland, 100 - (int)Math.Round(TransparencySlider.Value), _model.Settings.AlbumAccent ? _model.AccentColor : null);
    }

    private void RefreshUpdateStatus()
    {
        if (_closed) return;
        AutoUpdatesToggle.IsChecked = _model.Settings.AutoCheckUpdates;
        CheckUpdatesButton.IsEnabled = !_updates.IsBusy;
        UpdateStatus.Text = _updates.IsBusy ? UiText.T("Проверка / загрузка обновления…") :
            _updates.IsChecking ? UiText.T("Проверка обновлений…") :
            _updates.AvailableVersion is { } available ? string.Format(UiText.T("Доступна версия {0}"), available.ToString(3)) :
            UiText.T("Проверяйте новые версии здесь или включите автоматическую проверку.");
    }

    private void AlbumAccentClick(object sender, RoutedEventArgs e) => _model.SetAlbumAccent(AlbumAccentToggle.IsChecked == true);
    private void PositionLockedClick(object sender, RoutedEventArgs e) => _model.SetPositionLocked(PositionLockedToggle.IsChecked == true);
    private void LyricsClick(object sender, RoutedEventArgs e) { if (LyricsToggle.IsChecked != _model.Settings.ShowLyrics) _model.ToggleLyrics(); }
    private void OnlineLyricsClick(object sender, RoutedEventArgs e) { if (OnlineLyricsToggle.IsChecked != _model.Settings.OnlineLyrics) _model.ToggleOnlineLyrics(); }
    private void StartupClick(object sender, RoutedEventArgs e) { if (StartupToggle.IsChecked != _model.Startup.IsEnabled) _model.ToggleStartup(); }
    private void FullscreenClick(object sender, RoutedEventArgs e) => _model.SetHideInFullscreen(FullscreenToggle.IsChecked == true);
    private void VolumeClick(object sender, RoutedEventArgs e) => _model.SetVolumeAlerts(VolumeToggle.IsChecked == true);
    private void AutoUpdatesClick(object sender, RoutedEventArgs e) { if (AutoUpdatesToggle.IsChecked != _model.Settings.AutoCheckUpdates) _updates.ToggleAutomatic(); }
    private void ResetOffsetClick(object sender, RoutedEventArgs e) { _pendingOffset = null; _model.ResetLyricsOffset(); }
    // The island outlives this window; closing Settings during a network request
    // must not leave the result/download dialog with a destroyed owner.
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await _updates.CheckManuallyAsync(_updateOwner);
    private void ReleasesClick(object sender, RoutedEventArgs e) => OpenLink(GitHubUpdateService.Repository + "/releases/latest");
    private void SourceClick(object sender, RoutedEventArgs e) => OpenLink(GitHubUpdateService.Repository);
    private static void OpenLink(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception exception) { AppLog.Write(exception); }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !PlacementPicker.IsDropDownOpen && !MonitorPicker.IsDropDownOpen && !LanguagePicker.IsDropDownOpen)
        { e.Handled = true; Close(); }
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Loaded -= FitInitialWorkArea;
        FlushSliders();
        _sliderTimer.Tick -= SliderTimerTick;
        _model.SettingsChanged -= SettingsChanged;
        _model.MediaChanged -= MediaChanged;
        _model.PropertyChanged -= ModelPropertyChanged;
        _updates.Changed -= UpdatesChanged;
        base.OnClosed(e);
    }
}
