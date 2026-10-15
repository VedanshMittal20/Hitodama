using System.Diagnostics;
using System.Text.Json;
using Companion.Core;

namespace Companion.Services.Tools;

public sealed class ShellExecuteTool : ITool
{
    private const int MaxOutputLength = 16384;
    public ToolDefinition Definition { get; }

    public ShellExecuteTool()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Executable or shell command."
                },
                "arguments": {
                  "type": "string",
                  "description": "Arguments to pass to the executable."
                },
                "workingDirectory": {
                  "type": "string",
                  "description": "Optional working directory."
                },
                "timeoutSeconds": {
                  "type": "integer",
                  "description": "Maximum execution duration in seconds (default 15, max 60)."
                }
              },
              "required": ["command"]
            }
            """);

        Definition = new ToolDefinition(
            "shell_execute",
            "Executes a desktop executable or command line utility with arguments and captures stdout/stderr.",
            ToolRiskLevel.High,
            JsonDocument.Parse(doc.RootElement.GetRawText()));
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("command", out var cmdProp) ||
            cmdProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(cmdProp.GetString()))
        {
            return ToolResult.Fail("The 'command' property must be a non-empty string.");
        }

        string command = cmdProp.GetString()!.Trim();
        string args = arguments.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String
            ? argsProp.GetString() ?? string.Empty
            : string.Empty;

        string? workingDir = arguments.TryGetProperty("workingDirectory", out var wdProp) && wdProp.ValueKind == JsonValueKind.String
            ? wdProp.GetString()
            : null;

        int timeoutSeconds = 15;
        if (arguments.TryGetProperty("timeoutSeconds", out var timeoutProp) && timeoutProp.TryGetInt32(out int t))
        {
            timeoutSeconds = Math.Clamp(t, 1, 60);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (stdout.Length > MaxOutputLength) stdout = stdout[..MaxOutputLength] + " ...[truncated]";
            if (stderr.Length > MaxOutputLength) stderr = stderr[..MaxOutputLength] + " ...[truncated]";

            string combined = $"ExitCode: {process.ExitCode}\nOutput:\n{stdout}";
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                combined += $"\nError:\n{stderr}";
            }

            return process.ExitCode == 0
                ? ToolResult.Ok(combined)
                : ToolResult.Fail(combined);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have exited during kill
            }

            return ToolResult.Fail($"Execution timed out or was cancelled after {timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to launch process: {ex.Message}");
        }
    }
}
