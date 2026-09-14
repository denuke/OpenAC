using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp.Tools;

/// <summary>Changes the settings another plugin shares, through one configure line.</summary>
internal sealed class ConfigureTool(AgentContext context) : IMcpTool
{
    public string Name => "configure";

    public JsonObject Definition() => ToolDefinitions.Make(
        Name,
        "Change a plugin's settings",
        "Changes the settings another plugin shares, such as MossTank's, with one JSON object laid out as the "
        + "settings tool's howToChange says. For MossTank: {\"options\":{\"EnableCombat\":true,\"AttackDistance\":0.1}}, "
        + "{\"monsters\":{\"set\":[{\"name\":\"Drudge Skulker\",\"priority\":3,\"actions\":[\"Attack\"],\"damage\":\"Fire\"}]}}, "
        + "{\"items\":{\"add\":[\"Heavy Crossbow\"]}}, {\"buffs\":{\"addSpells\":[\"Strength Self VII\"]}} or "
        + "{\"macro\":{\"running\":true}}, alone or together. A change wrong in any part changes nothing and is refused "
        + "with every reason; an applied change answers with the parts it touched as the plugin now holds them. "
        + "Settings decide how a plugin makes the character act, so starting MossTank's macro can start a fight.",
        new JsonObject
        {
            ["plugin"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A plugin's name or settings id, such as mosstank.",
            },
            ["change"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The change, laid out as the plugin describes.",
            },
        },
        ["plugin", "change"],
        readOnly: false);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        string? plugin = ToolArguments.Text(arguments, "plugin")?.Trim();
        if (string.IsNullOrEmpty(plugin)
            || plugin.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return new FinishedRun(ToolResults.Error("plugin must be one word, such as mosstank"));
        }
        if (arguments["change"] is not JsonObject change)
            return new FinishedRun(ToolResults.Error("change must be one JSON object"));
        LineRecords records = ToolLines.Deliver(context, $"configure {plugin} {change.ToJsonString()}");
        return ToolLines.Settle(context, records, 0d);
    }
}
