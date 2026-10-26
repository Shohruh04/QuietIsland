using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace QuietIsland;

internal static class TaskbarLayout
{
    // All inputs and output are physical pixels. Reserve every observed button.
    public static Rect? Choose(NativeWindow.Rect bar, int trayLeft, IReadOnlyList<Rect> buttons, double scale, string placement, int widthDip = 480, double positionFraction = .5)
    {
        if (bar.Width <= bar.Height || bar.Height < 24 * scale) return null;
        var margin = 8 * scale;
        var minimum = 280 * scale;
        var desired = Math.Clamp(widthDip, 280, 1200) * scale;
        var end = Math.Min(bar.Right, trayLeft) - margin;
        var cursor = (double)bar.Left;
        var gaps = new List<(double left, double right)>();
        foreach (var button in buttons.OrderBy(b => b.Left))
        {
            var left = Math.Max(bar.Left, button.Left - margin);
            if (left - cursor >= minimum) gaps.Add((cursor, Math.Min(left, end)));
            cursor = Math.Max(cursor, button.Right + margin);
        }
        if (end - cursor >= minimum) gaps.Add((cursor, end));
        gaps = gaps.Where(g => g.right - g.left >= minimum).ToList();
        if (gaps.Count == 0) return null;
        if (placement is "custom" or "center")
        {
            var desiredX = bar.Left + Math.Clamp(double.IsFinite(positionFraction) ? positionFraction : .5, 0, 1) * Math.Max(0, end - bar.Left - desired);
            var targetCenter = (bar.Left + end) / 2;
            return gaps.Select(g =>
            {
                var availableWidth = Math.Min(desired, g.right - g.left);
                var left = placement == "center" ? Math.Clamp(targetCenter - availableWidth / 2, g.left, g.right - availableWidth) : Math.Clamp(desiredX, g.left, g.right - availableWidth);
                return new Rect(Math.Round(left), bar.Top, Math.Floor(availableWidth), bar.Height);
            }).OrderBy(r => placement == "center" ? Math.Abs(r.Left + r.Width / 2 - targetCenter) : Math.Abs(r.Left - desiredX)).First();
        }
        var gap = placement == "right" ? gaps[^1] : placement == "auto" ? gaps.OrderByDescending(g => g.right - g.left).First() : gaps[0];
        var width = Math.Min(desired, gap.right - gap.left);
        var x = placement == "right" ? gap.right - width : gap.left;
        return new Rect(Math.Round(x), bar.Top, Math.Floor(width), bar.Height);
    }
}
