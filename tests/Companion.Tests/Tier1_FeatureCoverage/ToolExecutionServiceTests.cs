using System.IO;
using System.Text.Json;
using Companion.Core;
using Companion.Services;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;
using File = System.IO.File;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class ToolExecutionServiceTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public ToolExecutionServiceTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("tool_execution.db"));
    }

    [Fact]
    public void ToolRegistry_OnlyAdvertisesToolsForEnabledAbilityPacks()
    {
        using var service = CreateService();

        string[] definitions = service.Registry
            .GetDefinitionsForAbilityPacks(["Planner"])
            .Select(definition => definition.Name)
            .ToArray();

        definitions.Should().Contain(["manage_todo", "schedule_action", "remember_fact"]);
        definitions.Should().NotContain(["open_app", "open_url", "media_control", "system_toggle"]);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesTodoAndMemory_AndWritesAuditEntries()
    {
        using var service = CreateService();
        using JsonDocument todoArgs = JsonDocument.Parse("""{"action":"add","title":"Review pull request","due_time":"10:30"}""");
        using JsonDocument memoryArgs = JsonDocument.Parse("""{"fact":"Prefers concise answers","tags":"preference"}""");

        await service.ExecuteAsync("manage_todo", todoArgs.RootElement, "message-1");
        await service.ExecuteAsync("remember_fact", memoryArgs.RootElement, "message-2");

        _database.GetTodos().Should().ContainSingle(todo =>
            todo.Title == "Review pull request" && todo.DueTimeText == "10:30");
        _database.GetAllMemories().Should().ContainSingle(memory =>
            memory.Fact == "Prefers concise answers" && memory.Tags == "preference");
        File.ReadAllLines(_scope.CreateFilePath("audit.log")).Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedToolDoesNotRunAndIsAudited()
    {
        bool launched = false;
        using var service = CreateService(
            (tool, _, _) => Task.FromResult(tool.Name != "open_app"),
            _ => launched = true);
        service.TrackUntrustedContext(true);
        using JsonDocument arguments = JsonDocument.Parse("""{"app_name":"Notepad"}""");

        Func<Task> execute = () => service.ExecuteAsync("open_app", arguments.RootElement, "untrusted-message");

        await execute.Should().ThrowAsync<UnauthorizedAccessException>();
        launched.Should().BeFalse();
        File.ReadAllText(_scope.CreateFilePath("audit.log")).Should().Contain("denied");
    }

    [Fact]
    public async Task ExecuteAsync_AssessesSystemControlRiskFromActionBeforeApproval()
    {
        ToolRiskLevel? requestedRisk = null;
        using var service = CreateService((tool, _, _) =>
        {
            requestedRisk = tool.RiskLevel;
            return Task.FromResult(false);
        });
        using JsonDocument arguments = JsonDocument.Parse("""{"action":"restart"}""");

        Func<Task> execute = () => service.ExecuteAsync("system_control", arguments.RootElement, "restart-message");

        await execute.Should().ThrowAsync<UnauthorizedAccessException>();
        requestedRisk.Should().Be(ToolRiskLevel.High);
    }

    [Fact]
    public async Task CreateRoutine_PersistsStepsAndSchedulesDailyRun()
    {
        using var service = CreateService();
        using JsonDocument arguments = JsonDocument.Parse(
            """{"name":"Morning apps","steps_json":"[{\"tool_name\":\"open_app\",\"args\":{\"app_name\":\"Notepad\"}}]","daily_time_hhmm":"09:00"}""");

        await service.ExecuteAsync("create_routine", arguments.RootElement, "message-routine");

        Routine routine = _database.GetRoutines().Should().ContainSingle().Subject;
        routine.Name.Should().Be("Morning apps");
        routine.StepsJson.Should().Contain("Notepad");
        ScheduledAction scheduled = _database.GetNextScheduledAction()!;
        scheduled.ToolName.Should().Be("Routine.Run");
        scheduled.CronExpression.Should().Be("daily:09:00");
    }

    public void Dispose() => _scope.Dispose();

    private ToolExecutionService CreateService(
        Func<ToolDefinition, JsonElement, ToolExecutionContext, Task<bool>>? approve = null,
        Action<string>? launchApp = null)
    {
        string auditPath = _scope.CreateFilePath("audit.log");
        var gatekeeper = new GatekeeperSecurityEngine(
            approve ?? ((_, _, _) => Task.FromResult(true)),
            _database);
        return new ToolExecutionService(
            _database,
            new SystemControlService(),
            new MediaService(),
            launchApp ?? (_ => { }),
            gatekeeper,
            auditPath);
    }
}
