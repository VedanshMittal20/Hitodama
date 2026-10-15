using System.Diagnostics;
using Companion.Core;
using Microsoft.Win32;

namespace Companion.Services;

/// <summary>
/// Owns one persistent countdown. A Stopwatch drives the live remaining time, while
/// the stored UTC deadline restores the countdown after app or device restarts.
/// </summary>
public sealed class CountdownTimerService : IDisposable
{
    private readonly object _sync = new();
    private readonly IDatabaseService _database;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _completionCancellation;
    private DateTimeOffset? _endUtc;
    private TimeSpan _remainingAtStart;
    private bool _disposed;

    public event Action? Completed;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _endUtc is not null;
            }
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            lock (_sync)
            {
                if (_endUtc is null)
                {
                    return TimeSpan.Zero;
                }

                TimeSpan remaining = _remainingAtStart - _clock.Elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public DateTimeOffset? EndUtc
    {
        get
        {
            lock (_sync)
            {
                return _endUtc;
            }
        }
    }

    public CountdownTimerService(IDatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <returns>True when a stored timer had already expired and was completed during restore.</returns>
    public bool Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset? endUtc = _database.GetCountdownTimerEndUtc();
        if (endUtc is null)
        {
            return false;
        }

        TimeSpan remaining = endUtc.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _database.ClearCountdownTimer();
            Completed?.Invoke();
            return true;
        }

        StartClock(endUtc.Value, remaining);
        return false;
    }

    public void Start(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Timer duration must be between one second and 24 hours.");
        }

        DateTimeOffset endUtc = DateTimeOffset.UtcNow.Add(duration);
        _database.SetCountdownTimerEndUtc(endUtc);
        StartClock(endUtc, duration);
    }

    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopClock(clearEnd: true);
        _database.ClearCountdownTimer();
    }

    private void StartClock(DateTimeOffset endUtc, TimeSpan remaining)
    {
        StopClock(clearEnd: false);
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _endUtc = endUtc;
            _remainingAtStart = remaining;
            _completionCancellation = cancellation;
            _clock.Restart();
        }

        _ = CompleteAfterAsync(endUtc, remaining, cancellation);
    }

    private async Task CompleteAfterAsync(
        DateTimeOffset endUtc,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_completionCancellation, cancellation) || _endUtc != endUtc)
            {
                return;
            }

            _completionCancellation = null;
            _endUtc = null;
            _remainingAtStart = TimeSpan.Zero;
            _clock.Stop();
        }

        try
        {
            _database.ClearCountdownTimer();
        }
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("timer", "complete-persistence", exception);
        }

        cancellation.Dispose();
        SafeDiagnostics.WriteEvent("timer", "completed");
        Completed?.Invoke();
    }

    private void StopClock(bool clearEnd)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _completionCancellation;
            _completionCancellation = null;
            if (clearEnd)
            {
                _endUtc = null;
            }

            _remainingAtStart = TimeSpan.Zero;
            _clock.Reset();
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        DateTimeOffset? endUtc = EndUtc;
        if (endUtc is null)
        {
            return;
        }

        TimeSpan remaining = endUtc.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            CancellationTokenSource? cancellation;
            lock (_sync)
            {
                cancellation = _completionCancellation;
            }

            if (cancellation is not null)
            {
                _ = CompleteAfterAsync(endUtc.Value, TimeSpan.Zero, cancellation);
            }
        }
        else
        {
            StartClock(endUtc.Value, remaining);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopClock(clearEnd: false);
    }
}
