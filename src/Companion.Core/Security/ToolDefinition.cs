using System.Text.Json;

namespace Companion.Core;

/// <summary>
/// Represents the formal metadata and schema declaration for an executable tool.
/// Matches the specification in PROJECT.md.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    ToolRiskLevel RiskLevel,
    JsonDocument? Schema = null)
{
    /// <summary>
    /// Converts this definition into an OpenAPI / Gemini Live function declaration object.
    /// Used directly in the Gemini Live WebSocket handshake setup payload.
    /// </summary>
    public object ToGeminiFunctionDeclaration() => new
    {
        name = Name,
        description = Description,
        parameters = Schema?.RootElement
    };
}
