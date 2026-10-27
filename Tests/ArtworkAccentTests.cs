using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class ArtworkAccentTests
{
    [TestMethod]
    public void DominantRegionWinsOverMoreSaturatedMinority()
    {
        var dominant = Color.FromRgb(180, 80, 60);
        var pixels = Pixels(100, dominant);
        for (var pixel = 80; pixel < 100; pixel++) SetPixel(pixels, pixel, Colors.Blue);
        Assert.AreEqual(dominant, ArtworkAccent.ExtractPixels(pixels));
    }

    [TestMethod]
    public void TransparentColorCannotOutvoteVisibleArtwork()
    {
        var pixels = Pixels(100, Color.FromArgb(0, 255, 0, 0));
        for (var pixel = 90; pixel < 100; pixel++) SetPixel(pixels, pixel, Colors.SeaGreen);
        Assert.AreEqual(Colors.SeaGreen, ArtworkAccent.ExtractPixels(pixels));
        Assert.IsNull(ArtworkAccent.ExtractPixels(Pixels(20, Color.FromArgb(127, 255, 0, 0))));
    }

    [TestMethod]
    public void NeutralArtworkAndIsolatedColoredNoiseUseThemeFallback()
    {
        foreach (var color in new[] { Colors.Transparent, Colors.Black, Colors.White, Colors.Gray, Color.FromRgb(120, 125, 130) })
            Assert.IsNull(ArtworkAccent.ExtractPixels(Pixels(100, color)), color.ToString());
        var pixels = Pixels(100, Colors.Gray);
        for (var pixel = 0; pixel < 4; pixel++) SetPixel(pixels, pixel, Colors.Red);
        Assert.IsNull(ArtworkAccent.ExtractPixels(pixels));
    }

    [TestMethod]
    public void EmptyMalformedAndOversizedPixelInputsReturnNull()
    {
        Assert.IsNull(ArtworkAccent.ExtractPixels(Array.Empty<byte>()));
        Assert.IsNull(ArtworkAccent.ExtractPixels(new byte[7]));
        Assert.IsNull(ArtworkAccent.ExtractPixels(new byte[32 * 32 * 4 + 4]));
        Assert.IsNull(ArtworkAccent.Extract(null!));
        Assert.IsNull(ArtworkAccent.Extract(new BitmapImage()));
    }

    [TestMethod]
    public void SampleDimensionsRemainBoundedForExtremeAspectRatios()
    {
        Assert.AreEqual((32, 16), ArtworkAccent.GetSampleSize(128, 64));
        Assert.AreEqual((1, 32), ArtworkAccent.GetSampleSize(128, int.MaxValue));
        Assert.AreEqual((32, 1), ArtworkAccent.GetSampleSize(int.MaxValue, 128));
        Assert.AreEqual((32, 32), ArtworkAccent.GetSampleSize(int.MaxValue, int.MaxValue));
        Assert.AreEqual((1, 1), ArtworkAccent.GetSampleSize(1, 1));
        Assert.AreEqual((0, 0), ArtworkAccent.GetSampleSize(0, 32));
        Assert.AreEqual((0, 0), ArtworkAccent.GetSampleSize(32, -1));
    }

    [TestMethod]
    public void BitmapExtractionHandlesScalingAndPixelFormatConversion()
    {
        const int width = 128, height = 64;
        var pixels = new byte[width * height * 3];
        for (var offset = 0; offset < pixels.Length; offset += 3)
        {
            pixels[offset] = 30;
            pixels[offset + 1] = 100;
            pixels[offset + 2] = 200;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        bitmap.Freeze();
        var accent = ArtworkAccent.Extract(bitmap);
        Assert.IsTrue(accent.HasValue);
        Assert.AreEqual(Color.FromRgb(200, 100, 30), accent.Value);
    }

    [TestMethod]
    public void BitmapExtractionHandlesVeryTallAndVeryWideImages()
    {
        var pixels = Pixels(16_384, Colors.Coral);
        foreach (var size in new[] { (Width: 1, Height: 16_384), (Width: 16_384, Height: 1) })
        {
            var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null, pixels, size.Width * 4);
            bitmap.Freeze();
            Assert.AreEqual(Colors.Coral, ArtworkAccent.Extract(bitmap));
        }
    }

    [TestMethod]
    public void ContrastAdjustmentCoversEveryThemeAndBothBrightnessExtremes()
    {
        foreach (var theme in ThemePalette.All)
        {
            var background = (Color)ColorConverter.ConvertFromString(theme.Background);
            foreach (var accent in new[] { Colors.Black, Colors.White, Colors.Blue, Colors.Red, Colors.Yellow, background })
            {
                var adjusted = ArtworkAccent.EnsureContrast(accent, background);
                Assert.IsTrue(ContrastRatio(adjusted, background) >= 3, $"{theme.Id}: {accent} -> {adjusted}");
                Assert.AreEqual((byte)255, adjusted.A);
            }
        }
    }

    [TestMethod]
    public void SufficientContrastPreservesColorAndAdjustmentRetainsChannelOrder()
    {
        Assert.AreEqual(Colors.Coral, ArtworkAccent.EnsureContrast(Colors.Coral, Colors.Black));
        var darkened = ArtworkAccent.EnsureContrast(Color.FromRgb(255, 210, 150), Colors.White);
        Assert.IsTrue(darkened.R > darkened.G && darkened.G > darkened.B);
        Assert.IsTrue(darkened.R < 255);
        var lightened = ArtworkAccent.EnsureContrast(Color.FromRgb(20, 35, 55), Colors.Black);
        Assert.IsTrue(lightened.B > lightened.G && lightened.G > lightened.R);
        Assert.IsTrue(lightened.B > 55);
    }

    private static byte[] Pixels(int count, Color color)
    {
        var pixels = new byte[count * 4];
        for (var pixel = 0; pixel < count; pixel++) SetPixel(pixels, pixel, color);
        return pixels;
    }

    private static void SetPixel(byte[] pixels, int pixel, Color color)
    {
        var offset = pixel * 4;
        pixels[offset] = color.B; pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R; pixels[offset + 3] = color.A;
    }

    private static double ContrastRatio(Color left, Color right)
    {
        static double Channel(byte value) => value <= 10 ? value / 3294.6 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        static double Luminance(Color color) => Channel(color.R) * .2126 + Channel(color.G) * .7152 + Channel(color.B) * .0722;
        var first = Luminance(left); var second = Luminance(right);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
}
