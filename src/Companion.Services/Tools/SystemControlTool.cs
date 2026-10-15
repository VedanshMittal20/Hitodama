using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Companion.Core;

namespace Companion.Services.Tools;

public sealed class SystemControlTool : ITool
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private readonly SystemControlService _systemControls;

    public ToolDefinition Definition { get; }

    public SystemControlTool(SystemControlService? systemControls = null)
    {
        _systemControls = systemControls ?? new SystemControlService();

        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["get_volume", "set_volume", "get_dark_mode", "set_dark_mode", "lock_workstation", "sleep", "restart", "shutdown"],
                  "description": "System action to execute."
                },
                "volume": {
                  "type": "integer",
                  "description": "Volume percentage from 0 to 100 (for set_volume)."
                },
                "enabled": {
                  "type": "boolean",
                  "description": "True or false (for set_dark_mode)."
                }
              },
              "required": ["action"]
            }
            """);

        Definition = new ToolDefinition(
            "system_control",
            "Controls Windows system state: master volume, mute, dark mode theme, screen lock, sleep, reboot, and shutdown.",
            ToolRiskLevel.Low,
            JsonDocument.Parse(doc.RootElement.GetRawText()));
    }

    public ToolRiskLevel AssessRisk(JsonElement arguments, ToolExecutionContext context)
    {
        if (arguments.TryGetProperty("action", out var actionProp))
        {
            string? action = actionProp.GetString();
            if (string.Equals(action, "restart", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                return ToolRiskLevel.High;
            }

            if (string.Equals(action, "lock_workstation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "sleep", StringComparison.OrdinalIgnoreCase))
            {
                return ToolRiskLevel.Medium;
            }
        }

        if (context.IsUntrustedInput)
        {
            return ToolRiskLevel.Medium;
        }

        return ToolRiskLevel.Low;
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetProperty("action", out var actionProp) ||
            actionProp.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolResult.Fail("The 'action' property is required."));
        }

        string action = actionProp.GetString()!.Trim().ToLowerInvariant();

        try
        {
            switch (action)
            {
                case "get_volume":
                    var (device, vol) = _systemControls.GetDefaultAudioOutput();
                    return Task.FromResult(ToolResult.Ok($"Audio output device: {device}, Volume: {vol}%."));

                case "set_volume":
                    if (!arguments.TryGetProperty("volume", out var volProp) || !volProp.TryGetInt32(out int volume) || volume is < 0 or > 100)
                    {
                        return Task.FromResult(ToolResult.Fail("Volume percent must be an integer between 0 and 100."));
                    }
                    _systemControls.SetMasterVolumePercent(volume);
                    return Task.FromResult(ToolResult.Ok($"Master volume set to {volume}%."));

                case "get_dark_mode":
                    bool isDark = _systemControls.IsDarkModeEnabled;
                    return Task.FromResult(ToolResult.Ok($"Dark mode is currently {(isDark ? "enabled" : "disabled")}."));

                case "set_dark_mode":
                    if (!arguments.TryGetProperty("enabled", out var enabledProp) ||
                        enabledProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return Task.FromResult(ToolResult.Fail("A boolean 'enabled' property is required for set_dark_mode."));
                    }
                    bool enable = enabledProp.GetBoolean();
                    _systemControls.SetDarkMode(enable);
                    return Task.FromResult(ToolResult.Ok($"Dark mode {(enable ? "enabled" : "disabled")}."));

                case "lock_workstation":
                    bool locked = LockWorkStation();
                    return Task.FromResult(locked ? ToolResult.Ok("Workstation locked.") : ToolResult.Fail("Failed to lock workstation."));

                case "sleep":
                    Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { CreateNoWindow = true });
                    return Task.FromResult(ToolResult.Ok("System entering sleep state."));

                case "restart":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"Hitodama scheduled restart\"") { CreateNoWindow = true });
                    return Task.FromResult(ToolResult.Ok("System restarting in 5 seconds."));

                case "shutdown":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 5 /c \"Hitodama scheduled shutdown\"") { CreateNoWindow = true });
                    return Task.FromResult(ToolResult.Ok("System shutting down in 5 seconds."));

                default:
                    return Task.FromResult(ToolResult.Fail($"Unknown action: '{action}'."));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Action '{action}' failed: {ex.Message}"));
        }
    }
}
