using System.Collections.Concurrent;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace Companion.Core;

public sealed class GatekeeperSecurityEngine : IGatekeeperService, IDisposable
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
    private readonly Func<ToolDefinition, JsonElement, ToolExecutionContext, Task<bool>>? _approvalPromptHandler;
    private readonly DatabaseService? _database;
    private readonly TimeSpan _defaultTimeout;
    private int _untrustedContextFlag;
    private bool _disposed;

    private int _totalApprovalRequests;
    private int _approvedCount;
    private int _rejectedCount;

    public bool IsContextUntrusted => Volatile.Read(ref _untrustedContextFlag) == 1;

    public int TotalApprovalRequests => Volatile.Read(ref _totalApprovalRequests);
    public int ApprovedCount => Volatile.Read(ref _approvedCount);
    public int RejectedCount => Volatile.Read(ref _rejectedCount);

    public event Action<GatekeeperRequest>? ApprovalRequested;
    public event Action<string, bool>? RequestResolved;

    public GatekeeperSecurityEngine(
        Func<ToolDefinition, JsonElement, ToolExecutionContext, Task<bool>>? approvalPromptHandler = null,
        DatabaseService? database = null,
        TimeSpan? defaultTimeout = null)
    {
        _approvalPromptHandler = approvalPromptHandler;
        _database = database;
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

        // Untrusted context or medium/high risk requires explicit confirmation
        bool isUntrusted = IsContextUntrusted || context.IsUntrustedInput;
        bool requiresConfirmation = tool.RiskLevel is ToolRiskLevel.Medium or ToolRiskLevel.High || isUntrusted;

        if (!requiresConfirmation)
        {
            Interlocked.Increment(ref _approvedCount);
            return true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _rejectedCount);
            return false;
        }

        // If an approvalPromptHandler is registered, use it directly (e.g. from tests or simple UI delegation)
        if (_approvalPromptHandler is not null)
        {
            bool approved;
            try
            {
                approved = await _approvalPromptHandler(tool, arguments, context);
            }
            catch
            {
                approved = false;
            }

            if (approved)
            {
                Interlocked.Increment(ref _approvedCount);
            }
            else
            {
                Interlocked.Increment(ref _rejectedCount);
            }

            return approved;
        }

        // Asynchronous confirmation workflow with timeout & events
        string requestId = Guid.NewGuid().ToString("N");
        string reason = isUntrusted
            ? $"Tool '{tool.Name}' requires approval because session context is untrusted."
            : $"Tool '{tool.Name}' ({tool.RiskLevel} risk) requires approval.";

        var request = new GatekeeperRequest(
            requestId,
            tool,
            arguments.Clone(),
            context,
            tool.RiskLevel,
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

        bool result = await tcs.Task;
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
            item.Tcs.TrySetResult(false);
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
