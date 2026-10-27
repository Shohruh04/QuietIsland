using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace QuietIsland;

/// <summary>Owns the notification-area icon and menus; settings and actions live in the view model.</summary>
internal sealed class TrayMenuController : IDisposable
{
    private readonly IslandViewModel _viewModel;
    private readonly Action _show;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu = new();
    private sealed record MenuText(string Text, string Tooltip);
    private readonly ConditionalWeakTable<Forms.ToolStripItem, MenuText> _menuText = new();
    private readonly Forms.ToolStripMenuItem _hiddenItem, _startupItem, _onlineItem, _lyricsItem, _volumeItem, _fullscreenItem;
    private readonly Forms.ToolStripMenuItem[] _placementItems;
    private bool _disposed;
    private AppSettings _settings => _viewModel.Settings;
    private MediaService _media => _viewModel.Media;
    private LyricsService _lyrics => _viewModel.Lyrics;
    public bool IsMenuVisible => !_disposed && _menu.Visible;

    public TrayMenuController(IslandViewModel viewModel, Action show, Action hideToggle,
        Action importLyrics, Action customizeWidth, Action customizePosition, Action exit)
    {
        _viewModel = viewModel;
        _show = show;
        _menu.Items.Add("Показать виджет                  Ctrl+Alt+I", null, (_, _) => OnUi(_show));
        _hiddenItem = AddToggle("Скрыть                                 Ctrl+Alt+D", hideToggle);
        var positions = new Forms.ToolStripMenuItem("Расположение");
        var names = new[] { "Слева", "По центру", "Справа", "Автоматически: больше свободного места", "Своя позиция…" };
        var values = new[] { "left", "center", "right", "auto", "custom" };
        _placementItems = new Forms.ToolStripMenuItem[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            _placementItems[i] = new Forms.ToolStripMenuItem(names[i], null, (_, _) => OnUi(() => { if (value == "custom") customizePosition(); else _viewModel.SetPosition(value); }));
            positions.DropDownItems.Add(_placementItems[i]);
        }
        _menu.Items.Add(positions);
        var sizes = new Forms.ToolStripMenuItem("Размер");
        foreach (var width in new[] { 280, 360, 480, 600, 720, 960 })
        {
            var selectedWidth = width;
            sizes.DropDownItems.Add(new Forms.ToolStripMenuItem(width + " DIP", null, (_, _) => OnUi(() => _viewModel.SetWidth(selectedWidth))) { Tag = width });
        }
        sizes.DropDownItems.Add("Своя ширина…", null, (_, _) => OnUi(customizeWidth));
        sizes.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in sizes.DropDownItems) item.Checked = item.Tag is int width && width == _settings.WidthDip; };
        _menu.Items.Add(sizes);
        var monitors = new Forms.ToolStripMenuItem("Монитор");
        monitors.DropDownOpening += (_, _) =>
        {
            ClearItems(monitors.DropDownItems);
            monitors.DropDownItems.Add(new Forms.ToolStripMenuItem(UiText.T("Основной монитор"), null, (_, _) => OnUi(() => _viewModel.SetMonitor(""))) { Checked = _settings.MonitorDevice.Length == 0 });
            var index = 0;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var device = screen.DeviceName;
                monitors.DropDownItems.Add(new Forms.ToolStripMenuItem(UiText.T("Монитор") + " " + ++index + " · " + screen.Bounds.Width + " × " + screen.Bounds.Height,
                    null, (_, _) => OnUi(() => _viewModel.SetMonitor(device))) { Checked = _settings.MonitorDevice == device });
            }
        };
        _menu.Items.Add(monitors);
        var languages = new Forms.ToolStripMenuItem("Язык");
        foreach (var (code, label) in new[] { ("ru", "Русский"), ("en", "English") })
        {
            var language = code;
            languages.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => OnUi(() => _viewModel.SetLanguage(language))) { Tag = code });
        }
        languages.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in languages.DropDownItems) item.Checked = (string)item.Tag! == _settings.Language; };
        _menu.Items.Add(languages);
        var themes = new Forms.ToolStripMenuItem("Цветовая тема");
        foreach (var theme in ThemePalette.All)
        {
            var selected = theme;
            themes.DropDownItems.Add(new Forms.ToolStripMenuItem(theme.Name, null, (_, _) => OnUi(() => _viewModel.SetTheme(selected.Id))) { Tag = theme.Id });
        }
        themes.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in themes.DropDownItems) item.Checked = (string?)item.Tag == _settings.Theme; };
        _menu.Items.Add(themes);
        var transparency = new Forms.ToolStripMenuItem("Прозрачность фона");
        foreach (var percent in new[] { 0, 10, 25, 40, 60, 80 })
        {
            var opacity = 100 - percent;
            transparency.DropDownItems.Add(new Forms.ToolStripMenuItem(percent == 0 ? "Непрозрачный фон" : percent + "% прозрачности", null,
                (_, _) => OnUi(() => _viewModel.SetTransparency(100 - opacity))) { Tag = opacity });
        }
        transparency.DropDownOpening += (_, _) => { foreach (Forms.ToolStripMenuItem item in transparency.DropDownItems) item.Checked = (int)item.Tag! == _settings.BackgroundOpacityPercent; };
        _menu.Items.Add(transparency);
        var sources = new Forms.ToolStripMenuItem("Источник музыки");
        sources.DropDownOpening += (_, _) =>
        {
            _media.RefreshSources(); ClearItems(sources.DropDownItems);
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
        _lyricsItem = AddToggle("Показывать строку песни", () => _viewModel.ToggleLyrics());
        _onlineItem = AddToggle("Онлайн-текст · LRCLIB", () => _viewModel.ToggleOnlineLyrics());
        _onlineItem.ToolTipText = "Для поиска в LRCLIB передаются название, исполнитель, альбом и длительность песни.";
        _menu.Items.Add("Обновить текст песни", null, (_, _) => OnUi(() => _viewModel.RetryLyrics()));
        var lyricVersions = new Forms.ToolStripMenuItem("Выбрать версию текста");
        lyricVersions.DropDownOpening += (_, _) =>
        {
            ClearItems(lyricVersions.DropDownItems);
            for (var i = 0; i < _lyrics.Candidates.Count; i++)
            {
                var index = i; var candidate = _lyrics.Candidates[i];
                lyricVersions.DropDownItems.Add(candidate.Artist + " — " + candidate.Title + " (" + TimeSpan.FromSeconds(candidate.Duration).ToString(@"m\:ss") + ")",
                    null, (_, _) => OnUi(() => _viewModel.SelectLyricsCandidate(index)));
            }
            if (lyricVersions.DropDownItems.Count == 0) lyricVersions.DropDownItems.Add(new Forms.ToolStripMenuItem("Нет вариантов: обновите текст песни") { Enabled = false });
            LocalizeMenu(lyricVersions.DropDownItems);
        };
        _menu.Items.Add(lyricVersions);
        var syncMenu = new Forms.ToolStripMenuItem("Синхронизация текста");
        syncMenu.DropDownItems.Add("Показать строку раньше на 0,5 с", null, (_, _) => OnUi(() => _viewModel.AdjustLyricsOffset(500)));
        syncMenu.DropDownItems.Add("Показать строку позже на 0,5 с", null, (_, _) => OnUi(() => _viewModel.AdjustLyricsOffset(-500)));
        syncMenu.DropDownItems.Add("Сбросить сдвиг", null, (_, _) => OnUi(() => _viewModel.ResetLyricsOffset()));
        _menu.Items.Add(syncMenu);
        _menu.Items.Add("Загрузить локальный файл .lrc…", null, (_, _) => OnUi(importLyrics));
        _volumeItem = AddToggle("Показывать изменение громкости", () => _viewModel.SetVolumeAlerts(!_settings.VolumeAlerts));
        _fullscreenItem = AddToggle("Скрывать в полноэкранном режиме", () => _viewModel.SetHideInFullscreen(!_settings.HideInFullscreen));
        var timer = new Forms.ToolStripMenuItem("Таймер фокусировки");
        foreach (var minutes in new[] { 5, 25, 50 })
        {
            var duration = minutes;
            timer.DropDownItems.Add(minutes + " минут", null, (_, _) => OnUi(() => _viewModel.StartFocus(duration)));
        }
        timer.DropDownItems.Add("Пауза / продолжить", null, (_, _) => OnUi(() => _viewModel.ToggleFocusPause()));
        timer.DropDownItems.Add("Остановить", null, (_, _) => OnUi(() => _viewModel.StopFocus()));
        _menu.Items.Add(timer);
        _startupItem = AddToggle("Запускать вместе с Windows", () => _viewModel.ToggleStartup());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Открыть папку программы", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true }));
        var version = typeof(App).Assembly.GetName().Version;
        var about = new Forms.ToolStripMenuItem("О программе");
        about.DropDownItems.Add(new Forms.ToolStripMenuItem("QuietIsland " + version?.ToString(3)) { Enabled = false });
        about.DropDownItems.Add("Релизы и обновления", null, (_, _) => Process.Start(new ProcessStartInfo("https://github.com/Shohruh04/QuietIsland/releases/latest") { UseShellExecute = true }));
        about.DropDownItems.Add("Исходный код · MIT", null, (_, _) => Process.Start(new ProcessStartInfo("https://github.com/Shohruh04/QuietIsland") { UseShellExecute = true }));
        _menu.Items.Add(about);
        _menu.Items.Add("Выход", null, (_, _) => OnUi(exit));
        _tray = new Forms.NotifyIcon { Text = "Quiet Island · Ctrl+Alt+I", Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "QuietIsland.ico"), 32, 32), ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += TrayDoubleClick;
        _menu.Opening += MenuOpening;
        _viewModel.SettingsChanged += ViewModelSettingsChanged;
        ApplyLanguage();
    }

    private void OnUi(Action action) => _viewModel.Dispatch(() => { if (!_disposed) action(); });
    private void TrayDoubleClick(object? sender, EventArgs e) => OnUi(_show);
    private void MenuOpening(object? sender, CancelEventArgs e)
    {
        if (_disposed) { e.Cancel = true; return; }
        UpdateMenu();
        LocalizeMenu(_menu.Items);
    }
    private void ViewModelSettingsChanged() => Refresh();

    public void ShowContextMenu() => OnUi(() =>
    {
        UpdateMenu();
        LocalizeMenu(_menu.Items);
        _menu.Show(Forms.Cursor.Position);
    });

    public void Refresh() => OnUi(() =>
    {
        UpdateMenu();
        LocalizeMenu(_menu.Items);
    });

    public void ApplyLanguage() => OnUi(() => LocalizeMenu(_menu.Items));

    public void Notify(string message, int seconds = 4) => OnUi(() =>
        _tray.ShowBalloonTip(Math.Clamp(seconds, 1, 30) * 1000, "Quiet Island", UiText.T(message), Forms.ToolTipIcon.Info));

    private Forms.ToolStripMenuItem AddToggle(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => OnUi(action));
        _menu.Items.Add(item);
        return item;
    }

    private static void ClearItems(Forms.ToolStripItemCollection items)
    {
        while (items.Count > 0)
        {
            var item = items[0];
            items.RemoveAt(0);
            item.Dispose();
        }
    }

    private void LocalizeMenu(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            var original = _menuText.GetValue(item, value => new(value.Text ?? "", value.ToolTipText ?? ""));
            item.Text = UiText.T(original.Text);
            item.ToolTipText = UiText.T(original.Tooltip);
            if (item is Forms.ToolStripMenuItem child) LocalizeMenu(child.DropDownItems);
        }
    }

    private void UpdateMenu()
    {
        _hiddenItem.Checked = _settings.Hidden;
        _startupItem.Checked = _viewModel.Startup.IsEnabled;
        _onlineItem.Checked = _settings.OnlineLyrics;
        _lyricsItem.Checked = _settings.ShowLyrics;
        _volumeItem.Checked = _settings.VolumeAlerts;
        _fullscreenItem.Checked = _settings.HideInFullscreen;
        var values = new[] { "left", "center", "right", "auto", "custom" };
        for (var i = 0; i < values.Length; i++) _placementItems[i].Checked = _settings.Placement == values[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.SettingsChanged -= ViewModelSettingsChanged;
        _menu.Opening -= MenuOpening;
        _tray.DoubleClick -= TrayDoubleClick;
        _tray.Visible = false;
        var icon = _tray.Icon;
        _tray.Dispose();
        icon?.Dispose();
        _menu.Dispose();
    }
}
