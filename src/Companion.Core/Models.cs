namespace Companion.Core;

public enum SoulVisualStyle
{
    HitodamaFlame,
    CyberOrb,
    SolarEmber,
    VoidRipple
}

public enum VoicePreset
{
    Charon,
    Puck,
    Kore,
    Fenrir,
    Aoede
}

public sealed class SoulLoadout
{
    public string SoulName { get; set; } = "Hitodama";
    public SoulVisualStyle VisualStyle { get; set; } = SoulVisualStyle.HitodamaFlame;
    public string PersonalityName { get; set; } = "True Chad";
    public string PersonalityPrompt { get; set; } = "You are Hitodama, a sharp, ultra-concise, confident desktop soul living on the user's Windows PC. Never waffle. Execute tools decisively and speak in natural, punchy sentences.";
    public VoicePreset Voice { get; set; } = VoicePreset.Charon;
    public string Model { get; set; } = "models/gemini-2.5-flash-native-audio-preview-12-2025";
    public HashSet<string> EnabledAbilityPacks { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "DesktopUIA",
        "Planner",
        "Media",
        "Research"
    };
}

public sealed record TodoItem(long Id, string Title, string? DueTimeText, bool IsCompleted, DateTime CreatedUtc);

public sealed record ScheduledAction(
    long Id,
    string Description,
    string ToolName,
    string ArgsJson,
    DateTime TriggerUtc,
    string? CronExpression,
    bool IsEnabled);

public sealed record ClipEntry(
    long Id,
    string ContentType,
    string TextOrImagePath,
    string PreviewText,
    bool IsPinned,
    DateTime CreatedUtc);

public sealed record Routine(long Id, string Name, string StepsJson, DateTime CreatedUtc, bool IsEnabled);

public sealed record AuditEntry(DateTime CreatedUtc, string ToolName, string ArgumentsJson, string Outcome);

public sealed record MemoryEntry(long Id, string Fact, string? Tags, DateTime CreatedUtc);

public sealed record DailyStats(string DateKey, long DictatedWords, long VoiceCommands, long ToolsExecuted);
