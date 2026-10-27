using System.Windows;
using System.Windows.Media;

namespace QuietIsland;

// Palette values adapted from devcode90/Dynamic-Island-for-Windows (MIT).
// Pinned reference and license are recorded in THIRD_PARTY_NOTICES.md.
internal sealed record ThemePalette(string Id, string Name, string Background, string Foreground, string Secondary, string Border)
{
    public static readonly ThemePalette[] All =
    {
        new("obsidian", "Обсидиан", "#08080A", "#FFFFFF", "#9B9BA5", "#1E1E22"),
        new("graphite", "Графит", "#1C1C1E", "#FFFFFF", "#A8A8AE", "#323236"),
        new("slate", "Сланец", "#111721", "#E9EEF6", "#92A2B8", "#232C3A"),
        new("nord", "Nord", "#2E3440", "#ECEFF4", "#A7B0C0", "#3B4252"),
        new("evergreen", "Хвойный", "#0C1512", "#E4F1EA", "#8CAE9D", "#1A2A23"),
        new("espresso", "Эспрессо", "#1A1512", "#F6EEE7", "#B7A395", "#2E2420"),
        new("plum", "Слива", "#16111C", "#F1EAF6", "#AC9BB9", "#281F33"),
        new("porcelain", "Фарфор · светлая", "#F5F6F8", "#14161A", "#5B6270", "#D8DBE1")
    };
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    public void Apply(Window window, System.Windows.Controls.Border surface, int opacityPercent = 90)
    {
        var bg = Parse(Background); var fg = Parse(Foreground); var border = Parse(Border);
        window.Resources["IslandText"] = Brush(fg);
        window.Resources["IslandTooltipBackground"] = Brush(bg);
        window.Resources["IslandSecondary"] = Brush(Parse(Secondary));
        window.Resources["IslandBorder"] = Brush(border);
        window.Resources["IslandControl"] = Brush(fg);
        window.Resources["IslandControlText"] = Brush(bg);
        window.Resources["IslandArtBackground"] = Brush(border);
        window.Resources["IslandTrackBackground"] = Brush(Color.FromArgb(40, fg.R, fg.G, fg.B));
        var alpha = (byte)System.Math.Round(System.Math.Clamp(opacityPercent, 20, 100) * 2.55);
        var top = Color.FromArgb(alpha, (byte)(bg.R * .91 + fg.R * .09), (byte)(bg.G * .91 + fg.G * .09), (byte)(bg.B * .91 + fg.B * .09));
        var bottom = Color.FromArgb(alpha, bg.R, bg.G, bg.B);
        var fill = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(.4, 1)); fill.Freeze(); surface.Background = fill;
        var edge = new LinearGradientBrush(); edge.StartPoint = new Point(0, 0); edge.EndPoint = new Point(.6, 1);
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(110, fg.R, fg.G, fg.B), 0));
        edge.GradientStops.Add(new GradientStop(border, .45));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(110, border.R, border.G, border.B), 1));
        edge.Freeze(); surface.BorderBrush = edge;
    }
}
