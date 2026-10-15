using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Companion.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

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

    private readonly Stopwatch _clock = new();
    private SoulState _soulState;
    private SoulVisualStyle _visualStyle = SoulVisualStyle.HitodamaFlame;
    private double _audioLevel;
    private bool _isLoaded;
    private bool _isSubscribed;

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
        double radius = size * 0.38;

        if (_soulState == SoulState.Idle)
        {
            // Rich multi-stop ethereal spirit flame (cyan/violet/emerald with ambient halo & specular shine)
            drawingContext.DrawEllipse(StyleHaloBrush, null, center, radius * 1.44, radius * 1.44);
            drawingContext.DrawEllipse(StyleFlameBrush, null, center, radius * 1.12, radius * 1.12);
            drawingContext.DrawEllipse(StyleCoreBrush, null, center, radius * 0.86, radius * 0.86);
            drawingContext.DrawEllipse(InnerBrush, null, center, radius * 0.60, radius * 0.60);
            drawingContext.DrawEllipse(SpecularBrush, null,
                new Point(center.X - radius * 0.22, center.Y - radius * 0.28), radius * 0.24, radius * 0.15);
            return;
        }

        double time = _clock.Elapsed.TotalSeconds;
        double activity = _soulState is SoulState.Listening or SoulState.Speaking
            ? Math.Max(0.16, _audioLevel)
            : 0.56;
        if (_soulState == SoulState.Error)
        {
            activity = 0.28;
        }

        double pulse = 1 + Math.Sin(time * 3.1) * (0.045 + 0.075 * activity);
        double sway = Math.Sin(time * 1.7) * radius * (0.06 + 0.12 * activity);

        DrawBlob(drawingContext, center, radius * 1.3 * pulse, time, 0.8, activity, StyleHaloBrush, 11);
        DrawBlob(drawingContext, new Point(center.X + sway * 0.55, center.Y - sway * 0.42),
            radius * pulse, time, 1.25, activity, StyleFlameBrush, 13);
        DrawBlob(drawingContext, new Point(center.X - sway * 0.48, center.Y + sway * 0.28),
            radius * 0.69 * pulse, time, 1.8, activity, StyleCoreBrush, 10);

        drawingContext.DrawEllipse(SpecularBrush, null,
            new Point(center.X - radius * 0.18 + sway * 0.18, center.Y - radius * 0.22),
            radius * 0.18, radius * 0.11);
    }

    private void DrawBlob(DrawingContext drawingContext, Point center, double radius,
        double time, double frequency, double activity, Brush brush, int pointCount)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            var points = new Point[pointCount];
            for (int index = 0; index < pointCount; index++)
            {
                double angle = 2 * Math.PI * index / pointCount;
                double waveA = Math.Sin(time * frequency + angle * 2.0 + index * 0.27);
                double waveB = Math.Sin(time * (frequency * 0.63) - angle * 3.0 + index * 0.41);
                double deformation = 1 + activity * (waveA * 0.12 + waveB * 0.055);
                points[index] = new Point(
                    center.X + Math.Cos(angle) * radius * deformation,
                    center.Y + Math.Sin(angle) * radius * deformation);
            }

            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            for (int index = 0; index < pointCount; index++)
            {
                Point current = points[index];
                Point next = points[(index + 1) % pointCount];
                Point midpoint = new((current.X + next.X) / 2, (current.Y + next.Y) / 2);
                context.QuadraticBezierTo(current, midpoint, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(brush, null, geometry);
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

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    private void UpdateAnimationSubscription()
    {
        bool shouldAnimate = _isLoaded && _soulState != SoulState.Idle;
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
        SoulVisualStyle.SolarEmber => EmberCoreBrush,
        SoulVisualStyle.VoidRipple => VoidCoreBrush,
        _ => SoulCyanBrush
    };

    private Brush StyleFlameBrush => _visualStyle switch
    {
        SoulVisualStyle.CyberOrb => CyberFlameBrush,
        SoulVisualStyle.SolarEmber => EmberFlameBrush,
        SoulVisualStyle.VoidRipple => VoidFlameBrush,
        _ => FlameBrush
    };

    private Brush StyleHaloBrush => _visualStyle switch
    {
        SoulVisualStyle.CyberOrb => CyberFlameBrush,
        SoulVisualStyle.SolarEmber => EmberFlameBrush,
        SoulVisualStyle.VoidRipple => VoidFlameBrush,
        _ => HaloBrush
    };
}
