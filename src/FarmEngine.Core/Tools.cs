using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Tool rules — extracted from src/lib/tools.ts, behavior-identical
/// (characterization-tested). Port of tools.ts.
/// </summary>
public static class Tools
{
    /// <summary>TS <c>TOOL_DEFINITIONS[toolType]</c>; unknown types yield <c>undefined</c> (null) like TS.</summary>
    public static ToolDefinition GetToolDefinition(string toolType) =>
        ContentBuiltin.ToolDefinitions.TryGetValue(toolType, out var definition) ? definition : null!;

    public static bool CanUseTool(Item tool, Tile targetTile)
    {
        if (string.IsNullOrEmpty(tool.ToolType)) return false;
        if (!ContentBuiltin.ToolDefinitions.TryGetValue(tool.ToolType, out var definition) || definition == null) return false;
        return definition.ValidTargets.Contains(targetTile.Type);
    }

    public static ToolDefinition? GetToolFromItem(Item item)
    {
        if (string.IsNullOrEmpty(item.ToolType)) return null;
        return ContentBuiltin.ToolDefinitions.TryGetValue(item.ToolType, out var definition) ? definition : null;
    }

    /// <summary>JS falsiness of an optional number (undefined, 0, NaN).</summary>
    private static bool Falsy(double? value) => value is not { } v || v == 0 || double.IsNaN(v);

    public static Item DamageToolDurability(Item tool, double amount = 1)
    {
        if (Falsy(tool.Durability) || Falsy(tool.MaxDurability)) return tool;
        return tool with { Durability = Math.Max(0, tool.Durability!.Value - amount) };
    }

    public static bool IsToolBroken(Item tool)
    {
        // M2 fix: a tool at exactly 0 durability IS broken (the old falsy check
        // meant tools could never break; repair shops make breakage meaningful).
        if (tool.Durability == null || tool.MaxDurability == null) return false;
        return tool.Durability.Value <= 0;
    }

    public static Item RepairTool(Item tool, double? amount = null)
    {
        if (Falsy(tool.Durability) || Falsy(tool.MaxDurability)) return tool;
        var repairAmount = Falsy(amount) ? tool.MaxDurability!.Value : amount!.Value;
        return tool with { Durability = Math.Min(tool.MaxDurability!.Value, tool.Durability!.Value + repairAmount) };
    }
}
