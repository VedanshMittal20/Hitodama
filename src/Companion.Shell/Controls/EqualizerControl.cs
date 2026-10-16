using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Companion.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Companion.Shell.Controls;

public sealed class EqualizerControl : FrameworkElement
{
    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // SoulCyan

    static EqualizerControl()
    {
        BarBrush.Freeze();
    }

    private static readonly double[] RestingHeights = [5.0, 10.0, 14.0, 9.0, 6.0];

    private readonly SpringValue[] _barSprings =
    [
        new(5.0, omega: 24.0, zeta: 0.70),
        new(10.0, omega: 26.0, zeta: 0.68),
        new(14.0, omega: 22.0, zeta: 0.72),
        new(9.0, omega: 25.0, zeta: 0.69),
        new(6.0, omega: 23.0, zeta: 0.71)
    ];

    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();
    private TimeSpan _lastTime;
    private bool _isAnimating;
    private bool _isPlaying;
    private double _audioLevel;
    private Brush? _albumBrush;

    public static readonly DependencyProperty HasAlbumPaletteProperty =
        DependencyProperty.Register(
            nameof(HasAlbumPalette),
            typeof(bool),
            typeof(EqualizerControl),
            new PropertyMetadata(false, OnPaletteChanged));

    public bool HasAlbumPalette
    {
        get => (bool)GetValue(HasAlbumPaletteProperty);
        set => SetValue(HasAlbumPaletteProperty, value);
    }

    public static readonly DependencyProperty AlbumPrimaryColorProperty =
        DependencyProperty.Register(
            nameof(AlbumPrimaryColor),
            typeof(Color),
            typeof(EqualizerControl),
            new PropertyMetadata(Color.FromRgb(56, 189, 248), OnPaletteChanged));

    public Color AlbumPrimaryColor
    {
        get => (Color)GetValue(AlbumPrimaryColorProperty);
        set => SetValue(AlbumPrimaryColorProperty, value);
    }

    public static readonly DependencyProperty AlbumSecondaryColorProperty =
        DependencyProperty.Register(
            nameof(AlbumSecondaryColor),
            typeof(Color),
            typeof(EqualizerControl),
            new PropertyMetadata(Color.FromRgb(129, 140, 248), OnPaletteChanged));

    public Color AlbumSecondaryColor
    {
        get => (Color)GetValue(AlbumSecondaryColorProperty);
        set => SetValue(AlbumSecondaryColorProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(EqualizerControl),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double AudioLevel
    {
        get => _audioLevel;
        set
        {
            _audioLevel = Math.Clamp(value, 0.0, 1.0);
            if (_isPlaying)
            {
                EnsureAnimation();
            }
        }
    }

    public EqualizerControl()
    {
        Width = 26;
        Height = 18;
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    private static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EqualizerControl control)
        {
            control.UpdateAlbumBrush();
            control.InvalidateVisual();
        }
    }

    private void UpdateAlbumBrush()
    {
        if (!HasAlbumPalette)
        {
            _albumBrush = null;
            return;
        }

        var brush = new LinearGradientBrush(AlbumPrimaryColor, AlbumSecondaryColor, 90.0);
        brush.Freeze();
        _albumBrush = brush;
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EqualizerControl control)
        {
            control._isPlaying = (bool)e.NewValue;
            if (!control._isPlaying)
            {
                for (int i = 0; i < control._barSprings.Length; i++)
                {
                    double h = i < RestingHeights.Length ? RestingHeights[i] : 3.0;
                    control._barSprings[i].Target = h;
                    control._barSprings[i].Reset(h);
                }
                control.StopAnimation();
                control.InvalidateVisual();
            }
            else
            {
                control.EnsureAnimation();
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            EnsureAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    private void EnsureAnimation()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        if (_isPlaying || _barSprings.Any(s => Math.Abs(s.Current - 3.0) > 0.05 || Math.Abs(s.Velocity) > 0.05))
        {
            if (!_isAnimating)
            {
                _isAnimating = true;
                _clock.Restart();
                _lastTime = TimeSpan.Zero;
                _timer.Start();
            }
        }
    }

    private void StopAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            _clock.Stop();
            _timer.Stop();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        if (!IsVisible)
        {
            StopAnimation();
            return;
        }

        double dt = Math.Clamp((now - _lastTime).TotalSeconds, 0.010, 0.100);
        _lastTime = now;
        double seconds = now.TotalSeconds;

        double maxHeight = Math.Max(12.0, ActualHeight > 0 ? ActualHeight : 18.0);
        double minHeight = 3.0;

        if (_isPlaying)
        {
            // 5 organic frequencies: omega = 3.7, 5.3, 4.1, 6.1, 3.3 Hz
            double wave1 = (Math.Sin(seconds * 3.7 * 2 * Math.PI) + 1.0) * 0.5;
            double wave2 = (Math.Sin(seconds * 5.3 * 2 * Math.PI + 1.2) + 1.0) * 0.5;
            double wave3 = (Math.Sin(seconds * 4.1 * 2 * Math.PI + 2.5) + 1.0) * 0.5;
            double wave4 = (Math.Sin(seconds * 6.1 * 2 * Math.PI + 0.7) + 1.0) * 0.5;
            double wave5 = (Math.Sin(seconds * 3.3 * 2 * Math.PI + 1.8) + 1.0) * 0.5;

            double boost = Math.Max(_audioLevel, 0.25);
            _barSprings[0].Target = minHeight + (maxHeight - minHeight) * (wave1 * 0.7 + boost * 0.3);
            _barSprings[1].Target = minHeight + (maxHeight - minHeight) * (wave2 * 0.6 + boost * 0.4);
            _barSprings[2].Target = minHeight + (maxHeight - minHeight) * (wave3 * 0.8 + boost * 0.2);
            _barSprings[3].Target = minHeight + (maxHeight - minHeight) * (wave4 * 0.5 + boost * 0.5);
            _barSprings[4].Target = minHeight + (maxHeight - minHeight) * (wave5 * 0.7 + boost * 0.3);
        }
        else
        {
            for (int i = 0; i < _barSprings.Length; i++)
            {
                _barSprings[i].Target = i < RestingHeights.Length ? RestingHeights[i] : 3.0;
            }
        }

        bool anyActive = false;
        foreach (var spring in _barSprings)
        {
            if (spring.Step(dt))
            {
                anyActive = true;
            }
        }

        InvalidateVisual();

        if (!anyActive && !_isPlaying)
        {
            StopAnimation();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth > 0 ? ActualWidth : 26.0;
        double h = ActualHeight > 0 ? ActualHeight : 18.0;

        const double barWidth = 2.5;
        const double barSpacing = 2.0;
        const double radius = 1.25;

        double totalBarsWidth = (5 * barWidth) + (4 * barSpacing);
        double startX = (w - totalBarsWidth) / 2.0;

        Brush brush = _albumBrush ?? (System.Windows.Application.Current?.TryFindResource("AccentBrush") as Brush) ?? BarBrush;

        for (int i = 0; i < 5; i++)
        {
            double barHeight = Math.Clamp(_barSprings[i].Current, 2.0, h);
            double x = startX + i * (barWidth + barSpacing);
            double y = h - barHeight;

            var rect = new Rect(x, y, barWidth, barHeight);
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }
}
