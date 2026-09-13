using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Verbs;

internal static class ItemRows
{
    internal static JsonObject Row(in PluginInventoryItem item) => new()
    {
        ["guid"] = Facts.Hex(item.ObjectId),
        ["name"] = item.Name,
        ["objectClass"] = EntityKinds.WordFor(item.ObjectClass),
        ["stackSize"] = item.StackSize,
        ["value"] = item.Value,
        ["burden"] = item.Burden,
        ["container"] = item.ContainerObjectId == 0u ? null : Facts.Hex(item.ContainerObjectId),
        ["equipped"] = item.IsEquipped,
    };

    internal static JsonArray Rows(IEnumerable<PluginInventoryItem> items)
    {
        var rows = new JsonArray();
        foreach (PluginInventoryItem item in items)
            rows.Add(Row(item));
        return rows;
    }
}
