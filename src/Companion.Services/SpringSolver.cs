using System.Diagnostics;
using System.Windows.Media;

namespace Companion.Services;

public sealed class SpringValue
{
    public double Current { get; set; }
    public double Target { get; set; }
    public double Velocity { get; set; }
    public double Omega { get; set; } = 26.0;
    public double Zeta { get; set; } = 0.76;

    public SpringValue(double initial, double omega = 26.0, double zeta = 0.76)
    {
        Current = Target = initial;
        Omega = omega;
        Zeta = zeta;
    }

    public bool Step(double dt)
    {
        double displacement = Current - Target;
        if (Math.Abs(displacement) < 0.05 && Math.Abs(Velocity) < 0.05)
        {
            Current = Target;
            Velocity = 0;
            return false;
        }

        int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double subDt = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double disp = Current - Target;
            double acceleration = -Omega * Omega * disp - 2.0 * Zeta * Omega * Velocity;
            Velocity += acceleration * subDt;
            Current += Velocity * subDt;
        }

        return true;
    }
}

public sealed class IslandAnimator
{
    private readonly List<SpringValue> _springs = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTime;
    private bool _isRunning;

    public event Action? FrameTick;

    public SpringValue CreateSpring(double initial, double omega = 26.0, double zeta = 0.76)
    {
        var spring = new SpringValue(initial, omega, zeta);
        _springs.Add(spring);
        return spring;
    }

    public void Wake()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _clock.Restart();
        _lastTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double dt = Math.Clamp((now - _lastTime).TotalSeconds, 0.001, 0.050);
        _lastTime = now;

        bool anyActive = false;
        foreach (var spring in _springs)
        {
            if (spring.Step(dt))
            {
                anyActive = true;
            }
        }

        FrameTick?.Invoke();

        if (!anyActive)
        {
            _isRunning = false;
            _clock.Stop();
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _clock.Stop();
        CompositionTarget.Rendering -= OnRendering;
    }
}
