using System.Globalization;
using Companion.Core;

namespace Companion.Services;

public sealed class AutonomousScheduler : IAsyncDisposable
{
    private readonly DatabaseService _database;
    private readonly Func<ScheduledAction, CancellationToken, Task> _execute;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runner;

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
        if (_runner is not null)
        {
            throw new InvalidOperationException("The autonomous scheduler is already running.");
        }

        _runner = Task.Run(() => RunAsync(_shutdown.Token));
    }

    public void Wake() => _wakeEvent.Set();

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

    public async ValueTask DisposeAsync()
    {
        _database.ScheduledActionsChanged -= Wake;
        await _shutdown.CancelAsync();
        _wakeEvent.Set();
        if (_runner is not null)
        {
            await _runner;
        }

        _shutdown.Dispose();
        _wakeEvent.Dispose();
    }
}
