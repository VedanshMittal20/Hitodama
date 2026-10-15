using System.Text.Json;

namespace Companion.Core;

/// <summary>
/// Encapsulates a pending execution approval request displayed in Island UI or sent over IPC.
/// </summary>
public sealed record GatekeeperRequest(
    string RequestId,
    ToolDefinition Tool,
    JsonElement Arguments,
    ToolExecutionContext Context,
    ToolRiskLevel EvaluatedRisk,
    string Reason,
    DateTime CreatedUtc,
    TimeSpan Timeout);
