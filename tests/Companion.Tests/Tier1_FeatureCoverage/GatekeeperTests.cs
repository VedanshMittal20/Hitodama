using System;
using System.Text.Json;
using System.Threading.Tasks;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class GatekeeperTests
{
    private static readonly ToolDefinition LowRiskTool = new(
        "System.GetVolume",
        "Gets master audio volume",
        ToolRiskLevel.Low);

    private static readonly ToolDefinition MediumRiskTool = new(
        "System.SetVolume",
        "Changes system audio volume",
        ToolRiskLevel.Medium);

    private static readonly ToolDefinition HighRiskTool = new(
        "Power.Shutdown",
        "Initiates system shutdown",
        ToolRiskLevel.High);

    [Fact]
    public async Task LowRiskTool_InTrustedContext_AutoApprovedWithoutPrompt()
    {
        // Arrange: no prompt handler provided
        var gatekeeper = new GatekeeperSecurityEngine(approvalPromptHandler: null);
        var context = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "msg-1");
        using var args = JsonDocument.Parse("{}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(LowRiskTool, args.RootElement, context);

        // Assert
        approved.Should().BeTrue();
        gatekeeper.TotalApprovalRequests.Should().Be(1);
        gatekeeper.ApprovedCount.Should().Be(1);
    }

    [Fact]
    public async Task MediumRiskTool_InTrustedContext_PromptsForApproval()
    {
        // Arrange
        bool promptTriggered = false;
        var gatekeeper = new GatekeeperSecurityEngine(
            approvalPromptHandler: (tool, args, ctx) =>
            {
                promptTriggered = true;
                return Task.FromResult(true); // User clicked Allow
            });
        var context = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "msg-2");
        using var args = JsonDocument.Parse("{\"volume\": 80}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(MediumRiskTool, args.RootElement, context);

        // Assert
        promptTriggered.Should().BeTrue();
        approved.Should().BeTrue();
        gatekeeper.ApprovedCount.Should().Be(1);
    }

    [Fact]
    public async Task HighRiskTool_UserDeniesApproval_ReturnsFalse()
    {
        // Arrange
        var gatekeeper = new GatekeeperSecurityEngine(
            approvalPromptHandler: (tool, args, ctx) => Task.FromResult(false)); // User clicked Cancel
        var context = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "msg-3");
        using var args = JsonDocument.Parse("{}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(HighRiskTool, args.RootElement, context);

        // Assert
        approved.Should().BeFalse();
        gatekeeper.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task LowRiskTool_WhenContextIsUntrusted_EscalatesToRequireApproval()
    {
        // Arrange: Tool is Low risk, but source context came from untrusted input (e.g. web/circle-to-ask)
        bool promptTriggered = false;
        var gatekeeper = new GatekeeperSecurityEngine(
            approvalPromptHandler: (tool, args, ctx) =>
            {
                promptTriggered = true;
                return Task.FromResult(true);
            });
        var untrustedContext = new ToolExecutionContext(IsUntrustedInput: true, SourceMessageId: "external-untrusted");
        using var args = JsonDocument.Parse("{}");

        // Act
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(LowRiskTool, args.RootElement, untrustedContext);

        // Assert
        promptTriggered.Should().BeTrue("untrusted context must escalate low-risk tools to confirmation");
        approved.Should().BeTrue();
    }

    [Fact]
    public async Task TrackUntrustedContext_PersistentlyAffectsSubsequentRequestsUntilReset()
    {
        // Arrange
        bool promptTriggered = false;
        var gatekeeper = new GatekeeperSecurityEngine(
            approvalPromptHandler: (tool, args, ctx) =>
            {
                promptTriggered = true;
                return Task.FromResult(true);
            });
        using var args = JsonDocument.Parse("{}");

        // Act 1: Mark context untrusted globally
        gatekeeper.TrackUntrustedContext(true);
        gatekeeper.IsContextUntrusted.Should().BeTrue();

        var normalContext = new ToolExecutionContext(IsUntrustedInput: false, SourceMessageId: "msg-4");
        await gatekeeper.RequestExecutionApprovalAsync(LowRiskTool, args.RootElement, normalContext);
        promptTriggered.Should().BeTrue();

        // Act 2: Reset context to trusted
        promptTriggered = false;
        gatekeeper.TrackUntrustedContext(false);
        gatekeeper.IsContextUntrusted.Should().BeFalse();

        await gatekeeper.RequestExecutionApprovalAsync(LowRiskTool, args.RootElement, normalContext);
        promptTriggered.Should().BeFalse("trusted context should not prompt for low-risk tool");
    }
}
