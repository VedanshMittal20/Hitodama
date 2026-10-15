namespace Companion.Core;

/// <summary>
/// Defines the security risk classification for tools.
/// </summary>
public enum ToolRiskLevel
{
    /// <summary>
    /// Non-destructive, read-only, or benign actions.
    /// Can execute autonomously in trusted contexts.
    /// </summary>
    Low = 0,

    /// <summary>
    /// File modifications, system lock, or sensitive state changes.
    /// Requires user confirmation.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// Arbitrary command execution, system reboot/shutdown, or credential operations.
    /// Always requires explicit user confirmation.
    /// </summary>
    High = 2
}
