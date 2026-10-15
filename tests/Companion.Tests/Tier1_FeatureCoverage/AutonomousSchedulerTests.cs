using System;
using System.Threading;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Services;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class AutonomousSchedulerTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public AutonomousSchedulerTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("scheduler_test.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task Scheduler_ExecutesDueActionImmediately_AndFiresActionCompleted()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ScheduledAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                tcs.TrySetResult(action);
                return Task.CompletedTask;
            });

        // Add action that is already due (past UTC)
        _database.AddScheduledAction("Immediate action", "System.Test", "{}", DateTime.UtcNow.AddSeconds(-2));

        // Act
        scheduler.Start();

        // Assert
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        executed.Should().NotBeNull();
        executed.Description.Should().Be("Immediate action");

        // Action must now be disabled in database
        _database.GetDueActions().Should().BeEmpty();
    }

    [Fact]
    public async Task Scheduler_MultipleStarts_ThrowsInvalidOperationException()
    {
        // Arrange
        await using var scheduler = new AutonomousScheduler(_database, (a, ct) => Task.CompletedTask);
        scheduler.Start();

        // Act & Assert
        Action act = () => scheduler.Start();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already running*");
    }

    [Fact]
    public async Task Scheduler_FailingAction_FiresActionFailedEvent()
    {
        // Arrange
        var tcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) => throw new InvalidOperationException("Tool execution failed"));

        scheduler.ActionFailed += (action, ex) => tcs.TrySetResult(ex);

        _database.AddScheduledAction("Failing action", "System.BrokenTool", "{}", DateTime.UtcNow.AddSeconds(-1));

        // Act
        scheduler.Start();

        // Assert
        var failure = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        failure.Should().NotBeNull();
        failure.Should().BeOfType<InvalidOperationException>();
        failure.Message.Should().Be("Tool execution failed");
    }

    [Fact]
    public async Task Scheduler_DailyRoutine_ReschedulesToNextDay()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ScheduledAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                tcs.TrySetResult(action);
                return Task.CompletedTask;
            });

        // Add due action with daily cron expression: daily:09:00
        long id = _database.AddScheduledAction(
            "Daily Morning Routine",
            "Routine.Run",
            "{}",
            DateTime.UtcNow.AddSeconds(-2),
            cronExpression: "daily:09:00");

        // Act
        scheduler.Start();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Wait brief tick for reschedule to persist
        await Task.Delay(100);

        // Assert
        var next = _database.GetNextScheduledAction();
        next.Should().NotBeNull();
        next!.Id.Should().Be(id);
        next.TriggerUtc.Should().BeAfter(DateTime.UtcNow, "daily recurring action must be rescheduled into future");
        next.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduler_Wake_PicksUpNewActionDynamicallyWithoutPolling()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ScheduledAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                tcs.TrySetResult(action);
                return Task.CompletedTask;
            });

        // Start scheduler with NO scheduled tasks
        scheduler.Start();
        await Task.Delay(50);

        // Act: Add a new due action while scheduler is sleeping
        _database.AddScheduledAction("Dynamically added action", "System.Alert", "{}", DateTime.UtcNow.AddSeconds(-1));

        // Assert: AutoResetEvent wakes up scheduler via ScheduledActionsChanged
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        executed.Description.Should().Be("Dynamically added action");
    }
}
