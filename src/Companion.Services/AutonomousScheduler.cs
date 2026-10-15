using System.Globalization;
using Companion.Core;

namespace Companion.Services;

public sealed class AutonomousScheduler : IAsyncDisposable, IDisposable
{
    private readonly DatabaseService _database;
    private readonly Func<ScheduledAction, CancellationToken, Task> _execute;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private Task? _runner;
    private Task? _disposeTask;
    private int _disposeStarted;

    public event Action<ScheduledAction>? ActionCompleted;
    public event Action<ScheduledAction, Exception>? ActionFailed;
    public event Action<Exception>? SchedulerFailed;

    public AutonomousScheduler(
        DatabaseService database,
        Func<ScheduledAction, CancellationToken, Task> execute)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _database.ScheduledActionsChanged += Wake;
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_runner is not null)
            {
                throw new InvalidOperationException("The autonomous scheduler is already running.");
            }

            _runner = Task.Run(() => RunAsync(_shutdown.Token));
        }
    }

    public void Wake()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        try
        {
            _wakeEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // Another DisposeAsync caller already closed the wake event.
            // A database change may race with final shutdown.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<ScheduledAction> dueActions = _database.GetDueActions();
                if (dueActions.Count > 0)
                {
                    foreach (ScheduledAction action in dueActions)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            await _execute(action, cancellationToken);
                            DateTime? nextTrigger = GetNextDailyTrigger(action.CronExpression);
                            _database.MarkExecutedOrReschedule(action.Id, nextTrigger);
                            ActionCompleted?.Invoke(action);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            _database.MarkExecutedOrReschedule(action.Id);
                            ActionFailed?.Invoke(action, exception);
                        }
                    }

                    continue;
                }

                ScheduledAction? nextAction = _database.GetNextScheduledAction();
                if (nextAction is null)
                {
                    _wakeEvent.WaitOne(Timeout.Infinite);
                    continue;
                }

                TimeSpan delay = nextAction.TriggerUtc - DateTime.UtcNow;
                int waitMilliseconds = delay <= TimeSpan.Zero
                    ? 100
                    : (int)Math.Clamp(delay.TotalMilliseconds, 100, TimeSpan.FromHours(1).TotalMilliseconds);
                if (_wakeEvent.WaitOne(waitMilliseconds))
                {
                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SchedulerFailed?.Invoke(exception);
        }
    }

    private static DateTime? GetNextDailyTrigger(string? expression)
    {
        if (expression is null ||
            !expression.StartsWith("daily:", StringComparison.OrdinalIgnoreCase) ||
            !TimeOnly.TryParseExact(
                expression.AsSpan("daily:".Length),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly time))
        {
            return null;
        }

        DateTime now = DateTime.Now;
        DateTime nextLocal = now.Date.Add(time.ToTimeSpan());
        if (nextLocal <= now)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        return nextLocal.ToUniversalTime();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        _database.ScheduledActionsChanged -= Wake;

        Task cancellation = _shutdown.CancelAsync();
        try
        {
            _wakeEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent teardown already closed the wake event.
        }

        try
        {
            await cancellation.ConfigureAwait(false);
            Task? runner = _runner;
            if (runner is not null)
            {
                try
                {
                    await runner.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Disposal stays bounded if an action callback ignores cancellation.
                }
                catch (OperationCanceledException)
                {
                    // The scheduler runner is expected to stop through cancellation.
                }
            }
        }
        finally
        {
            _shutdown.Dispose();
            _wakeEvent.Dispose();
        }
    }
}

