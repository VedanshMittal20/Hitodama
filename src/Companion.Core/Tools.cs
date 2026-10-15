using System.Text.Json;

namespace Companion.Core;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _abilityPacks = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool, string? abilityPack = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Definition.Name] = tool;
        if (!string.IsNullOrWhiteSpace(abilityPack))
        {
            _abilityPacks[tool.Definition.Name] = abilityPack;
        }
    }

    public bool TryGetTool(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyCollection<ToolDefinition> GetAllDefinitions() =>
        _tools.Values.Select(t => t.Definition).ToArray();

    public IReadOnlyCollection<ToolDefinition> GetDefinitionsForAbilityPacks(IEnumerable<string> enabledAbilityPacks)
    {
        ArgumentNullException.ThrowIfNull(enabledAbilityPacks);
        var enabled = new HashSet<string>(enabledAbilityPacks, StringComparer.OrdinalIgnoreCase);
        return _tools.Values
            .Where(tool => !_abilityPacks.TryGetValue(tool.Definition.Name, out string? pack) || enabled.Contains(pack))
            .Select(tool => tool.Definition)
            .ToArray();
    }
}
