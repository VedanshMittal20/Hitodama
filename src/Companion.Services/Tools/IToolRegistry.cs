using Companion.Core;

namespace Companion.Services.Tools;

public interface IToolRegistry
{
    void Register(ITool tool, string? abilityPack = null);
    bool TryGetTool(string name, out ITool? tool);
    IReadOnlyCollection<ToolDefinition> GetAllDefinitions();
}
