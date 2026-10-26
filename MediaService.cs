using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace QuietIsland;

internal sealed record MediaState(string Title = "Музыка не играет", string Artist = "Включите музыку в любимом плеере", bool Available = false, bool Playing = false,
    ImageSource? Artwork = null, bool CanPlayPause = false, bool CanPrevious = false, bool CanNext = false, string Album = "", double Duration = 0, bool CanSeek = false);
internal sealed record MediaSource(string Id, string Name, bool IsSelected);

internal sealed class MediaService : IDisposable
{
    private sealed record SourceEntry(string Id, string AppId, GlobalSystemMediaTransportControlsSession Session);
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _seekGate = new(1);
    private List<SourceEntry> _entries = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _generation, _sourceSequence, _seekSequence;
    private bool _disposed, _refreshRunning, _refreshRequested;
    private TimeSpan _position;
    private DateTimeOffset _positionAt;
    private double _rate = 1;
    private sealed record PendingSeek(double Target, DateTimeOffset Baseline, DateTimeOffset Requested, DateTimeOffset Deadline);
    private PendingSeek? _pendingSeek;
    public bool? LastSeekConfirmed { get; private set; }
    public double PositionSeconds => Math.Clamp((_position + (State.Playing ? TimeSpan.FromSeconds(Math.Max(0, (DateTimeOffset.UtcNow - _positionAt).TotalSeconds) * _rate) : TimeSpan.Zero)).TotalSeconds, 0, Math.Max(0, State.Duration));
    public MediaState State { get; private set; } = new();
    public IReadOnlyList<MediaSource> Sources { get; private set; } = Array.Empty<MediaSource>();
    // Null follows Windows' current session. Selecting an entry pins that player until it closes.
    public string? SelectedSourceId { get; private set; }
    public event Action? Changed;
    public event Action? SourcesChanged;
    public MediaService(Dispatcher dispatcher) { _dispatcher = dispatcher; }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) { _manager = null; return; }
            _manager.CurrentSessionChanged += SessionChanged;
            _manager.SessionsChanged += SessionListChanged;
            RefreshSources();
        }
        catch (Exception ex) { if (!_disposed) AppSettings.Log(ex); }
    }
    private void Queue(Action action)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action);
    }
    private void SessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Queue(RefreshSources);
    private void SessionListChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Queue(RefreshSources);

    public void RefreshSources()
    {
        if (_disposed || _manager == null) return;
        try
        {
            var next = new List<SourceEntry>();
            foreach (var session in _manager.GetSessions())
            {
                // Preserve identity, including multiple sessions from the same browser/app.
                var previous = _entries.FirstOrDefault(entry => entry.Session == session);
                next.Add(previous ?? new SourceEntry("session-" + ++_sourceSequence, session.SourceAppUserModelId, session));
            }
            _entries = next;
            var selected = _entries.FirstOrDefault(entry => entry.Id == SelectedSourceId);
            if (SelectedSourceId != null && selected == null) SelectedSourceId = null;
            AttachSession(selected?.Session ?? _manager.GetCurrentSession());
            PublishSources();
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    public bool SelectSource(string? id)
    {
        if (_disposed || _manager == null) return false;
        var entry = id == null ? null : _entries.FirstOrDefault(source => source.Id == id);
        if (id != null && entry == null) return false;
        SelectedSourceId = id;
        try
        {
            AttachSession(entry?.Session ?? _manager.GetCurrentSession());
            PublishSources();
            return true;
        }
        catch (Exception ex) { AppSettings.Log(ex); return false; }
    }
    private void PublishSources()
    {
        var duplicateNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<MediaSource>();
        foreach (var entry in _entries)
        {
            var name = SourceName(entry.AppId);
            duplicateNames.TryGetValue(name, out var count);
            duplicateNames[name] = ++count;
            sources.Add(new(entry.Id, count > 1 ? name + " (" + count + ")" : name, entry.Session == _session));
        }
        Sources = sources.AsReadOnly();
        SourcesChanged?.Invoke();
    }
    private static string SourceName(string appId)
    {
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("yandex", StringComparison.OrdinalIgnoreCase) && appId.Contains("music", StringComparison.OrdinalIgnoreCase)) return "Яндекс Музыка";
        var name = appId.Split('!')[0];
        var package = name.IndexOf('_');
        if (package > 0) name = name.Substring(0, package);
        name = Path.GetFileName(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed || _session == session) return;
        if (_session != null) Unsubscribe(_session);
        _session = session;
        _generation++;
        LastSeekSucceeded = null;
        _pendingSeek = null; LastSeekConfirmed = null;
        State = new(); _position = TimeSpan.Zero; _positionAt = DateTimeOffset.UtcNow; _rate = 1;
        Changed?.Invoke();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += PropertiesChanged;
            _session.PlaybackInfoChanged += PlaybackChanged;
            _session.TimelinePropertiesChanged += TimelineChanged;
        }
        RequestRefresh();
    }
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Queue(() => { if (sender == _session) RequestRefresh(); });
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Queue(() => { if (sender == _session) UpdatePlayback(); });
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Queue(() => { if (sender == _session) UpdateTimeline(); });
    private void UpdateTimeline()
    {
        if (_disposed || _session == null) return;
        try
        {
            var timeline = _session.GetTimelineProperties();
            State = State with { Duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds) };
            if (_pendingSeek is { } pending)
            {
                var now = DateTimeOffset.UtcNow;
                var confirmed = ConfirmsSeek((timeline.Position - timeline.StartTime).TotalSeconds, timeline.LastUpdatedTime,
                    pending.Target, pending.Baseline, pending.Requested, now, State.Playing ? _rate : 0);
                if (!confirmed && now < pending.Deadline) return;
                _pendingSeek = null; LastSeekConfirmed = confirmed;
            }
            _position = timeline.Position - timeline.StartTime;
            _positionAt = timeline.LastUpdatedTime;
            if (_positionAt == default) _positionAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    public void ReconcileSeek() { if (_pendingSeek is { } pending && DateTimeOffset.UtcNow >= pending.Deadline) { UpdateTimeline(); Changed?.Invoke(); } }
    internal static bool ConfirmsSeek(double position, DateTimeOffset timestamp, double target, DateTimeOffset baseline, DateTimeOffset requested, DateTimeOffset now, double rate) =>
        timestamp > baseline && timestamp >= requested.AddMilliseconds(-100) &&
        Math.Abs(position + Math.Max(0, (now - timestamp).TotalSeconds) * rate - (target + Math.Max(0, (now - requested).TotalSeconds) * rate)) <= 2;
    private void UpdatePlayback()
    {
        if (_disposed || _session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            _rate = info.PlaybackRate ?? 1;
            State = State with { Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPlayPause = info.Controls.IsPlayPauseToggleEnabled, CanPrevious = info.Controls.IsPreviousEnabled, CanNext = info.Controls.IsNextEnabled, CanSeek = info.Controls.IsPlaybackPositionEnabled };
            UpdateTimeline(); Changed?.Invoke();
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    private void RequestRefresh()
    {
        if (_disposed) return;
        _generation++;
        _refreshRequested = true;
        if (!_refreshRunning) _ = RefreshLoopAsync();
    }
    private bool IsCurrent(GlobalSystemMediaTransportControlsSession session, int generation) => !_disposed && generation == _generation && session == _session;
    private async Task RefreshLoopAsync()
    {
        _refreshRunning = true;
        try
        {
            // At most one metadata request/decode runs. Events during a request queue one latest refresh.
            while (_refreshRequested && !_disposed)
            {
                _refreshRequested = false;
                var version = _generation;
                var session = _session;
                if (session == null) continue;
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync().AsTask(_lifetime.Token);
                    if (!IsCurrent(session, version)) continue;
                    ImageSource? image = null;
                    try
                    {
                        if (properties.Thumbnail != null)
                        {
                            var thumbnail = properties.Thumbnail;
                            image = await Task.Run(() => LoadArtworkAsync(thumbnail, _lifetime.Token), _lifetime.Token);
                        }
                    }
                    catch (OperationCanceledException) when (_disposed) { break; }
                    catch (Exception ex) { if (!_disposed) AppSettings.Log(ex); }
                    if (!IsCurrent(session, version)) continue;
                    if (State.Title != properties.Title || State.Artist != properties.Artist) { _pendingSeek = null; LastSeekConfirmed = null; }
                    State = new(string.IsNullOrWhiteSpace(properties.Title) ? "Медиа" : properties.Title, properties.Artist, true, Artwork: image, Album: properties.AlbumTitle);
                    UpdatePlayback();
                }
                catch (OperationCanceledException) when (_disposed) { break; }
                catch (Exception ex) { if (!_disposed) AppSettings.Log(ex); }
            }
        }
        finally { _refreshRunning = false; if (_disposed) _lifetime.Dispose(); }
    }
    private static async Task<ImageSource?> LoadArtworkAsync(IRandomAccessStreamReference thumbnail, CancellationToken token)
    {
        using var stream = await thumbnail.OpenReadAsync().AsTask(token).ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size >= 8 * 1024 * 1024) return null;
        using var input = stream.AsStreamForRead();
        using var buffer = new MemoryStream((int)stream.Size);
        await input.CopyToAsync(buffer, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        buffer.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128;
        bitmap.StreamSource = buffer; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    public async Task ControlAsync(string action)
    {
        var session = _session;
        if (session == null) return;
        try
        {
            if (action == "toggle") await session.TryTogglePlayPauseAsync();
            else if (action == "next") await session.TrySkipNextAsync();
            else if (action == "previous") await session.TrySkipPreviousAsync();
        }
        catch (Exception ex) { if (!_disposed) AppSettings.Log(ex); }
    }
    public bool? LastSeekSucceeded { get; private set; }
    public async Task<bool> SeekAsync(double seconds)
    {
        var request = ++_seekSequence;
        var session = _session;
        if (_disposed || session == null || !State.CanSeek) { LastSeekSucceeded = false; return false; }
        var title = State.Title; var artist = State.Artist;
        bool Current() => !_disposed && request == _seekSequence && session == _session && State.Title == title && State.Artist == artist;
        await _seekGate.WaitAsync();
        try
        {
            if (!Current()) return false;
            var timeline = session.GetTimelineProperties();
            var position = timeline.StartTime + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, State.Duration));
            var requested = DateTimeOffset.UtcNow;
            var succeeded = await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(_lifetime.Token);
            if (!Current()) return false;
            LastSeekSucceeded = succeeded;
            if (succeeded)
            {
                _pendingSeek = new((position - timeline.StartTime).TotalSeconds, timeline.LastUpdatedTime, requested, DateTimeOffset.UtcNow.AddSeconds(2));
                LastSeekConfirmed = null;
                _position = position - timeline.StartTime; _positionAt = DateTimeOffset.UtcNow;
                UpdateTimeline(); Changed?.Invoke();
            }
            return succeeded;
        }
        catch (Exception ex) { if (Current()) LastSeekSucceeded = false; if (!_disposed) AppSettings.Log(ex); return false; }
        finally { _seekGate.Release(); }
    }
    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged -= PropertiesChanged;
        session.PlaybackInfoChanged -= PlaybackChanged;
        session.TimelinePropertiesChanged -= TimelineChanged;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _refreshRequested = false; _lifetime.Cancel();
        if (_manager != null) { _manager.CurrentSessionChanged -= SessionChanged; _manager.SessionsChanged -= SessionListChanged; }
        if (_session != null) Unsubscribe(_session);
        _manager = null; _session = null; _entries.Clear(); Sources = Array.Empty<MediaSource>();
        // The pending WinRT task/worker may still observe the cancellation source until it unwinds.
        if (!_refreshRunning) _lifetime.Dispose();
    }
}
