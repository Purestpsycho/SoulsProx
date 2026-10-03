using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SoulsProx.App;

/// <summary>
/// Round player avatar with an initial, a glowing ring while talking (like CrewLink),
/// and an optional status badge in the corner.
/// </summary>
public sealed class Avatar : Grid
{
    private const double RingThickness = 3;
    private const double RingGap = 3;

    private readonly Ellipse _ring = new() { StrokeThickness = RingThickness, Visibility = Visibility.Hidden };
    private readonly Ellipse _face = new() { Margin = new Thickness(RingThickness + RingGap) };
    private readonly TextBlock _initial = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly Border _badge = new()
    {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(2),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _badgeGlyph = new()
    {
        FontSize = 11,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9 };

    public Avatar()
    {
        _ring.Effect = _glow;
        _badge.Child = _badgeGlyph;
        Children.Add(_ring);
        Children.Add(_face);
        Children.Add(_initial);
        Children.Add(_badge);
        Loaded += (_, _) =>
        {
            _badgeGlyph.FontFamily = (FontFamily)FindResource("IconFont");
            _badge.BorderBrush = (Brush)FindResource("BgBrush");
        };
    }

    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), Brushes.Gray, (a, v) =>
    {
        var brush = (Brush)v;
        a._face.Fill = brush;
        a._initial.Foreground = IsLight(brush) ? new SolidColorBrush(Color.FromRgb(0x1D, 0x1A, 0x23)) : Brushes.White;
    });

    public static readonly DependencyProperty InitialProperty = Register(nameof(Initial), typeof(string), "", (a, v) => a._initial.Text = (string)v);
    public static readonly DependencyProperty InitialSizeProperty = Register(nameof(InitialSize), typeof(double), 24.0, (a, v) => a._initial.FontSize = (double)v);
    public static readonly DependencyProperty TalkingProperty = Register(nameof(Talking), typeof(bool), false, (a, v) => a._ring.Visibility = (bool)v ? Visibility.Visible : Visibility.Hidden);
    public static readonly DependencyProperty RingBrushProperty = Register(nameof(RingBrush), typeof(Brush), Brushes.LimeGreen, (a, v) =>
    {
        a._ring.Stroke = (Brush)v;
        if (v is SolidColorBrush s) a._glow.Color = s.Color;
    });
    public static readonly DependencyProperty ShowBadgeProperty = Register(nameof(ShowBadge), typeof(bool), false, (a, v) => a._badge.Visibility = (bool)v ? Visibility.Visible : Visibility.Collapsed);
    public static readonly DependencyProperty BadgeGlyphProperty = Register(nameof(BadgeGlyph), typeof(string), "", (a, v) => a._badgeGlyph.Text = (string)v);
    public static readonly DependencyProperty BadgeBrushProperty = Register(nameof(BadgeBrush), typeof(Brush), Brushes.Orange, (a, v) => a._badge.Background = (Brush)v);

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public string Initial { get => (string)GetValue(InitialProperty); set => SetValue(InitialProperty, value); }
    public double InitialSize { get => (double)GetValue(InitialSizeProperty); set => SetValue(InitialSizeProperty, value); }
    public bool Talking { get => (bool)GetValue(TalkingProperty); set => SetValue(TalkingProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public bool ShowBadge { get => (bool)GetValue(ShowBadgeProperty); set => SetValue(ShowBadgeProperty, value); }
    public string BadgeGlyph { get => (string)GetValue(BadgeGlyphProperty); set => SetValue(BadgeGlyphProperty, value); }
    public Brush BadgeBrush { get => (Brush)GetValue(BadgeBrushProperty); set => SetValue(BadgeBrushProperty, value); }

    private static DependencyProperty Register(string name, Type type, object defaultValue, Action<Avatar, object> apply)
    {
        var metadata = new PropertyMetadata(defaultValue, (d, e) => apply((Avatar)d, e.NewValue));
        var dp = DependencyProperty.Register(name, type, typeof(Avatar), metadata);
        return dp;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // Apply defaults for anything not set from XAML.
        _face.Fill ??= Fill;
        _ring.Stroke ??= RingBrush;
        _initial.FontSize = InitialSize;
        _badge.Background ??= BadgeBrush;
    }

    private static bool IsLight(Brush brush)
    {
        if (brush is not SolidColorBrush s) return false;
        var c = s.Color;
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 170;
    }
}

/// <summary>Microphone level bar with the voice-activity threshold marked.</summary>
public sealed class LevelBar : FrameworkElement
{
    private const double MinDb = -70, MaxDb = 0;
    private static readonly Brush Track = Frozen(Color.FromRgb(0x3E, 0x3A, 0x44));
    private static readonly Brush Idle = Frozen(Color.FromRgb(0x8A, 0x84, 0x91));
    private static readonly Brush Live = Frozen(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly Brush Marker = Frozen(Color.FromRgb(0xFF, 0xC1, 0x07));

    public static readonly DependencyProperty LevelDbProperty = DependencyProperty.Register(nameof(LevelDb), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(-100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThresholdDbProperty = DependencyProperty.Register(nameof(ThresholdDb), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(-45.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowThresholdProperty = DependencyProperty.Register(nameof(ShowThreshold), typeof(bool), typeof(LevelBar),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active), typeof(bool), typeof(LevelBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LevelDb { get => (double)GetValue(LevelDbProperty); set => SetValue(LevelDbProperty, value); }
    public double ThresholdDb { get => (double)GetValue(ThresholdDbProperty); set => SetValue(ThresholdDbProperty, value); }
    public bool ShowThreshold { get => (bool)GetValue(ShowThresholdProperty); set => SetValue(ShowThresholdProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    private double ToX(double db) => (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb) * ActualWidth;

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, h), r, r);
        double w = ToX(LevelDb);
        if (w > 0.5) dc.DrawRoundedRectangle(Active ? Live : Idle, null, new Rect(0, 0, w, h), r, r);
        if (ShowThreshold) dc.DrawRectangle(Marker, null, new Rect(Math.Max(0, ToX(ThresholdDb) - 1), -2, 2, h + 4));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
