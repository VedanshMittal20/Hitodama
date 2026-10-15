using System;
using System.Threading;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Services;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class SchedulerBoundaryTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public SchedulerBoundaryTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("scheduler_boundary.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Theory]
    [InlineData("daily:")]
    [InlineData("daily:invalid")]
    [InlineData("daily:25:99")]
    [InlineData("daily:-1:00")]
    [InlineData("every 5 minutes")]
    [InlineData("* * * * *")]
    [InlineData("")]
    public async Task Scheduler_InvalidCronExpressions_DoNotCrashScheduler(string invalidCron)
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

        _database.AddScheduledAction(
            "Task with bad cron",
            "System.Test",
            "{}",
            DateTime.UtcNow.AddSeconds(-1),
            cronExpression: invalidCron);

        // Act
        scheduler.Start();

        // Assert: executed once, invalid cron fails to reschedule (disables task) without crashing runner
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        executed.Should().NotBeNull();

        await Task.Delay(50);
        _database.GetDueActions().Should().BeEmpty();
    }

    [Fact]
    public async Task Scheduler_FarPastTimestamp_ExecutesImmediatelyOnStartup()
    {
        // Arrange: Timestamp in year 2020
        var tcs = new TaskCompletionSource<ScheduledAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                tcs.TrySetResult(action);
                return Task.CompletedTask;
            });

        _database.AddScheduledAction(
            "Old task",
            "System.Test",
            "{}",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act
        scheduler.Start();

        // Assert
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        executed.Description.Should().Be("Old task");
    }

    [Fact]
    public async Task Scheduler_FarFutureTimestamp_DoesNotTriggerPrematurely()
    {
        // Arrange: Timestamp in year 2099
        bool executed = false;
        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                executed = true;
                return Task.CompletedTask;
            });

        _database.AddScheduledAction(
            "Future task",
            "System.Test",
            "{}",
            DateTime.UtcNow.AddYears(10));

        // Act
        scheduler.Start();
        await Task.Delay(200);

        // Assert
        executed.Should().BeFalse("actions scheduled years in the future must remain pending");
    }

    [Fact]
    public void Scheduler_ConcurrentWakeCalls_DoNotThrowOrDeadlock()
    {
        // Arrange
        var scheduler = new AutonomousScheduler(_database, (a, ct) => Task.CompletedTask);

        // Act & Assert
        Action act = () => Parallel.For(0, 50, _ => scheduler.Wake());
        act.Should().NotThrow();
    }
}
