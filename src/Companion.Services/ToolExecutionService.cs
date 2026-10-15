using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Companion.Core;
using Companion.Services.Tools;

namespace Companion.Services;

public sealed class ToolExecutionService : IToolExecutionService, IDisposable
{
    private readonly DatabaseService _database;
    private readonly SystemControlService _systemControls;
    private readonly MediaService _media;
    private readonly Action<string> _launchApp;
    private readonly DesktopAutomationService _desktopAutomation = new();
    private readonly GatekeeperSecurityEngine _gatekeeper;
    private readonly ToolRegistry _registry = new();
    private readonly object _auditLock = new();
    private readonly AsyncLocal<IntPtr?> _approvedTargetWindow = new();
    private readonly string _auditLogPath;

    public ToolRegistry Registry => _registry;

    public ToolExecutionService(
        DatabaseService database,
        SystemControlService systemControls,
        MediaService media,
        Action<string> launchApp,
        GatekeeperSecurityEngine gatekeeper,
        string? auditLogPath = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _systemControls = systemControls ?? throw new ArgumentNullException(nameof(systemControls));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _launchApp = launchApp ?? throw new ArgumentNullException(nameof(launchApp));
        _gatekeeper = gatekeeper ?? throw new ArgumentNullException(nameof(gatekeeper));
        _auditLogPath = auditLogPath ?? AppPaths.AuditLogPath;

        Register("open_app", "Launch an installed Start Menu application by name.", ToolRiskLevel.Low, "System",
            """{"type":"OBJECT","properties":{"app_name":{"type":"STRING"}},"required":["app_name"]}""",
            OpenAppAsync);
        Register("open_url", "Open an http or https URL in the default browser.", ToolRiskLevel.Low, "System",
            """{"type":"OBJECT","properties":{"url":{"type":"STRING"}},"required":["url"]}""",
            OpenUrlAsync);
        Register("media_control", "Control the current media session or set master volume.", ToolRiskLevel.Low, "Media",
            """{"type":"OBJECT","properties":{"action":{"type":"STRING","enum":["play","pause","toggle","next","previous","set_volume"]},"percent":{"type":"INTEGER"}},"required":["action"]}""",
            MediaControlAsync);
        Register("system_toggle", "Change a supported Windows system setting.", ToolRiskLevel.Medium, "System",
            """{"type":"OBJECT","properties":{"setting":{"type":"STRING","enum":["dark_mode","mute","wifi","bluetooth"]},"state":{"type":"BOOLEAN"}},"required":["setting","state"]}""",
            SystemToggleAsync);
        Register("manage_todo", "Add, list, complete, or delete a to-do item.", ToolRiskLevel.Low, "Planner",
            """{"type":"OBJECT","properties":{"action":{"type":"STRING","enum":["add","list","complete","delete"]},"title":{"type":"STRING"},"id":{"type":"INTEGER"},"due_time":{"type":"STRING"}},"required":["action"]}""",
            ManageTodoAsync);
        Register("schedule_action", "Schedule a supported action for later or at a daily local time.", ToolRiskLevel.Medium, "Planner",
            """{"type":"OBJECT","properties":{"description":{"type":"STRING"},"tool_name":{"type":"STRING","enum":["open_app","open_url","media_control","system_toggle"]},"args_json":{"type":"STRING"},"trigger_iso_or_delay_minutes":{"type":"STRING"},"daily_time_hhmm":{"type":"STRING"}},"required":["description","tool_name","args_json"]}""",
            ScheduleActionAsync);
        Register("create_routine", "Create a daily routine with up to ten supported actions.", ToolRiskLevel.Medium, "Planner",
            """{"type":"OBJECT","properties":{"name":{"type":"STRING"},"steps_json":{"type":"STRING"},"daily_time_hhmm":{"type":"STRING"}},"required":["name","steps_json","daily_time_hhmm"]}""",
            CreateRoutineAsync);
        Register("read_active_window", "Read visible text and control names from the active application window.", ToolRiskLevel.Low, "DesktopUIA",
            """{"type":"OBJECT","properties":{} }""",
            ReadActiveWindowAsync);
        Register("click_ui_element", "Click a uniquely named button or link in the active application.", ToolRiskLevel.Medium, "DesktopUIA",
            """{"type":"OBJECT","properties":{"element_name":{"type":"STRING"}},"required":["element_name"]}""",
            ClickUiElementAsync);
        Register("type_into_active_window", "Type text into the active application window.", ToolRiskLevel.High, "DesktopUIA",
            """{"type":"OBJECT","properties":{"text":{"type":"STRING"},"press_enter":{"type":"BOOLEAN"}},"required":["text"]}""",
            TypeIntoActiveWindowAsync);
        Register("remember_fact", "Save a useful user preference or fact for future sessions.", ToolRiskLevel.Low, "Planner",
            """{"type":"OBJECT","properties":{"fact":{"type":"STRING"},"tags":{"type":"STRING"}},"required":["fact"]}""",
            RememberFactAsync);

        _registry.Register(new SystemControlTool(_systemControls), "System");
        _registry.Register(new ShellExecuteTool(), "System");
        _registry.Register(new FileReadWriteTool(), "System");
        _registry.Register(new WebSearchTool(_gatekeeper), "Research");
    }

    public void TrackUntrustedContext(bool isUntrusted) => _gatekeeper.TrackUntrustedContext(isUntrusted);

    public void Dispose()
    {
        foreach (ToolDefinition definition in _registry.GetAllDefinitions())
        {
            definition.Schema?.Dispose();
        }
    }

    public async Task<string> ExecuteAsync(
        string name,
        JsonElement arguments,
        string sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryGetTool(name, out ITool? tool) || tool is null)
        {
            throw new InvalidOperationException($"Tool '{name}' is not registered.");
        }

        IntPtr? targetWindow = name is "click_ui_element" or "type_into_active_window"
            ? _desktopAutomation.GetActiveWindowHandle()
            : null;
        var context = new ToolExecutionContext(_gatekeeper.IsContextUntrusted, sourceMessageId);
        ToolDefinition executionDefinition = tool.Definition with
        {
            RiskLevel = tool.AssessRisk(arguments, context)
        };
        bool approved = await _gatekeeper.RequestExecutionApprovalAsync(
            executionDefinition,
            arguments,
            context,
            cancellationToken);
        if (!approved)
        {
            WriteAudit(name, arguments, "denied");
            throw new UnauthorizedAccessException($"The user did not approve '{name}'.");
        }

        try
        {
            IntPtr? previousTargetWindow = _approvedTargetWindow.Value;
            _approvedTargetWindow.Value = targetWindow;
            string result;
            try
            {
                result = await tool.ExecuteAsync(arguments, cancellationToken);
            }
            finally
            {
                _approvedTargetWindow.Value = previousTargetWindow;
            }

            WriteAudit(name, arguments, "succeeded");
            _database.IncrementStat("tools_executed");
            return result;
        }
        catch (Exception exception)
        {
            WriteAudit(name, arguments, $"failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void Register(
        string name,
        string description,
        ToolRiskLevel risk,
        string pack,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> execute)
    {
        _registry.Register(new DelegateTool(
            new ToolDefinition(name, description, risk, JsonDocument.Parse(schema)),
            execute), pack);
    }

    private Task<string> OpenAppAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string appName = RequiredString(arguments, "app_name");
        _launchApp(appName);
        return Task.FromResult($"Launched {appName}.");
    }

    private static Task<string> OpenUrlAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string value = RequiredString(arguments, "url");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("Only absolute http and https URLs can be opened.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult($"Opened {uri.Host}.");
    }

    private async Task<string> MediaControlAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string action = RequiredString(arguments, "action");
        bool success = action switch
        {
            "play" when _media.CurrentTrack is { IsPlaying: false } => await _media.TogglePlayPauseAsync(),
            "play" => _media.CurrentTrack is { IsPlaying: true },
            "pause" when _media.CurrentTrack is { IsPlaying: true } => await _media.TogglePlayPauseAsync(),
            "pause" => _media.CurrentTrack is { IsPlaying: false },
            "toggle" => await _media.TogglePlayPauseAsync(),
            "next" => await _media.SkipNextAsync(),
            "previous" => await _media.SkipPreviousAsync(),
            "set_volume" => SetVolume(arguments),
            _ => throw new InvalidDataException($"Unsupported media action '{action}'.")
        };
        if (!success)
        {
            throw new InvalidOperationException("No active media session accepted the request.");
        }

        return action == "set_volume" ? "Master volume updated." : $"Media action '{action}' completed.";
    }

    private bool SetVolume(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("percent", out JsonElement percent) ||
            !percent.TryGetInt32(out int value) || value is < 0 or > 100)
        {
            throw new InvalidDataException("Volume percent must be an integer from 0 to 100.");
        }

        _systemControls.SetMasterVolumePercent(value);
        return true;
    }

    private async Task<string> SystemToggleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string setting = RequiredString(arguments, "setting");
        if (!arguments.TryGetProperty("state", out JsonElement state) ||
            state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("A boolean state is required.");
        }

        bool enabled = state.GetBoolean();
        switch (setting)
        {
            case "dark_mode":
                _systemControls.SetDarkMode(enabled);
                break;
            case "mute":
                _systemControls.SetMuted(enabled);
                break;
            case "wifi":
                await _systemControls.SetWifiEnabledAsync(enabled, cancellationToken);
                break;
            case "bluetooth":
                await _systemControls.SetBluetoothEnabledAsync(enabled);
                break;
            default:
                throw new InvalidDataException($"Unsupported system setting '{setting}'.");
        }

        return $"{setting} is {(enabled ? "on" : "off")}.";
    }

    private Task<string> ManageTodoAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string action = RequiredString(arguments, "action");
        string title = arguments.TryGetProperty("title", out JsonElement titleElement) &&
                       titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        switch (action)
        {
            case "add":
                if (title.Length == 0)
                {
                    throw new InvalidDataException("A title is required to add a to-do.");
                }

                string? dueTime = arguments.TryGetProperty("due_time", out JsonElement dueElement) &&
                                  dueElement.ValueKind == JsonValueKind.String
                    ? dueElement.GetString()
                    : null;
                long id = _database.AddTodo(title, dueTime);
                return Task.FromResult($"Added to-do {id}: {title}.");
            case "list":
                return Task.FromResult(JsonSerializer.Serialize(_database.GetTodos()));
            case "complete":
            case "delete":
                TodoItem? todo = FindTodo(arguments, title);
                if (todo is null)
                {
                    throw new InvalidOperationException("No matching to-do was found.");
                }

                bool changed = action == "complete"
                    ? _database.ToggleTodo(todo.Id)
                    : _database.DeleteTodo(todo.Id);
                if (!changed)
                {
                    throw new InvalidOperationException("The to-do changed before the request completed.");
                }

                return Task.FromResult($"{action}d to-do: {todo.Title}.");
            default:
                throw new InvalidDataException($"Unsupported to-do action '{action}'.");
        }
    }

    private TodoItem? FindTodo(JsonElement arguments, string title)
    {
        if (arguments.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out long id))
        {
            return _database.GetTodos().FirstOrDefault(todo => todo.Id == id);
        }

        if (title.Length == 0)
        {
            throw new InvalidDataException("Provide a to-do id or title.");
        }

        return _database.GetTodos().FirstOrDefault(todo =>
            string.Equals(todo.Title, title, StringComparison.CurrentCultureIgnoreCase));
    }

    private Task<string> ScheduleActionAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string description = RequiredString(arguments, "description");
        string toolName = RequiredString(arguments, "tool_name");
        if (toolName is not ("open_app" or "open_url" or "media_control" or "system_toggle"))
        {
            throw new InvalidDataException("Only supported system, media, and URL actions can be scheduled.");
        }

        string argsJson = RequiredString(arguments, "args_json");
        using (JsonDocument parsed = JsonDocument.Parse(argsJson))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Scheduled action arguments must be a JSON object.");
            }
        }

        string? dailyTime = arguments.TryGetProperty("daily_time_hhmm", out JsonElement dailyElement) &&
                            dailyElement.ValueKind == JsonValueKind.String
            ? dailyElement.GetString()
            : null;
        string? triggerValue = arguments.TryGetProperty("trigger_iso_or_delay_minutes", out JsonElement triggerElement) &&
                               triggerElement.ValueKind == JsonValueKind.String
            ? triggerElement.GetString()
            : null;
        DateTime triggerUtc;
        string? cron = null;
        if (!string.IsNullOrWhiteSpace(dailyTime))
        {
            if (!TimeOnly.TryParseExact(dailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
            {
                throw new InvalidDataException("Daily time must use 24-hour HH:mm format.");
            }

            DateTime nextLocal = DateTime.Today.Add(time.ToTimeSpan());
            if (nextLocal <= DateTime.Now)
            {
                nextLocal = nextLocal.AddDays(1);
            }

            triggerUtc = nextLocal.ToUniversalTime();
            cron = $"daily:{time:HH:mm}";
        }
        else if (int.TryParse(triggerValue, NumberStyles.None, CultureInfo.InvariantCulture, out int delayMinutes) &&
                 delayMinutes is >= 1 and <= 525600)
        {
            triggerUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
        }
        else if (DateTimeOffset.TryParse(triggerValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp) &&
                 timestamp.UtcDateTime > DateTime.UtcNow)
        {
            triggerUtc = timestamp.UtcDateTime;
        }
        else
        {
            throw new InvalidDataException("Provide a future ISO timestamp, a delay in minutes, or a daily local time.");
        }

        long id = _database.AddScheduledAction(description, toolName, argsJson, triggerUtc, cron);
        return Task.FromResult($"Scheduled '{description}' (action {id}).");
    }

    private Task<string> RememberFactAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fact = RequiredString(arguments, "fact");
        string? tags = arguments.TryGetProperty("tags", out JsonElement tagsElement) &&
                       tagsElement.ValueKind == JsonValueKind.String
            ? tagsElement.GetString()
            : null;
        long id = _database.AddMemory(fact, tags);
        return Task.FromResult($"Saved memory {id}.");
    }

    private Task<string> ReadActiveWindowAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = _desktopAutomation.ReadActiveWindow();
            _gatekeeper.TrackUntrustedContext(true);
            return content;
        }, cancellationToken);

    private Task<string> ClickUiElementAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string elementName = RequiredString(arguments, "element_name");
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr targetWindow = _approvedTargetWindow.Value
                ?? throw new InvalidOperationException("No approved target window is available.");
            _desktopAutomation.ClickElement(elementName, targetWindow);
            return $"Clicked '{elementName}'.";
        }, cancellationToken);
    }

    private Task<string> TypeIntoActiveWindowAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string text = RequiredString(arguments, "text");
        bool pressEnter = arguments.TryGetProperty("press_enter", out JsonElement enterElement) &&
                          enterElement.ValueKind == JsonValueKind.True;
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr targetWindow = _approvedTargetWindow.Value
                ?? throw new InvalidOperationException("No approved target window is available.");
            _desktopAutomation.TypeIntoActiveWindow(text, pressEnter, targetWindow);
            return "Text was typed into the active window.";
        }, cancellationToken);
    }

    private Task<string> CreateRoutineAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = RequiredString(arguments, "name");
        string stepsJson = RequiredString(arguments, "steps_json");
        string dailyTime = RequiredString(arguments, "daily_time_hhmm");
        if (!TimeOnly.TryParseExact(dailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            throw new InvalidDataException("Routine time must use 24-hour HH:mm format.");
        }

        using JsonDocument steps = JsonDocument.Parse(stepsJson);
        if (steps.RootElement.ValueKind != JsonValueKind.Array ||
            steps.RootElement.GetArrayLength() is < 1 or > 10)
        {
            throw new InvalidDataException("Routine steps must be a JSON array containing 1 to 10 actions.");
        }

        foreach (JsonElement step in steps.RootElement.EnumerateArray())
        {
            if (!step.TryGetProperty("tool_name", out JsonElement toolNameElement) ||
                toolNameElement.ValueKind != JsonValueKind.String ||
                toolNameElement.GetString() is not ("open_app" or "open_url" or "media_control" or "system_toggle") ||
                !step.TryGetProperty("args", out JsonElement stepArgs) ||
                stepArgs.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Each routine step must contain a supported tool_name and an args JSON object.");
            }
        }

        DateTime nextLocalTime = DateTime.Today.Add(time.ToTimeSpan());
        if (nextLocalTime <= DateTime.Now)
        {
            nextLocalTime = nextLocalTime.AddDays(1);
        }

        long id = _database.AddRoutine(
            name,
            stepsJson,
            nextLocalTime.ToUniversalTime(),
            time.ToString("HH:mm", CultureInfo.InvariantCulture));
        return Task.FromResult($"Created daily routine '{name}' (routine {id}) at {time:HH:mm}.");
    }

    private void WriteAudit(string toolName, JsonElement arguments, string outcome)
    {
        string argumentsJson = arguments.GetRawText();
        if (argumentsJson.Length > 8192)
        {
            argumentsJson = argumentsJson[..8192];
        }

        string line = JsonSerializer.Serialize(new AuditEntry(
            DateTime.UtcNow,
            toolName,
            argumentsJson,
            outcome));
        _database.AddAuditEntry(toolName, argumentsJson, outcome);
        lock (_auditLock)
        {
            File.AppendAllText(_auditLogPath, line + Environment.NewLine);
        }
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"A non-empty '{name}' value is required.");
        }

        return value.GetString()!.Trim();
    }

    private sealed class DelegateTool(
        ToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<string>> execute) : ITool
    {
        public ToolDefinition Definition { get; } = definition;

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            execute(arguments, cancellationToken);

        public async Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string output = await execute(arguments, cancellationToken);
                return ToolResult.Ok(output);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }
    }
}
