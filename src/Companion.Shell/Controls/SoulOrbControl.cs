using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Companion.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Companion.Shell.Controls;

public enum SoulState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Acting,
    Error
}

public sealed class SoulOrbControl : FrameworkElement
{
    private static readonly Brush SoulCyanBrush = CreateMultiStopBrush(
        (Color.FromArgb(255, 255, 255, 255), 0.0),
        (Color.FromArgb(235, 56, 189, 248), 0.38),
        (Color.FromArgb(140, 99, 102, 241), 0.72),
        (Color.FromArgb(0, 15, 23, 42), 1.0));
    private static readonly Brush InnerBrush = CreateMultiStopBrush(
        (Color.FromArgb(250, 255, 255, 255), 0.0),
        (Color.FromArgb(150, 186, 230, 253), 0.45),
        (Color.FromArgb(0, 56, 189, 248), 1.0));
    private static readonly Brush FlameBrush = CreateMultiStopBrush(
        (Color.FromArgb(245, 224, 254, 254), 0.0),
        (Color.FromArgb(210, 56, 189, 248), 0.30),
        (Color.FromArgb(160, 129, 140, 248), 0.65),
        (Color.FromArgb(85, 52, 211, 153), 0.88),
        (Color.FromArgb(0, 20, 30, 60), 1.0));
    private static readonly Brush HaloBrush = CreateMultiStopBrush(
        (Color.FromArgb(150, 56, 189, 248), 0.0),
        (Color.FromArgb(95, 129, 140, 248), 0.40),
        (Color.FromArgb(50, 52, 211, 153), 0.72),
        (Color.FromArgb(0, 15, 23, 42), 1.0));
    private static readonly Brush SpecularBrush = CreateMultiStopBrush(
        (Color.FromArgb(250, 255, 255, 255), 0.0),
        (Color.FromArgb(130, 224, 242, 254), 0.40),
        (Color.FromArgb(0, 255, 255, 255), 1.0));
    private static readonly Brush CyberCoreBrush = CreateRadialBrush(
        Color.FromRgb(244, 245, 255), Color.FromRgb(129, 140, 248), Color.FromArgb(0, 49, 46, 129));
    private static readonly Brush CyberFlameBrush = CreateRadialBrush(
        Color.FromArgb(225, 196, 181, 253), Color.FromArgb(165, 129, 140, 248), Color.FromArgb(0, 67, 56, 202));
    private static readonly Brush EmberCoreBrush = CreateRadialBrush(
        Color.FromRgb(255, 251, 235), Color.FromRgb(251, 191, 36), Color.FromArgb(0, 180, 83, 9));
    private static readonly Brush EmberFlameBrush = CreateRadialBrush(
        Color.FromArgb(225, 255, 237, 166), Color.FromArgb(165, 249, 115, 22), Color.FromArgb(0, 194, 65, 12));
    private static readonly Brush VoidCoreBrush = CreateRadialBrush(
        Color.FromRgb(245, 243, 255), Color.FromRgb(167, 139, 250), Color.FromArgb(0, 49, 46, 129));
    private static readonly Brush VoidFlameBrush = CreateRadialBrush(
        Color.FromArgb(225, 196, 181, 253), Color.FromArgb(165, 99, 102, 241), Color.FromArgb(0, 30, 27, 75));
    private static readonly Brush EmeraldCoreBrush = CreateRadialBrush(
        Color.FromRgb(236, 253, 245), Color.FromRgb(16, 185, 129), Color.FromArgb(0, 4, 120, 87));
    private static readonly Brush EmeraldFlameBrush = CreateRadialBrush(
        Color.FromArgb(225, 167, 243, 208), Color.FromArgb(165, 52, 211, 153), Color.FromArgb(0, 6, 95, 70));

    private static readonly Brush ErrorCoreBrush = CreateRadialBrush(
        Color.FromRgb(254, 202, 202), Color.FromRgb(239, 68, 68), Color.FromArgb(0, 153, 27, 27));
    private static readonly Brush ErrorHaloBrush = CreateRadialBrush(
        Color.FromArgb(160, 239, 68, 68), Color.FromArgb(80, 220, 38, 38), Color.FromArgb(0, 69, 10, 10));

    private readonly Stopwatch _clock = new();
    private SoulState _soulState;
    private SoulVisualStyle _visualStyle = SoulVisualStyle.VoidFlame;
    private double _audioLevel;
    private bool _isLoaded;
    private bool _isSubscribed;
    private TimeSpan _lastRenderTime;
    private static readonly TimeSpan FrameInterval30Fps = TimeSpan.FromSeconds(1.0 / 30.0);

    public SoulState SoulState
    {
        get => _soulState;
        set
        {
            if (_soulState == value)
            {
                return;
            }

            _soulState = value;
            UpdateAnimationSubscription();
            InvalidateVisual();
        }
    }

    public double AudioLevel
    {
        get => _audioLevel;
        set
        {
            double level = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
            if (Math.Abs(_audioLevel - level) < 0.002)
            {
                return;
            }

            _audioLevel = level;
            if (_soulState is SoulState.Listening or SoulState.Speaking)
            {
                InvalidateVisual();
            }
        }
    }

    public SoulVisualStyle VisualStyle
    {
        get => _visualStyle;
        set
        {
            if (_visualStyle == value)
            {
                return;
            }

            _visualStyle = value;
            InvalidateVisual();
        }
    }

    public SoulOrbControl()
    {
        Width = 112;
        Height = 112;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double size = Math.Min(width, height);
        Point center = new((width - size) / 2 + size / 2, (height - size) / 2 + size / 2);
        double radius = size * 0.36;
        double time = _clock.Elapsed.TotalSeconds;

        if (_soulState == SoulState.Idle)
        {
            // Idle: solid core, faint resting halo. No animation loop when idle.
            drawingContext.DrawEllipse(StyleHaloBrush, null, center, radius * 1.35, radius * 1.35);
            drawingContext.DrawEllipse(StyleFlameBrush, null, center, radius * 1.08, radius * 1.08);
            drawingContext.DrawEllipse(StyleCoreBrush, null, center, radius * 0.82, radius * 0.82);
            drawingContext.DrawEllipse(InnerBrush, null, center, radius * 0.54, radius * 0.54);
            drawingContext.DrawEllipse(SpecularBrush, null,
                new Point(center.X - radius * 0.20, center.Y - radius * 0.25), radius * 0.20, radius * 0.12);
            return;
        }

        if (_soulState == SoulState.Error)
        {
            // Error: desaturated red core, brief single-axis jitter (3 oscillations over 180ms), then static.
            double jitter = 0;
            if (time < 0.18)
            {
                jitter = Math.Sin(time / 0.18 * Math.PI * 6.0) * (radius * 0.15);
            }
            Point jitteredCenter = new(center.X + jitter, center.Y);
            drawingContext.DrawEllipse(ErrorHaloBrush, null, jitteredCenter, radius * 1.30, radius * 1.30);
            drawingContext.DrawEllipse(ErrorCoreBrush, null, jitteredCenter, radius * 0.85, radius * 0.85);
            drawingContext.DrawEllipse(SpecularBrush, null,
                new Point(jitteredCenter.X - radius * 0.18, jitteredCenter.Y - radius * 0.22), radius * 0.18, radius * 0.10);
            return;
        }

        // Active solid core
        drawingContext.DrawEllipse(StyleHaloBrush, null, center, radius * 1.15, radius * 1.15);
        drawingContext.DrawEllipse(StyleCoreBrush, null, center, radius * 0.82, radius * 0.82);
        drawingContext.DrawEllipse(InnerBrush, null, center, radius * 0.54, radius * 0.54);
        drawingContext.DrawEllipse(SpecularBrush, null,
            new Point(center.X - radius * 0.20, center.Y - radius * 0.25), radius * 0.20, radius * 0.12);

        if (_soulState == SoulState.Listening)
        {
            // Listening: concentric ring that scales with input volume clamped to 1.3x.
            double scale = 1.0 + Math.Clamp(_audioLevel, 0.0, 1.0) * 0.30;
            double ringRadius = radius * 1.25 * scale;
            var pen = new Pen(StyleFlameBrush, 2.0);
            drawingContext.DrawEllipse(null, pen, center, ringRadius, ringRadius);
        }
        else if (_soulState == SoulState.Thinking)
        {
            // Thinking: rotating segmented arc (3 dashes orbiting).
            double ringRadius = radius * 1.25;
            double baseAngle = time * 3.5;
            const double arcLength = Math.PI / 4.0;
            var pen = new Pen(StyleFlameBrush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            for (int i = 0; i < 3; i++)
            {
                double angle1 = baseAngle + i * (2.0 * Math.PI / 3.0);
                double angle2 = angle1 + arcLength;
                Point p1 = new(center.X + Math.Cos(angle1) * ringRadius, center.Y + Math.Sin(angle1) * ringRadius);
                Point p2 = new(center.X + Math.Cos(angle2) * ringRadius, center.Y + Math.Sin(angle2) * ringRadius);

                var arcGeom = new StreamGeometry();
                using (var sgc = arcGeom.Open())
                {
                    sgc.BeginFigure(p1, isFilled: false, isClosed: false);
                    sgc.ArcTo(p2, new Size(ringRadius, ringRadius), 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
                }
                arcGeom.Freeze();
                drawingContext.DrawGeometry(null, pen, arcGeom);
            }
        }
        else if (_soulState == SoulState.Speaking)
        {
            // Speaking: outer ring whose thickness breathes with the output envelope.
            double thickness = 1.5 + Math.Max(0.12, _audioLevel) * 4.5;
            double ringRadius = radius * 1.25;
            var pen = new Pen(StyleFlameBrush, thickness);
            drawingContext.DrawEllipse(null, pen, center, ringRadius, ringRadius);
        }
        else if (_soulState == SoulState.Acting)
        {
            // Acting: single satellite dot orbiting once per second.
            double orbitRadius = radius * 1.28;
            double angle = 2.0 * Math.PI * time;
            Point satellite = new(center.X + Math.Cos(angle) * orbitRadius, center.Y + Math.Sin(angle) * orbitRadius);
            drawingContext.DrawEllipse(StyleFlameBrush, null, satellite, radius * 0.18, radius * 0.18);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdateAnimationSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        UpdateAnimationSubscription();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateAnimationSubscription();

    private void OnRendering(object? sender, EventArgs e)
    {
        var elapsed = _clock.Elapsed;
        if (_soulState == SoulState.Error && elapsed.TotalSeconds >= 0.18)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isSubscribed = false;
            InvalidateVisual();
            return;
        }

        if (elapsed - _lastRenderTime >= FrameInterval30Fps)
        {
            _lastRenderTime = elapsed;
            InvalidateVisual();
        }
    }

    private void UpdateAnimationSubscription()
    {
        bool shouldAnimate = _isLoaded && IsVisible && _soulState != SoulState.Idle;
        if (shouldAnimate == _isSubscribed)
        {
            return;
        }

        _isSubscribed = shouldAnimate;
        if (shouldAnimate)
        {
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
            InvalidateVisual();
        }
    }

    private static Brush CreateMultiStopBrush(params (Color Color, double Offset)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.40, 0.36),
            GradientOrigin = new Point(0.34, 0.30),
            RadiusX = 0.82,
            RadiusY = 0.82,
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Brush CreateRadialBrush(Color center, Color middle, Color edge)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.38, 0.34),
            GradientOrigin = new Point(0.32, 0.28),
            RadiusX = 0.76,
            RadiusY = 0.76,
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        brush.GradientStops.Add(new GradientStop(center, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.42));
        brush.GradientStops.Add(new GradientStop(edge, 1));
        brush.Freeze();
        return brush;
    }

    private Brush StyleCoreBrush => _visualStyle switch
    {
        SoulVisualStyle.CyberOrb => CyberCoreBrush,
        SoulVisualStyle.EmeraldSpirit => EmeraldCoreBrush,
        SoulVisualStyle.SolarEmber => EmberCoreBrush,
        SoulVisualStyle.VoidRipple => VoidCoreBrush,
        _ => SoulCyanBrush
    };

    private Brush StyleFlameBrush => _visualStyle switch
    {
        SoulVisualStyle.CyberOrb => CyberFlameBrush,
        SoulVisualStyle.EmeraldSpirit => EmeraldFlameBrush,
        SoulVisualStyle.SolarEmber => EmberFlameBrush,
        SoulVisualStyle.VoidRipple => VoidFlameBrush,
        _ => FlameBrush
    };

    private Brush StyleHaloBrush => _visualStyle switch
    {
        SoulVisualStyle.CyberOrb => CyberFlameBrush,
        SoulVisualStyle.EmeraldSpirit => EmeraldFlameBrush,
        SoulVisualStyle.SolarEmber => EmberFlameBrush,
        SoulVisualStyle.VoidRipple => VoidFlameBrush,
        _ => HaloBrush
    };
}
