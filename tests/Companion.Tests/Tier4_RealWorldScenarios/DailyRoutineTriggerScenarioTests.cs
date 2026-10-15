using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Services;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier4_RealWorldScenarios;

public sealed class DailyRoutineTriggerScenarioTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public DailyRoutineTriggerScenarioTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("daily_routine_scenario.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task DailyRoutine_Workflow_ExecutesSteps_UpdatesStats_AndSchedulesNextDayRun()
    {
        // 1. Seed user's morning tasks into SQLite
        _database.AddTodo("Review PR #123", "09:30");
        _database.AddTodo("Plan sprint backlog", "11:00");
        _database.GetTodos().Should().HaveCount(2);

        // 2. Set up AutonomousScheduler with a routine executor
        int stepsExecuted = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var scheduler = new AutonomousScheduler(
            _database,
            (action, ct) =>
            {
                if (action.ToolName == "Routine.Run")
                {
                    // Step 1: Read active todos
                    var todos = _database.GetTodos();
                    stepsExecuted += todos.Count;

                    // Step 2: Record executed tool in daily stats
                    _database.IncrementStat("tools_executed", 1);
                }
                return Task.CompletedTask;
            });

        scheduler.ActionCompleted += action =>
        {
            if (action.ToolName == "Routine.Run")
            {
                tcs.TrySetResult(true);
            }
        };

        // 3. User registers a recurring morning routine action
        long routineActionId = _database.AddScheduledAction(
            "Morning Briefing",
            "Routine.Run",
            "{\"routineId\": 1}",
            DateTime.UtcNow.AddSeconds(-1),
            cronExpression: "daily:09:00");

        // 4. Act: Scheduler starts and executes routine
        scheduler.Start();
        bool completed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // 5. Assert: Both todos inspected and stats recorded
        completed.Should().BeTrue();
        stepsExecuted.Should().Be(2);
        _database.GetTodayStats().ToolsExecuted.Should().Be(1);

        // 6. Routine action is rescheduled to next day at 09:00 local/UTC
        var nextAction = _database.GetNextScheduledAction();
        nextAction.Should().NotBeNull();
        nextAction!.Id.Should().Be(routineActionId);
        nextAction.TriggerUtc.Should().BeAfter(DateTime.UtcNow);
        nextAction.IsEnabled.Should().BeTrue();
    }
}
