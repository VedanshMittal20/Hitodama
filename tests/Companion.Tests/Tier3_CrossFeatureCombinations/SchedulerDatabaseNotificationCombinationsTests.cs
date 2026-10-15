using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Services;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier3_CrossFeatureCombinations;

public sealed class SchedulerDatabaseNotificationCombinationsTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public SchedulerDatabaseNotificationCombinationsTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("comb_sched_db.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task Scheduler_ExecutesAction_UpdatesDailyStats_AndReschedulesDailyRoutine()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ScheduledAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                // Action execution modifies SQLite stats
                _database.IncrementStat("tools_executed", 1);
                return Task.CompletedTask;
            });
        scheduler.ActionCompleted += action => tcs.TrySetResult(action);

        // Add daily routine action due in past
        long actionId = _database.AddScheduledAction(
            "Sync Cloud Calendars",
            "Calendar.Sync",
            "{}",
            DateTime.UtcNow.AddSeconds(-2),
            cronExpression: "daily:08:30");

        // Act
        scheduler.Start();
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert
        executed.Should().NotBeNull();
        executed.Id.Should().Be(actionId);

        // Daily stats must be updated
        _database.GetTodayStats().ToolsExecuted.Should().Be(1);

        // Action must be rescheduled into the future
        var next = _database.GetNextScheduledAction();
        next.Should().NotBeNull();
        next!.Id.Should().Be(actionId);
        next.TriggerUtc.Should().BeAfter(DateTime.UtcNow);
        next.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduler_ToolFailure_UpdatesActionState_AndLogsFailureWithoutCrashingScheduler()
    {
        // Arrange
        var tcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) => throw new HttpRequestException("Cloud network unavailable"));

        scheduler.ActionFailed += (action, ex) => tcs.TrySetResult(ex);

        _database.AddScheduledAction(
            "Sync Weather",
            "Weather.Fetch",
            "{}",
            DateTime.UtcNow.AddSeconds(-1));

        // Act
        scheduler.Start();
        var failure = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert
        failure.Should().NotBeNull();
        failure.Should().BeOfType<HttpRequestException>();

        // Action is now disabled in DB
        _database.GetDueActions().Should().BeEmpty();
    }
}
