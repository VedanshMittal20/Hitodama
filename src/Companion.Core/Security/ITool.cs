using System.Text.Json;

namespace Companion.Core;

/// <summary>
/// Interface implemented by all Void tools.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Static tool metadata and baseline schema.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Dynamically evaluates the risk of this execution based on supplied arguments.
    /// Defaults to Definition.RiskLevel if not overridden.
    /// </summary>
    ToolRiskLevel AssessRisk(JsonElement arguments, ToolExecutionContext context) =>
        Definition.RiskLevel;

    /// <summary>
    /// Executes the tool logic asynchronously with context awareness.
    /// Primary contract to be implemented by tools. Throws NotSupportedException if not overridden.
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Tool '{Definition?.Name ?? "Unknown"}' does not implement {nameof(ExecuteAsync)}.");
    }

    /// <summary>
    /// Backward-compatible execution overload without context.
    /// Delegates to the primary ExecuteAsync method with a trusted context.
    /// </summary>
    async Task<string> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(arguments, ToolExecutionContext.Trusted(), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "Tool execution failed.");
        }
        return result.Output ?? string.Empty;
    }
}
