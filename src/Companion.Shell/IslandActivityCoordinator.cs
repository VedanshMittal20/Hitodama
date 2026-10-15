using Timer = System.Threading.Timer;

namespace Companion.Shell;

public enum IslandActivityKind
{
    IdleClock,
    Media,
    TimerRunning,
    Clipboard,
    ScheduledAction,
    TimerComplete,
    Notification,
    Dictation,
    CircleToAsk,
    Voice,
    Error,
    Gatekeeper
}

public sealed record IslandActivity(
    string Id,
    IslandActivityKind Kind,
    string Title,
    string Detail,
    int Priority,
    DateTimeOffset? ExpiresAtUtc,
    long Sequence);

/// <summary>
/// Chooses which activity may occupy the island's compact surface. Expiring items
/// use one one-shot timer, while persistent activities remain until explicitly cleared.
/// </summary>
public sealed class IslandActivityCoordinator : IDisposable
{
    private static readonly IReadOnlyDictionary<IslandActivityKind, int> Priorities =
        new Dictionary<IslandActivityKind, int>
        {
            [IslandActivityKind.IdleClock] = 0,
            [IslandActivityKind.Media] = 20,
            [IslandActivityKind.TimerRunning] = 30,
            [IslandActivityKind.Clipboard] = 40,
            [IslandActivityKind.ScheduledAction] = 60,
            [IslandActivityKind.TimerComplete] = 70,
            [IslandActivityKind.Notification] = 60,
            [IslandActivityKind.Dictation] = 80,
            [IslandActivityKind.CircleToAsk] = 80,
            [IslandActivityKind.Voice] = 80,
            [IslandActivityKind.Error] = 90,
            [IslandActivityKind.Gatekeeper] = 100
        };

    private readonly object _sync = new();
    private readonly Dictionary<string, IslandActivity> _activities = new(StringComparer.Ordinal);
    private readonly Timer _expiryTimer;
    private long _sequence;
    private bool _disposed;

    public event Action<IslandActivity?>? CurrentActivityChanged;

    public IslandActivity? Current
    {
        get
        {
            lock (_sync)
            {
                return SelectCurrent();
            }
        }
    }

    public IslandActivityCoordinator()
    {
        _expiryTimer = new Timer(OnExpiryTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public IslandActivity Publish(
        string id,
        IslandActivityKind kind,
        string title,
        string detail,
        TimeSpan? lifetime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detail);
        if (lifetime.HasValue && lifetime.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "An activity lifetime must be positive.");
        }

        IslandActivity? previous;
        IslandActivity? current;
        IslandActivity activity;
        lock (_sync)
        {
            previous = SelectCurrent();
            RemoveExpired(DateTimeOffset.UtcNow);
            activity = new IslandActivity(
                id,
                kind,
                title,
                detail,
                Priorities[kind],
                lifetime is null ? null : DateTimeOffset.UtcNow.Add(lifetime.Value),
                ++_sequence);
            _activities[id] = activity;
            ScheduleNextExpiry();
            current = SelectCurrent();
        }

        RaiseIfChanged(previous, current);
        return activity;
    }

    public bool Clear(string id)
    {
        IslandActivity? previous;
        IslandActivity? current;
        bool removed;
        lock (_sync)
        {
            previous = SelectCurrent();
            RemoveExpired(DateTimeOffset.UtcNow);
            removed = _activities.Remove(id);
            if (!removed)
            {
                return false;
            }

            ScheduleNextExpiry();
            current = SelectCurrent();
        }

        RaiseIfChanged(previous, current);
        return true;
    }

    private void OnExpiryTimer(object? state)
    {
        IslandActivity? previous;
        IslandActivity? current;
        lock (_sync)
        {
            previous = SelectCurrent();
            RemoveExpired(DateTimeOffset.UtcNow);
            ScheduleNextExpiry();
            current = SelectCurrent();
        }

        RaiseIfChanged(previous, current);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string id in _activities.Values
                     .Where(activity => activity.ExpiresAtUtc is not null && activity.ExpiresAtUtc <= now)
                     .Select(activity => activity.Id)
                     .ToArray())
        {
            _activities.Remove(id);
        }
    }

    private IslandActivity? SelectCurrent() => _activities.Values
        .OrderByDescending(activity => activity.Priority)
        .ThenByDescending(activity => activity.Sequence)
        .FirstOrDefault();

    private void ScheduleNextExpiry()
    {
        DateTimeOffset? next = _activities.Values
            .Where(activity => activity.ExpiresAtUtc is not null)
            .Select(activity => activity.ExpiresAtUtc)
            .Min();
        if (next is null)
        {
            _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        TimeSpan due = next.Value - DateTimeOffset.UtcNow;
        if (due < TimeSpan.FromMilliseconds(1))
        {
            due = TimeSpan.FromMilliseconds(1);
        }

        _expiryTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void RaiseIfChanged(IslandActivity? previous, IslandActivity? current)
    {
        if (previous != current)
        {
            CurrentActivityChanged?.Invoke(current);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _expiryTimer.Dispose();
        lock (_sync)
        {
            _activities.Clear();
        }
    }
}
