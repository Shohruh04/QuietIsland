namespace QuietIsland;

internal sealed class AppSettings
{
    public bool Hidden { get; set; }
    public bool TaskbarPlacement { get; set; } = true;
    public bool VolumeAlerts { get; set; } = true;
    public bool OnlineLyrics { get; set; } = true;
    public bool ShowLyrics { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool PositionLocked { get; set; }
    public bool AlbumAccent { get; set; }
    public string Placement { get; set; } = "left";
    public int LyricsOffsetMs { get; set; }
    public string Theme { get; set; } = "obsidian";
    public int BackgroundOpacityPercent { get; set; } = 20;
    public string MonitorDevice { get; set; } = "";
    public string Language { get; set; } = "en";
    public int WidthDip { get; set; } = 360;
    public double PositionFraction { get; set; } = .5;
}
