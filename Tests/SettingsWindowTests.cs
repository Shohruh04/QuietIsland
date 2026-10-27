using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsWindowTests
{
    [STATestMethod]
    public void SavedChoicesAndLanguageAreVisibleWithoutOpeningAWindow()
    {
        using var fixture = new Fixture(new AppSettings
        {
            Language = "en", Theme = "plum", Placement = "right", WidthDip = 360,
            BackgroundOpacityPercent = 72, PositionLocked = true, AlbumAccent = false,
            ShowLyrics = false, OnlineLyrics = false, LyricsOffsetMs = -2500
        });
        var window = fixture.Window;
        var content = Layout(window);
        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual("QuietIsland · Settings", window.Title);
        Assert.AreEqual("Appearance", Label(window, "Внешний вид").Text);
        Assert.AreEqual("Use album color", Label(window, "Использовать цвет обложки").Text);
        Assert.AreEqual("Lock position", AutomationProperties.GetName(Control<CheckBox>(window, "PositionLockedToggle")));
        Assert.IsTrue(Control<CheckBox>(window, "PositionLockedToggle").IsChecked == true);
        Assert.IsTrue(Control<CheckBox>(window, "AlbumAccentToggle").IsChecked == false);
        Assert.IsTrue(Control<CheckBox>(window, "LyricsToggle").IsChecked == false);
        Assert.IsTrue(Control<CheckBox>(window, "OnlineLyricsToggle").IsChecked == false);
        Assert.AreEqual("right", ((ComboBoxItem)Control<ComboBox>(window, "PlacementPicker").SelectedItem).Tag);
        Assert.AreEqual("plum", LogicalDescendants(window).OfType<RadioButton>().Single(choice => choice.IsChecked == true).Tag);
        Assert.AreEqual(28d, Control<Slider>(window, "TransparencySlider").Value);
        Assert.AreEqual(-2.5, Control<Slider>(window, "OffsetSlider").Value);
        RenderIfRequested(content, "settings-appearance-en.png");
        RenderTabIfRequested(window, 1, "settings-behavior-en.png");

        var language = Control<ComboBox>(window, "LanguagePicker");
        language.SelectedItem = language.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, "ru"));
        content = Layout(window);
        Assert.AreEqual("ru", fixture.Store.Load().Language);
        Assert.AreEqual("QuietIsland · Настройки", window.Title);
        Assert.AreEqual("Внешний вид", Label(window, "Внешний вид").Text);
        Assert.AreEqual("Использовать цвет обложки", Label(window, "Использовать цвет обложки").Text);
        Assert.AreEqual("Закрепить положение", AutomationProperties.GetName(Control<CheckBox>(window, "PositionLockedToggle")));
        Assert.IsTrue(Control<CheckBox>(window, "PositionLockedToggle").IsChecked == true, "Changing language must preserve the selected settings.");
        RenderTabIfRequested(window, 0, "settings-appearance-ru.png");
        RenderTabIfRequested(window, 1, "settings-behavior-ru.png");
    }

    [STATestMethod]
    public void PreviewStaysInsideItsViewportAcrossSupportedWidths()
    {
        using var fixture = new Fixture(new AppSettings { Language = "en", WidthDip = 360 });
        var window = fixture.Window;
        var preview = Control<Border>(window, "PreviewIsland");
        var slider = Control<Slider>(window, "WidthSlider");
        foreach (var width in new[] { 280, 360, 1200 })
        {
            slider.Value = width;
            var content = Layout(window);
            var bounds = preview.TransformToAncestor(content).TransformBounds(new Rect(preview.RenderSize));
            Assert.AreEqual((double)width, preview.Width, "The preview must respond before saving the slider value.");
            Assert.IsGreaterThan(0d, bounds.Width);
            Assert.IsGreaterThan(0d, bounds.Height);
            Assert.IsTrue(bounds.Left >= -0.5 && bounds.Right <= content.ActualWidth + 0.5,
                $"Preview width {width} escaped the {content.ActualWidth} DIP viewport: {bounds}.");
            Assert.IsTrue(bounds.Top >= -0.5 && bounds.Bottom <= content.ActualHeight + 0.5,
                $"Preview width {width} escaped the vertical viewport: {bounds}.");
            RenderIfRequested(content, "settings-width-" + width + ".png");
        }
        Assert.IsFalse(window.IsVisible);
    }

    [STATestMethod]
    public void ToggleClicksPersistAndClosingFlushesDeferredSliderChanges()
    {
        using var fixture = new Fixture(new AppSettings
        {
            Language = "en", WidthDip = 360, BackgroundOpacityPercent = 20,
            LyricsOffsetMs = 0, PositionLocked = false, AlbumAccent = false
        });
        var window = fixture.Window;
        Layout(window);
        ClickToggle(Control<CheckBox>(window, "PositionLockedToggle"), true);
        ClickToggle(Control<CheckBox>(window, "AlbumAccentToggle"), true);
        var saved = fixture.Store.Load();
        Assert.IsTrue(saved.PositionLocked);
        Assert.IsTrue(saved.AlbumAccent);

        var persistedBeforeDrag = File.ReadAllText(fixture.SettingsPath);
        Control<Slider>(window, "WidthSlider").Value = 950;
        Control<Slider>(window, "TransparencySlider").Value = 35;
        Control<Slider>(window, "OffsetSlider").Value = 12.5;
        Assert.AreEqual(950d, Control<Border>(window, "PreviewIsland").Width);
        Assert.AreEqual(360, fixture.Model.Settings.WidthDip, "Dragging must not synchronously save every width change.");
        Assert.AreEqual(20, fixture.Model.Settings.BackgroundOpacityPercent);
        Assert.AreEqual(0, fixture.Model.Settings.LyricsOffsetMs);
        Assert.AreEqual(persistedBeforeDrag, File.ReadAllText(fixture.SettingsPath));

        fixture.CloseWindow();
        saved = fixture.Store.Load();
        Assert.AreEqual(950, saved.WidthDip);
        Assert.AreEqual(65, saved.BackgroundOpacityPercent);
        Assert.AreEqual(12500, saved.LyricsOffsetMs);
        Assert.IsTrue(saved.PositionLocked);
        Assert.IsTrue(saved.AlbumAccent);
        Assert.IsFalse(window.IsVisible);
    }

    private static T Control<T>(SettingsWindow window, string name) where T : FrameworkElement
    {
        var element = window.FindName(name) as T;
        Assert.IsNotNull(element, "Expected a settings control named " + name + ".");
        return element;
    }

    private static TextBlock Label(SettingsWindow window, string source)
        => LogicalDescendants(window).OfType<TextBlock>().Single(label => Equals(label.Tag, source));

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (var descendant in LogicalDescendants(element)) yield return descendant;
        }
    }

    private static FrameworkElement Layout(SettingsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(810, 790);
        // Give the never-shown parent a layout size before flushing bindings and
        // selected tab content; no native window or input is created.
        window.Measure(size);
        window.Arrange(new Rect(size));
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        content.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);
        InvalidateLayout(content);
        content.Measure(size);
        content.Arrange(new Rect(size));
        foreach (var name in new[] { "AppIcon", "CloseButton" })
        {
            var headerControl = Control<FrameworkElement>(window, name);
            var bounds = headerControl.TransformToAncestor(content).TransformBounds(new Rect(headerControl.RenderSize));
            Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= size.Width && bounds.Top >= 0 && bounds.Bottom <= 76 && bounds.Width > 0 && bounds.Height > 0,
                "The offscreen header must remain inside the title row: " + name + " " + bounds);
        }
        var preview = Control<Border>(window, "PreviewIsland");
        foreach (var path in new[] { "Title", "Detail" })
        {
            var label = LogicalDescendants(preview).OfType<TextBlock>()
                .Single(item => BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == path);
            Assert.IsFalse(string.IsNullOrWhiteSpace(label.Text), "The offscreen preview binding must resolve: " + path);
        }
        var tabs = LogicalDescendants(window).OfType<TabControl>().Single();
        var selectedContent = (FrameworkElement)((TabItem)tabs.SelectedItem).Content;
        Assert.IsNotNull(VisualTreeHelper.GetParent(selectedContent), "The selected tab must have a visual host before rendering.");
        Assert.IsTrue(selectedContent.ActualWidth > 0 && selectedContent.ActualHeight > 0, "The selected tab must have a nonempty layout.");
        var tabBounds = selectedContent.TransformToAncestor(content).TransformBounds(new Rect(selectedContent.RenderSize));
        Assert.IsTrue(tabBounds.Top >= 76 && tabBounds.Bottom <= size.Height, "The selected tab must render inside the settings content area.");
        return content;
    }

    private static void InvalidateLayout(DependencyObject visual)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            InvalidateLayout(VisualTreeHelper.GetChild(visual, index));
        if (visual is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
            element.InvalidateVisual();
        }
    }

    private static void ClickToggle(CheckBox toggle, bool value)
    {
        toggle.IsChecked = value;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
    }

    private static void RenderTabIfRequested(SettingsWindow window, int index, string fileName)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUIETISLAND_RENDER_SETTINGS_DIR"))) return;
        var tabs = LogicalDescendants(window).OfType<TabControl>().Single();
        var previousIndex = tabs.SelectedIndex;
        try
        {
            tabs.SelectedIndex = index;
            RenderIfRequested(Layout(window), fileName);
        }
        finally { tabs.SelectedIndex = previousIndex; Layout(window); }
    }

    private static void RenderIfRequested(FrameworkElement content, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("QUIETISLAND_RENDER_SETTINGS_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Assert.IsTrue(Path.IsPathFullyQualified(directory), "The optional render destination must be an explicit absolute directory.");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The settings tests must never perform an HTTP request.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _originalLanguage = UiText.Language;
        private readonly string _testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QuietIsland.Tests"));
        private readonly string _directory;
        private IslandViewModel? _model;
        private UpdateController? _updates;
        private SettingsWindow? _window;
        private bool _closed;
        public JsonSettingsStore Store { get; }
        public IslandViewModel Model => _model!;
        public SettingsWindow Window => _window!;
        public string SettingsPath => Path.Combine(_directory, "settings.json");

        public Fixture(AppSettings settings)
        {
            _directory = Path.Combine(_testRoot, "SettingsWindow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new JsonSettingsStore(_directory);
            try
            {
                Store.Save(settings);
                var dispatcher = Dispatcher.CurrentDispatcher;
                var startup = new StartupRegistration();
                _model = new IslandViewModel(settings, Store, startup, new MediaService(dispatcher),
                    new AudioService(dispatcher), new LyricsService(), new FocusTimer(), dispatcher);
                _model.Refresh();
                _updates = new UpdateController(new GitHubUpdateService(new RejectNetwork()), new UpdateInstaller(),
                    settings, startup, _model.SaveSettings, Path.Combine(_directory, "Updates"));
                _window = new SettingsWindow(_model, _updates);
                // Native nonclient chrome requires a shown HWND. These tests render
                // only client content and intentionally never create that surface.
                System.Windows.Shell.WindowChrome.SetWindowChrome(_window, null);
                _window.Closed += (_, _) => _closed = true;
            }
            catch { Dispose(); throw; }
        }

        public void CloseWindow()
        {
            if (_window != null && !_closed) _window.Close();
        }

        public void Dispose()
        {
            try { CloseWindow(); }
            finally
            {
                _updates?.Dispose();
                _model?.Dispose();
                UiText.Language = _originalLanguage;
                var resolved = Path.GetFullPath(_directory);
                if (resolved.StartsWith(_testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                    Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
