using System.Text.Json;
using Companion.Core;

namespace Companion.Services.Tools;

public interface IToolExecutionService
{
    Task<string> ExecuteAsync(
        string name,
        JsonElement arguments,
        string sourceMessageId,
        CancellationToken cancellationToken = default);

    void TrackUntrustedContext(bool isUntrusted);
}
