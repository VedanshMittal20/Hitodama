using System.Collections.Concurrent;
using System.Text.Json;
using Companion.Core;

namespace Companion.Services.Tools;

/// <summary>
/// Gatekeeper Security Engine responsible for contextual taint tracking,
/// dynamic risk assessment, and asynchronous user confirmation dialogs.
/// </summary>
public sealed class GatekeeperService : IGatekeeperService, IDisposable
{
    private sealed class PendingApproval : IDisposable
    {
        public required GatekeeperRequest Request { get; init; }
        public required TaskCompletionSource<bool> Tcs { get; init; }
        public required System.Threading.Timer TimeoutTimer { get; init; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public void Dispose()
        {
            TimeoutTimer.Dispose();
            CancellationRegistration.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultTimeout;
    private int _untrustedContextFlag;
    private bool _disposed;

    private int _totalApprovalRequests;
    private int _approvedCount;
    private int _rejectedCount;

    public int TotalApprovalRequests => Volatile.Read(ref _totalApprovalRequests);
    public int ApprovedCount => Volatile.Read(ref _approvedCount);
    public int RejectedCount => Volatile.Read(ref _rejectedCount);

    public event Action<GatekeeperRequest>? ApprovalRequested;
    public event Action<string, bool>? RequestResolved;

    public bool IsContextUntrusted => Volatile.Read(ref _untrustedContextFlag) == 1;

    public GatekeeperService(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void TrackUntrustedContext(bool isUntrusted)
    {
        Interlocked.Exchange(ref _untrustedContextFlag, isUntrusted ? 1 : 0);
    }

    public void ResetUntrustedContext()
    {
        Interlocked.Exchange(ref _untrustedContextFlag, 0);
    }

    public bool RequiresApproval(
        ToolDefinition tool,
        JsonElement arguments,
        ToolExecutionContext context,
        out ToolRiskLevel evaluatedRisk,
        out string reason)
    {
        evaluatedRisk = tool.RiskLevel;
        bool isUntrusted = context.IsUntrustedInput || IsContextUntrusted;

        if (evaluatedRisk == ToolRiskLevel.High)
        {
            reason = isUntrusted
                ? $"High-risk tool '{tool.Name}' invoked while session context is UNTRUSTED (potential prompt injection)."
                : $"High-risk tool '{tool.Name}' requires explicit confirmation.";
            return true;
        }

        if (evaluatedRisk == ToolRiskLevel.Medium)
        {
            reason = isUntrusted
                ? $"Medium-risk tool '{tool.Name}' invoked while session context is UNTRUSTED."
                : $"Medium-risk tool '{tool.Name}' requires confirmation.";
            return true;
        }

        // Low risk:
        if (isUntrusted)
        {
            reason = $"Low-risk tool '{tool.Name}' requires confirmation because context is UNTRUSTED (potential prompt injection).";
            return true;
        }

        reason = "Low-risk tool permitted autonomously in trusted context.";
        return false;
    }

    public async Task<bool> RequestExecutionApprovalAsync(
        ToolDefinition tool,
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        Interlocked.Increment(ref _totalApprovalRequests);

        if (!RequiresApproval(tool, arguments, context, out ToolRiskLevel evaluatedRisk, out string reason))
        {
            Interlocked.Increment(ref _approvedCount);
            return true; // Autonomous execution allowed
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _rejectedCount);
            return false;
        }

        string requestId = Guid.NewGuid().ToString("N");
        var request = new GatekeeperRequest(
            requestId,
            tool,
            arguments.Clone(),
            context,
            evaluatedRisk,
            reason,
            DateTime.UtcNow,
            _defaultTimeout);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new System.Threading.Timer(OnTimeout, requestId, _defaultTimeout, Timeout.InfiniteTimeSpan);

        var pendingItem = new PendingApproval
        {
            Request = request,
            Tcs = tcs,
            TimeoutTimer = timer
        };

        if (cancellationToken.CanBeCanceled)
        {
            pendingItem.CancellationRegistration = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(requestId, out PendingApproval? item))
                {
                    item.Dispose();
                    item.Tcs.TrySetResult(false);
                    RequestResolved?.Invoke(requestId, false);
                }
            });
        }

        if (!_pending.TryAdd(requestId, pendingItem))
        {
            pendingItem.Dispose();
            Interlocked.Increment(ref _rejectedCount);
            return false;
        }

        ApprovalRequested?.Invoke(request);

        bool result = await tcs.Task.ConfigureAwait(false);
        if (result)
        {
            Interlocked.Increment(ref _approvedCount);
        }
        else
        {
            Interlocked.Increment(ref _rejectedCount);
        }

        return result;
    }

    public bool RespondToRequest(string requestId, bool approved)
    {
        if (_pending.TryRemove(requestId, out PendingApproval? item))
        {
            item.Dispose();
            item.Tcs.TrySetResult(approved);
            RequestResolved?.Invoke(requestId, approved);
            return true;
        }

        return false;
    }

    public void CancelAllPendingRequests()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out PendingApproval? item))
            {
                item.Dispose();
                item.Tcs.TrySetResult(false);
                RequestResolved?.Invoke(kvp.Key, false);
            }
        }
    }

    public IReadOnlyList<GatekeeperRequest> GetPendingRequests() =>
        _pending.Values.Select(p => p.Request).ToList();

    private void OnTimeout(object? state)
    {
        if (state is string requestId && _pending.TryRemove(requestId, out PendingApproval? item))
        {
            item.Dispose();
            item.Tcs.TrySetResult(false); // Default Deny on timeout
            RequestResolved?.Invoke(requestId, false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAllPendingRequests();
    }
}
