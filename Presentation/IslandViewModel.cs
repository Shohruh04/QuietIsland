using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietIsland;

internal sealed class IslandViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly JsonSettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private bool _disposed, _visible, _locked, _suspended, _seeking, _seekPending;
    private int _seekGeneration;
    private double _previewPosition;
    private string _alert = "";
    private DateTimeOffset _alertUntil;

    public AppSettings Settings { get; }
    public StartupRegistration Startup { get; }
    public MediaService Media { get; }
    public AudioService Audio { get; }
    public LyricsService Lyrics { get; }
    public FocusTimer Focus { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SettingsChanged;
    public event Action? MediaChanged;
    public event Action<string, int>? NotificationRequested;

    public IslandViewModel(AppSettings settings, JsonSettingsStore store, StartupRegistration startup,
        MediaService media, AudioService audio, LyricsService lyrics, FocusTimer focus, Dispatcher dispatcher)
    {
        Settings = settings; _store = store; Startup = startup; Media = media;
        Audio = audio; Lyrics = lyrics; Focus = focus; _dispatcher = dispatcher;
        UiText.Language = settings.Language == "en" ? "en" : "ru";
        PlayPauseCommand = new AsyncActionCommand(() => Media.ControlAsync("toggle"), () => CanPlayPause);
        PreviousCommand = new AsyncActionCommand(() => Media.ControlAsync("previous"), () => CanPrevious);
        NextCommand = new AsyncActionCommand(() => Media.ControlAsync("next"), () => CanNext);
        LyricsCommand = new ActionCommand(ToggleLyrics);
        Media.Changed += OnMediaChanged;
        Lyrics.Changed += Refresh;
        Audio.Changed += OnAudioChanged;
        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += Tick;
    }

    public AsyncActionCommand PlayPauseCommand { get; }
    public AsyncActionCommand PreviousCommand { get; }
    public AsyncActionCommand NextCommand { get; }
    public ICommand LyricsCommand { get; }
    public string Title => Media.State.Available ? (Media.State.Title == "Медиа" ? UiText.T("Медиа") : Media.State.Title) : "Quiet Island";
    public string Detail { get; private set; } = "";
    public string DetailTooltip => Settings.ShowLyrics ? UiText.T(Lyrics.Document.Status) : Media.State.Artist;
    public ImageSource? Artwork => Media.State.Artwork;
    public bool IsPlaying => Media.State.Playing;
    public bool HasArtwork => Artwork != null;
    public bool CanPlayPause => Media.State.Available && Media.State.CanPlayPause;
    public bool CanPrevious => Media.State.Available && Media.State.CanPrevious;
    public bool CanNext => Media.State.Available && Media.State.CanNext;
    public bool CanSeek => Media.State.Available && Media.State.CanSeek && Media.State.Duration > 0;
    public double Duration => Math.Max(1, Media.State.Duration);
    public double Position => _seeking || _seekPending ? _previewPosition : Media.PositionSeconds;
    public bool ShowLyrics => Settings.ShowLyrics;
    public bool IsVisible => _visible;
    public string CurrentSeekTrack => Media.SelectedSourceId + "\n" + Media.State.Title + "\n" + Media.State.Artist;

    public Task StartAsync() => Media.StartAsync();
    public void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    private void OnMediaChanged()
    {
        Lyrics.Update(Media.State, Settings.OnlineLyrics);
        Refresh(); MediaChanged?.Invoke();
    }
    private void OnAudioChanged()
    {
        if (Settings.VolumeAlerts) Alert(Audio.Muted ? "Звук выключен" : "Громкость: " + Math.Round(Audio.Volume * 100) + "%");
    }
    private void Tick(object? sender, EventArgs args)
    {
        if (Focus.Tick())
        {
            Alert("Таймер завершён. Пора отдохнуть.", 8);
            NotificationRequested?.Invoke(UiText.T("Таймер завершён. Пора отдохнуть."), 4);
        }
        Media.ReconcileSeek();
        if (_visible) RefreshDynamic();
        UpdateClock();
    }
    public void SetVisibility(bool visible)
    {
        if (_visible != visible) { _visible = visible; Changed(nameof(IsVisible)); }
        if (visible) RefreshDynamic();
        UpdateClock();
    }
    public void SetSessionState(bool locked, bool suspended)
    {
        _locked = locked; _suspended = suspended; UpdateClock();
    }
    private void UpdateClock()
    {
        var needed = !_disposed && !_locked && !_suspended && (Focus.Running || Media.HasPendingSeek ||
            (_visible && (Media.State.Playing || _seekPending || DateTimeOffset.UtcNow < _alertUntil)));
        if (needed) _clock.Start(); else _clock.Stop();
    }
    public void Refresh()
    {
        if (_disposed) return;
        foreach (var property in new[] { nameof(Title), nameof(DetailTooltip), nameof(Artwork), nameof(HasArtwork), nameof(IsPlaying), nameof(CanPlayPause), nameof(CanPrevious), nameof(CanNext), nameof(CanSeek), nameof(Duration), nameof(ShowLyrics) }) Changed(property);
        PlayPauseCommand.Refresh(); PreviousCommand.Refresh(); NextCommand.Refresh();
        RefreshDynamic(); UpdateClock();
    }
    private void RefreshDynamic()
    {
        var detail = _seeking || _seekPending ? UiText.T("Перемотка · ") + TimeSpan.FromSeconds(Position).ToString(@"m\:ss") :
            DateTimeOffset.UtcNow < _alertUntil ? UiText.T(_alert) : Focus.Active ? UiText.T("Фокус · ") + Focus.Display :
            Settings.ShowLyrics && Lyrics.Document.Lines.Count > 0 ? Lyrics.At(Media.PositionSeconds + Settings.LyricsOffsetMs / 1000.0).current :
            Media.State.Available ? Media.State.Artist : UiText.T("Включите музыку в любимом плеере");
        if (Detail != detail) { Detail = detail; Changed(nameof(Detail)); }
        Changed(nameof(Position));
    }
    public void Alert(string message, int seconds = 2)
    {
        _alert = message; _alertUntil = DateTimeOffset.UtcNow.AddSeconds(seconds); RefreshDynamic(); UpdateClock();
    }
    public void SaveSettings() { _store.Save(Settings); SettingsChanged?.Invoke(); Refresh(); }
    public void SetLanguage(string language)
    {
        if (language is not ("ru" or "en")) return;
        Settings.Language = language; UiText.Language = language; SaveSettings();
    }
    public void SetMonitor(string device) { Settings.MonitorDevice = device; SaveSettings(); }
    public void SetWidth(int width) { Settings.WidthDip = Math.Clamp(width, 280, 1200); SaveSettings(); }
    public void SetPosition(string position)
    {
        if (double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent))
        { Settings.Placement = "custom"; Settings.PositionFraction = Math.Clamp(percent / 100, 0, 1); }
        else if (position is "left" or "center" or "right" or "auto") Settings.Placement = position;
        else return;
        SaveSettings();
    }
    public void SetTheme(string theme) { if (Array.Exists(ThemePalette.All, item => item.Id == theme)) { Settings.Theme = theme; SaveSettings(); } }
    public void SetTransparency(int percent) { Settings.BackgroundOpacityPercent = 100 - Math.Clamp(percent, 0, 80); SaveSettings(); }
    public void ToggleLyrics()
    {
        Settings.ShowLyrics = !Settings.ShowLyrics; SaveSettings();
        if (Settings.ShowLyrics && Lyrics.Document.Lines.Count == 0) Alert(Lyrics.Document.Status, 5);
    }
    public void ToggleOnlineLyrics() { Settings.OnlineLyrics = !Settings.OnlineLyrics; Lyrics.Update(Media.State, Settings.OnlineLyrics); SaveSettings(); }
    public void RetryLyrics() => Lyrics.Retry(Media.State, Settings.OnlineLyrics);
    public void SelectLyricsCandidate(int index) { Lyrics.SelectCandidate(index); Settings.ShowLyrics = true; SaveSettings(); }
    public void AdjustLyricsOffset(int deltaMs) { Settings.LyricsOffsetMs = Math.Clamp(Settings.LyricsOffsetMs + deltaMs, -30000, 30000); SaveSettings(); }
    public void ResetLyricsOffset() { Settings.LyricsOffsetMs = 0; SaveSettings(); }
    public void SetVolumeAlerts(bool enabled) { Settings.VolumeAlerts = enabled; SaveSettings(); }
    public void SetHideInFullscreen(bool enabled) { Settings.HideInFullscreen = enabled; SaveSettings(); }
    public void ToggleStartup() { Startup.SetEnabled(!Startup.IsEnabled); SettingsChanged?.Invoke(); }
    public void StartFocus(int minutes) { StartFocusSeconds(minutes * 60); }
    public void StartFocusSeconds(int seconds) { Focus.Start(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400))); Refresh(); }
    public void ToggleFocusPause() { Focus.TogglePause(); Refresh(); }
    public void StopFocus() { Focus.Stop(); Refresh(); }
    public void ImportLyrics(string path) { Lyrics.Import(path); Settings.ShowLyrics = true; SaveSettings(); }
    public void SetVolumeDelta(float delta) => Audio.SetVolume(Audio.Volume + delta);
    public bool HandleCommand(string command)
    {
        if (command == "--toggle-hidden") { Settings.Hidden = !Settings.Hidden; SaveSettings(); }
        else if (command is "--enable-startup" or "--disable-startup") { Startup.SetEnabled(command == "--enable-startup"); SettingsChanged?.Invoke(); }
        else if (command == "--lyrics-retry") RetryLyrics();
        else if (command == "--enable-lyrics") { Settings.OnlineLyrics = Settings.ShowLyrics = true; Lyrics.Retry(Media.State, true); SaveSettings(); }
        else if (command.StartsWith("--position=")) SetPosition(command[11..]);
        else if (command.StartsWith("--language=")) SetLanguage(command[11..]);
        else if (command.StartsWith("--width=") && int.TryParse(command[8..], out var width)) SetWidth(width);
        else if (command.StartsWith("--transparency=") && int.TryParse(command[15..], out var transparency)) SetTransparency(transparency);
        else if (command == "--monitor=primary") SetMonitor("");
        else if (command.StartsWith("--monitor="))
        { foreach (var screen in System.Windows.Forms.Screen.AllScreens) if (screen.DeviceName == command[10..]) { SetMonitor(screen.DeviceName); break; } }
        else if (command.StartsWith("--timer=") && int.TryParse(command[8..], out var seconds)) StartFocusSeconds(seconds);
        else return false;
        return true;
    }
    public void BeginSeek() { _previewPosition = Media.PositionSeconds; _seeking = true; }
    public void PreviewSeek(double seconds) { _previewPosition = Math.Clamp(seconds, 0, Duration); RefreshDynamic(); }
    public void CancelSeek() { _seeking = false; RefreshDynamic(); }
    public async Task CompleteSeekAsync(double target, string track)
    {
        _seeking = false;
        if (track != CurrentSeekTrack) { RefreshDynamic(); return; }
        var generation = ++_seekGeneration; _previewPosition = target; _seekPending = true; UpdateClock();
        var success = await Media.SeekAsync(target);
        if (_disposed || generation != _seekGeneration) return;
        _seekPending = false;
        if (!success) Alert("Плеер не разрешил перемотку", 3);
        RefreshDynamic(); UpdateClock();
    }
    internal static double SeekPosition(double x, double width, double duration) => width <= 0 || duration <= 0 ? 0 : Math.Clamp((x - 2.5) / Math.Max(1, width - 5), 0, 1) * duration;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _clock.Stop(); _clock.Tick -= Tick;
        Media.Changed -= OnMediaChanged; Lyrics.Changed -= Refresh; Audio.Changed -= OnAudioChanged;
        Media.Dispose(); Audio.Dispose(); Lyrics.Dispose();
    }
}
