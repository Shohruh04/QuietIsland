using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietIsland;

internal static class ArtworkAccent
{
    private const int SampleEdge = 32;
    private const double MinimumContrast = 3;

    internal static Color? Extract(BitmapSource source)
    {
        if (source is null || (!source.IsFrozen && !source.CheckAccess())) return null;
        try
        {
            var size = GetSampleSize(source.PixelWidth, source.PixelHeight);
            if (size.Width == 0 || size.Height == 0) return null;

            // Scale before converting/copying so tall or wide artwork cannot allocate
            // a source-sized pixel buffer. Rounding retains at least one pixel per axis.
            BitmapSource sampled = source;
            if (size.Width != source.PixelWidth || size.Height != source.PixelHeight)
                sampled = new TransformedBitmap(source, new ScaleTransform(
                    (double)size.Width / source.PixelWidth, (double)size.Height / source.PixelHeight));
            if (sampled.PixelWidth <= 0 || sampled.PixelHeight <= 0 ||
                sampled.PixelWidth > SampleEdge || sampled.PixelHeight > SampleEdge) return null;
            if (sampled.Format != PixelFormats.Bgra32)
                sampled = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);

            var stride = sampled.PixelWidth * 4;
            var pixels = new byte[stride * sampled.PixelHeight];
            sampled.CopyPixels(pixels, stride, 0);
            return ExtractPixels(pixels);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NotSupportedException or IOException or ExternalException)
        {
            // Artwork is optional; unsupported or incomplete images retain the theme accent.
            return null;
        }
    }

    internal static (int Width, int Height) GetSampleSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        var scale = Math.Min(1, (double)SampleEdge / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static Color? ExtractPixels(ReadOnlySpan<byte> pixels)
    {
        if (pixels.IsEmpty || pixels.Length % 4 != 0 || pixels.Length > SampleEdge * SampleEdge * 4) return null;
        // Three bits per channel group nearby colors without allowing vivid isolated
        // pixels to outweigh a larger, moderately saturated region.
        Span<int> weights = stackalloc int[512];
        Span<int> reds = stackalloc int[512];
        Span<int> greens = stackalloc int[512];
        Span<int> blues = stackalloc int[512];
        weights.Clear(); reds.Clear(); greens.Clear(); blues.Clear();
        var visible = 0;
        var colored = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            int alpha = pixels[offset + 3];
            if (alpha < 128) continue;
            visible++;
            int blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            if (maximum < 24 || minimum > 235 || maximum - minimum < Math.Max(24, maximum * .18)) continue;
            colored++;
            var bucket = (red >> 5) << 6 | (green >> 5) << 3 | blue >> 5;
            weights[bucket] += alpha;
            reds[bucket] += red * alpha;
            greens[bucket] += green * alpha;
            blues[bucket] += blue * alpha;
        }
        // A few colored compression artifacts on otherwise neutral art are not an accent.
        if (colored == 0 || colored < Math.Min(3, visible) || colored * 20 < visible) return null;
        var winner = 0;
        for (var bucket = 1; bucket < weights.Length; bucket++)
            if (weights[bucket] > weights[winner]) winner = bucket;
        var weight = weights[winner];
        return Color.FromRgb((byte)(reds[winner] / weight), (byte)(greens[winner] / weight), (byte)(blues[winner] / weight));
    }

    internal static Color EnsureContrast(Color accent, Color background)
    {
        accent = Color.FromRgb(accent.R, accent.G, accent.B);
        var backgroundLuminance = Luminance(background);
        if (Contrast(accent, backgroundLuminance) >= MinimumContrast) return accent;

        var dark = FindContrast(accent, backgroundLuminance, 0);
        var light = FindContrast(accent, backgroundLuminance, 255);
        return dark.Amount < light.Amount ? dark.Color : light.Color;
    }

    private static (Color Color, double Amount) FindContrast(Color accent, double backgroundLuminance, byte target)
    {
        var result = Color.FromRgb(target, target, target);
        if (Contrast(result, backgroundLuminance) < MinimumContrast) return (result, double.PositiveInfinity);
        double low = 0, high = 1;
        for (var step = 0; step < 16; step++)
        {
            var amount = (low + high) / 2;
            var candidate = Color.FromRgb(
                (byte)Math.Round(accent.R + (target - accent.R) * amount),
                (byte)Math.Round(accent.G + (target - accent.G) * amount),
                (byte)Math.Round(accent.B + (target - accent.B) * amount));
            if (Contrast(candidate, backgroundLuminance) >= MinimumContrast) { high = amount; result = candidate; }
            else low = amount;
        }
        return (result, high);
    }

    private static double Contrast(Color color, double backgroundLuminance)
    {
        var luminance = Luminance(color);
        return (Math.Max(luminance, backgroundLuminance) + .05) / (Math.Min(luminance, backgroundLuminance) + .05);
    }

    private static double Luminance(Color color)
        => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
