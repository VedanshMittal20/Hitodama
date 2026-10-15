namespace Companion.Core;

/// <summary>
/// Carries execution origin and taint metadata for security evaluation.
/// Matches the specification in PROJECT.md.
/// </summary>
public sealed record ToolExecutionContext(
    bool IsUntrustedInput,
    string SourceMessageId,
    string? OriginDescription = null,
    DateTime CreatedUtc = default)
{
    /// <summary>
    /// Creates a trusted context originating directly from authorized local user speech.
    /// </summary>
    public static ToolExecutionContext Trusted(string sourceMessageId = "user_direct") =>
        new(false, sourceMessageId, "User Direct Speech", DateTime.UtcNow);

    /// <summary>
    /// Creates an untrusted context originating from external data (web, screen OCR, clipboard).
    /// </summary>
    public static ToolExecutionContext Untrusted(string sourceMessageId, string originDescription) =>
        new(true, sourceMessageId, originDescription, DateTime.UtcNow);
}
