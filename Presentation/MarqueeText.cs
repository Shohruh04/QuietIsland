using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace QuietIsland;

/// <summary>Clipped, low-frame-rate scrolling for text wider than its allotted column.</summary>
public sealed class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MarqueeText), new FrameworkPropertyMetadata(true));

    private const double Gap = 24;
    private const double Speed = 24;
    private readonly TextBlock _first;
    private readonly TextBlock _second;
    private readonly TextStrip _strip;
    private readonly TranslateTransform _offset = new();
    private DispatcherOperation? _pending;
    private bool _loaded;
    private string _animatedText = "";
    private double _animatedDistance;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    internal bool IsScrolling { get; private set; }

    public MarqueeText()
    {
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _first = CreateTextBlock();
        _second = CreateTextBlock();
        _second.Visibility = Visibility.Hidden;
        _strip = new TextStrip(_first, _second) { RenderTransform = _offset };
        Content = new Border { ClipToBounds = true, Child = _strip };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private TextBlock CreateTextBlock()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None };
        foreach (var (property, name) in new[]
        {
            (TextBlock.TextProperty, nameof(Text)),
            (TextBlock.FontFamilyProperty, nameof(FontFamily)),
            (TextBlock.FontSizeProperty, nameof(FontSize)),
            (TextBlock.FontWeightProperty, nameof(FontWeight)),
            (TextBlock.FontStyleProperty, nameof(FontStyle)),
            (TextBlock.FontStretchProperty, nameof(FontStretch)),
            (TextBlock.ForegroundProperty, nameof(Foreground))
        })
            block.SetBinding(property, new Binding(name) { Source = this, Mode = BindingMode.OneWay });
        return block;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Foreground is bound explicitly, so a theme change does not restart the scroll.
        if (_strip is null) return;
        if (e.Property == IsActiveProperty || e.Property == IsVisibleProperty)
        {
            if (!IsActive || !IsVisible) StopAnimation();
            else ScheduleRefresh();
        }
        else if (e.Property == TextProperty || e.Property == FontFamilyProperty ||
            e.Property == FontSizeProperty || e.Property == FontWeightProperty ||
            e.Property == FontStyleProperty || e.Property == FontStretchProperty)
            ScheduleRefresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged) ScheduleRefresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ScheduleRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _pending?.Abort();
        _pending = null;
        StopAnimation();
    }

    private void ScheduleRefresh()
    {
        if (!_loaded || _pending is not null) return;
        // Let property bindings and the enclosing star-sized column settle first.
        _pending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pending = null;
            RefreshAnimation();
        }));
    }

    private void RefreshAnimation()
    {
        var width = ActualWidth;
        if (!_loaded || !IsActive || !IsVisible || !double.IsFinite(width) || width <= 0 || string.IsNullOrEmpty(Text))
        {
            StopAnimation();
            return;
        }
        _first.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _first.DesiredSize.Width;
        if (!double.IsFinite(textWidth) || textWidth <= width + 1)
        {
            StopAnimation();
            return;
        }
        var distance = textWidth + Gap;
        if (IsScrolling && _animatedText == Text && Math.Abs(_animatedDistance - distance) < .01) return;

        StopAnimation();
        _second.Visibility = Visibility.Visible;
        _animatedText = Text;
        _animatedDistance = distance;
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1 + distance / Speed))));
        Timeline.SetDesiredFrameRate(animation, 24);
        animation.Freeze();
        _offset.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
        IsScrolling = true;
    }

    private void StopAnimation()
    {
        if (IsScrolling)
        {
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _offset.X = 0;
            IsScrolling = false;
        }
        _second.Visibility = Visibility.Hidden;
    }

    // The strip reports its text height, but never requests its full text width.
    // Its parent can therefore constrain a long title to a finite Grid star column.
    private sealed class TextStrip : Panel
    {
        private readonly TextBlock _first, _second;
        public TextStrip(TextBlock first, TextBlock second)
        {
            _first = first;
            _second = second;
            Children.Add(first);
            Children.Add(second);
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            _first.Measure(unbounded);
            _second.Measure(unbounded);
            return new Size(0, _first.DesiredSize.Height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = _first.DesiredSize;
            var top = Math.Max(0, (finalSize.Height - size.Height) / 2);
            _first.Arrange(new Rect(new Point(0, top), size));
            _second.Arrange(new Rect(new Point(size.Width + Gap, top), _second.DesiredSize));
            return finalSize;
        }
    }
}
