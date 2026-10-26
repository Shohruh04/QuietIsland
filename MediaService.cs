using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace QuietIsland;

internal sealed record MediaState(string Title = "Музыка не играет", string Artist = "Включите музыку в любимом плеере", bool Available = false, bool Playing = false,
    ImageSource? Artwork = null, bool CanPlayPause = false, bool CanPrevious = false, bool CanNext = false, string Album = "", double Duration = 0, bool CanSeek = false);

internal sealed class MediaService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _generation;
    private bool _disposed;
    private TimeSpan _position;
    private DateTimeOffset _positionAt;
    private double _rate = 1;
    public double PositionSeconds => Math.Clamp((_position + (State.Playing ? TimeSpan.FromSeconds(Math.Max(0, (DateTimeOffset.UtcNow - _positionAt).TotalSeconds) * _rate) : TimeSpan.Zero)).TotalSeconds, 0, Math.Max(0, State.Duration));
    public MediaState State { get; private set; } = new();
    public event Action? Changed;
    public MediaService(Dispatcher dispatcher) { _dispatcher = dispatcher; }
    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) { _manager = null; return; }
            _manager.CurrentSessionChanged += SessionChanged;
            AttachSession();
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    private void SessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => _dispatcher.BeginInvoke(AttachSession);
    private void AttachSession()
    {
        if (_disposed) return;
        if (_session != null) { _session.MediaPropertiesChanged -= PropertiesChanged; _session.PlaybackInfoChanged -= PlaybackChanged; _session.TimelinePropertiesChanged -= TimelineChanged; }
        _session = _manager?.GetCurrentSession();
        State = new(); _position = TimeSpan.Zero; Changed?.Invoke();
        if (_session != null) { _session.MediaPropertiesChanged += PropertiesChanged; _session.PlaybackInfoChanged += PlaybackChanged; _session.TimelinePropertiesChanged += TimelineChanged; }
        _ = RefreshAsync();
    }
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _dispatcher.BeginInvoke(UpdatePlayback);
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _dispatcher.BeginInvoke(UpdateTimeline);
    private void UpdateTimeline()
    {
        if (_disposed || _session == null) return;
        try
        {
            var timeline = _session.GetTimelineProperties();
            _position = timeline.Position - timeline.StartTime;
            _positionAt = timeline.LastUpdatedTime;
            if (_positionAt == default) _positionAt = DateTimeOffset.UtcNow;
            State = State with { Duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds) };
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
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
    private async Task RefreshAsync()
    {
        var version = ++_generation;
        var session = _session;
        if (session == null) { State = new(); Changed?.Invoke(); return; }
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            ImageSource? image = null;
            try
            {
                if (properties.Thumbnail != null)
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync();
                    if (stream.Size > 0 && stream.Size < 8 * 1024 * 1024)
                    {
                        using var input = stream.AsStreamForRead();
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128;
                        bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); image = bitmap;
                    }
                }
            }
            catch (Exception ex) { AppSettings.Log(ex); } // Artwork must not block valid track metadata.
            if (_disposed || version != _generation || session != _session) return;
            State = new(string.IsNullOrWhiteSpace(properties.Title) ? "Media" : properties.Title, properties.Artist, true, Artwork: image, Album: properties.AlbumTitle);
            UpdatePlayback();
        }
        catch (Exception ex) { AppSettings.Log(ex); }
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
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    public bool? LastSeekSucceeded { get; private set; }
    public async Task SeekAsync(double seconds)
    {
        var session = _session;
        if (session == null || !State.CanSeek) { LastSeekSucceeded = false; return; }
        try
        {
            var timeline = session.GetTimelineProperties();
            var position = timeline.StartTime + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, State.Duration));
            LastSeekSucceeded = await session.TryChangePlaybackPositionAsync(position.Ticks);
            if (LastSeekSucceeded == true)
            { _position = position - timeline.StartTime; _positionAt = DateTimeOffset.UtcNow; Changed?.Invoke(); }
        }
        catch (Exception ex) { LastSeekSucceeded = false; AppSettings.Log(ex); }
    }
    public void Dispose()
    {
        _disposed = true; _generation++;
        if (_manager != null) _manager.CurrentSessionChanged -= SessionChanged;
        if (_session != null) { _session.MediaPropertiesChanged -= PropertiesChanged; _session.PlaybackInfoChanged -= PlaybackChanged; _session.TimelinePropertiesChanged -= TimelineChanged; }
        _manager = null; _session = null;
    }
}
