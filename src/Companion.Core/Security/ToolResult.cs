using System.Text.Json;

namespace Companion.Core;

/// <summary>
/// Represents the outcome of a tool execution.
/// </summary>
public sealed record ToolResult(
    bool Success,
    string? Output,
    string? ErrorMessage = null,
    JsonDocument? StructuredData = null)
{
    public static ToolResult Ok(string output, JsonDocument? structuredData = null) =>
        new(true, output, null, structuredData);

    public static ToolResult Fail(string errorMessage) =>
        new(false, null, errorMessage, null);
}
