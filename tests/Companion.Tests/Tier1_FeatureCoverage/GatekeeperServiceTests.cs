using System.Text.Json;
using Companion.Core;
using Companion.Services.Tools;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class GatekeeperServiceTests : IDisposable
{
    private readonly GatekeeperService _sut = new(TimeSpan.FromSeconds(5));

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public async Task LowRiskTool_TrustedContext_ApprovesAutonomously()
    {
        // Arrange
        var tool = new ToolDefinition("System.GetVolume", "Reads volume", ToolRiskLevel.Low);
        var context = ToolExecutionContext.Trusted();
        using var args = JsonDocument.Parse("{}");

        // Act
        bool approved = await _sut.RequestExecutionApprovalAsync(tool, args.RootElement, context);

        // Assert
        approved.Should().BeTrue();
        _sut.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task LowRiskTool_UntrustedContext_RequiresUserConfirmation()
    {
        // Arrange
        var tool = new ToolDefinition("System.GetVolume", "Reads volume", ToolRiskLevel.Low);
        var untrustedContext = ToolExecutionContext.Untrusted("msg-1", "Web Snippet");
        using var args = JsonDocument.Parse("{}");

        _sut.ApprovalRequested += req =>
        {
            // Simulate user clicking "Allow" in UI or IPC
            _sut.RespondToRequest(req.RequestId, true);
        };

        // Act
        bool approved = await _sut.RequestExecutionApprovalAsync(tool, args.RootElement, untrustedContext);

        // Assert
        approved.Should().BeTrue();
    }

    [Fact]
    public async Task MediumRiskTool_RequiresConfirmation_AndReturnsFalseWhenDenied()
    {
        // Arrange
        var tool = new ToolDefinition("System.SetVolume", "Sets volume", ToolRiskLevel.Medium);
        var context = ToolExecutionContext.Trusted();
        using var args = JsonDocument.Parse("{\"volume\": 50}");

        _sut.ApprovalRequested += req =>
        {
            // Simulate user clicking "Deny"
            _sut.RespondToRequest(req.RequestId, false);
        };

        // Act
        bool approved = await _sut.RequestExecutionApprovalAsync(tool, args.RootElement, context);

        // Assert
        approved.Should().BeFalse();
    }

    [Fact]
    public async Task HighRiskTool_RequiresConfirmation_AndReturnsTrueWhenApproved()
    {
        // Arrange
        var tool = new ToolDefinition("shell_execute", "Executes command", ToolRiskLevel.High);
        var context = ToolExecutionContext.Trusted();
        using var args = JsonDocument.Parse("{\"command\": \"powershell.exe\"}");

        _sut.ApprovalRequested += req =>
        {
            req.Tool.Name.Should().Be("shell_execute");
            req.EvaluatedRisk.Should().Be(ToolRiskLevel.High);
            _sut.RespondToRequest(req.RequestId, true);
        };

        // Act
        bool approved = await _sut.RequestExecutionApprovalAsync(tool, args.RootElement, context);

        // Assert
        approved.Should().BeTrue();
    }

    [Fact]
    public async Task Gatekeeper_Timeout_DefaultDeniesToolExecution()
    {
        // Arrange: Short timeout of 100ms
        using var fastTimeoutGatekeeper = new GatekeeperService(TimeSpan.FromMilliseconds(100));
        var tool = new ToolDefinition("Power.Shutdown", "Shuts down PC", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        // Act: User does not respond
        bool approved = await fastTimeoutGatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());

        // Assert
        approved.Should().BeFalse("unanswered approval request must default to Deny upon timeout");
    }

    [Fact]
    public async Task EmergencyKillSwitch_CancelsAllPendingRequestsWithFalse()
    {
        // Arrange
        var tool1 = new ToolDefinition("Power.Sleep", "Sleep PC", ToolRiskLevel.Medium);
        var tool2 = new ToolDefinition("Power.Reboot", "Reboot PC", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        var task1 = _sut.RequestExecutionApprovalAsync(tool1, args.RootElement, ToolExecutionContext.Trusted());
        var task2 = _sut.RequestExecutionApprovalAsync(tool2, args.RootElement, ToolExecutionContext.Trusted());

        _sut.GetPendingRequests().Should().HaveCount(2);

        // Act: Trigger emergency abort
        _sut.CancelAllPendingRequests();

        bool result1 = await task1;
        bool result2 = await task2;

        // Assert
        result1.Should().BeFalse();
        result2.Should().BeFalse();
        _sut.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public void TaintTracking_TrackAndReset_TogglesContextState()
    {
        // Initial state is trusted
        _sut.IsContextUntrusted.Should().BeFalse();

        // Taint context
        _sut.TrackUntrustedContext(true);
        _sut.IsContextUntrusted.Should().BeTrue();

        // Reset context
        _sut.ResetUntrustedContext();
        _sut.IsContextUntrusted.Should().BeFalse();
    }
}
