using System.Text.Json;

namespace Companion.Core;

/// <summary>
/// Gatekeeper security engine interface responsible for taint tracking and confirmation prompts.
/// Matches and extends PROJECT.md section 1.2.
/// </summary>
public interface IGatekeeperService
{
    /// <summary>
    /// Requests approval for tool execution. Returns true if approved (or autonomous); false if denied/timed out.
    /// </summary>
    Task<bool> RequestExecutionApprovalAsync(
        ToolDefinition tool,
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the global untrusted context tracker (e.g. upon reading web content or screen OCR).
    /// </summary>
    void TrackUntrustedContext(bool isUntrusted);

    /// <summary>
    /// Indicates whether the active session is currently tainted with untrusted external content.
    /// </summary>
    bool IsContextUntrusted { get; }

    /// <summary>
    /// Resets the untrusted context flag back to trusted.
    /// </summary>
    void ResetUntrustedContext();

    /// <summary>
    /// Event fired when user confirmation is required. Subscribed by IslandWindow and IPC broadcaster.
    /// </summary>
    event Action<GatekeeperRequest>? ApprovalRequested;

    /// <summary>
    /// Event fired when a pending request is resolved (approved, denied, or timed out).
    /// </summary>
    event Action<string, bool>? RequestResolved;

    /// <summary>
    /// Resolves a pending confirmation request. Triggered by IslandWindow buttons or IPC (gatekeeper.respond).
    /// </summary>
    bool RespondToRequest(string requestId, bool approved);

    /// <summary>
    /// Cancels all pending requests immediately (e.g. emergency kill switch Ctrl+Alt+Backspace).
    /// </summary>
    void CancelAllPendingRequests();

    /// <summary>
    /// Lists all currently pending confirmation requests.
    /// </summary>
    IReadOnlyList<GatekeeperRequest> GetPendingRequests();
}
