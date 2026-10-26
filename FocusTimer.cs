using System;

namespace QuietIsland;

internal sealed class FocusTimer
{
    private DateTimeOffset? _end;
    private TimeSpan _paused;
    public bool Active => _end.HasValue || _paused > TimeSpan.Zero;
    public bool Running => _end.HasValue;
    public TimeSpan Remaining => _end.HasValue ? TimeSpan.FromTicks(Math.Max(0, (_end.Value - DateTimeOffset.UtcNow).Ticks)) : _paused;
    public void Start(TimeSpan time) { _paused = TimeSpan.Zero; _end = DateTimeOffset.UtcNow + time; }
    public void TogglePause()
    {
        if (_end.HasValue) { _paused = Remaining; _end = null; }
        else if (_paused > TimeSpan.Zero) { _end = DateTimeOffset.UtcNow + _paused; _paused = TimeSpan.Zero; }
    }
    public void Stop() { _end = null; _paused = TimeSpan.Zero; }
    public bool Tick()
    {
        if (!_end.HasValue || Remaining > TimeSpan.Zero) return false;
        Stop(); return true;
    }
    public string Display => Remaining.ToString(Remaining.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
}
