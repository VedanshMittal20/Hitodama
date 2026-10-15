using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier3_CrossFeatureCombinations;

public sealed class IpcGatekeeperToolCombinationsTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public IpcGatekeeperToolCombinationsTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("ipc_gatekeeper_tool.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task Ipc_ToolExecution_LowRiskInTrustedContext_AutoApprovedAndAudited()
    {
        // Arrange
        var gatekeeper = new GatekeeperSecurityEngine();
        var tool = new ToolDefinition("System.GetStatus", "Reads system stats", ToolRiskLevel.Low);
        var context = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "ipc-msg-1");
        using var args = JsonDocument.Parse("{}");

        // Act 1: Gatekeeper evaluation
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, context);

        // Act 2: Tool execution and auditing in database
        if (approved)
        {
            _database.IncrementStat("tools_executed", 1);
        }

        // Assert
        approved.Should().BeTrue();
        _database.GetTodayStats().ToolsExecuted.Should().Be(1);
    }

    [Fact]
    public async Task Ipc_ToolExecution_HighRisk_RequiresConfirmationAndExecutesOnApproval()
    {
        // Arrange: User confirmation prompt returns true
        bool dialogShown = false;
        var gatekeeper = new GatekeeperSecurityEngine((t, a, ctx) =>
        {
            dialogShown = true;
            return Task.FromResult(true); // User clicked Confirm
        });

        var highRiskTool = new ToolDefinition("Power.Restart", "Restarts the system", ToolRiskLevel.High);
        var context = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "ipc-msg-2");
        using var args = JsonDocument.Parse("{}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(highRiskTool, args.RootElement, context);
        if (approved)
        {
            _database.IncrementStat("tools_executed", 1);
        }

        // Assert
        dialogShown.Should().BeTrue();
        approved.Should().BeTrue();
        _database.GetTodayStats().ToolsExecuted.Should().Be(1);
    }

    [Fact]
    public async Task Ipc_ToolExecution_UntrustedContext_BlocksToolWhenUserRejects()
    {
        // Arrange: Untrusted context (e.g. from web search or circle to ask prompt)
        bool dialogShown = false;
        var gatekeeper = new GatekeeperSecurityEngine((t, a, ctx) =>
        {
            dialogShown = true;
            return Task.FromResult(false); // User rejected prompt
        });

        var lowRiskTool = new ToolDefinition("System.SetVolume", "Adjusts volume", ToolRiskLevel.Low);
        var untrustedContext = new ToolExecutionContext(IsUntrustedInput: true, SourceMessageId: "web-untrusted-1");
        using var args = JsonDocument.Parse("{\"level\": 100}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(lowRiskTool, args.RootElement, untrustedContext);
        if (approved)
        {
            _database.IncrementStat("tools_executed", 1);
        }

        // Assert
        dialogShown.Should().BeTrue("untrusted context must trigger confirmation dialog even for low-risk tool");
        approved.Should().BeFalse("user rejected tool execution");
        _database.GetTodayStats().ToolsExecuted.Should().Be(0, "rejected tool must not increment tools_executed");
    }
}
