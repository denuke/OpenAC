using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// What the character carries and wears, and moving it: <c>inventory [search]</c>,
/// <c>equipment</c>, <c>drop</c>, <c>give</c>, <c>move</c>, <c>equip</c> and
/// <c>unequip</c>.
/// </summary>
internal sealed class InventoryVerbs : IVerbFamily
{
    /// <summary>Carried items shown unless a read asks for more.</summary>
    internal const int DefaultItemRows = 100;

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal InventoryVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } =
        ["inventory", "equipment", "drop", "give", "move", "equip", "unequip"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        return line.Verb switch
        {
            "inventory" => Inventory(line),
            "equipment" => Equipment(line),
            "drop" => Drop(line),
            "give" => Transfer(line, give: true),
            "move" => Transfer(line, give: false),
            "equip" => Equip(line),
            _ => Unequip(line),
        };
    }

    /// <summary>What is carried, narrowed to names containing the search text when one is given.</summary>
    private VerbResult Inventory(CommandLine line)
    {
        if (!RowLimits.TrySplit(line.Arguments, DefaultItemRows, out string rest, out int limit))
            return Refuse(line, RowLimits.Problem);
        string search = rest.Equals("list", StringComparison.OrdinalIgnoreCase) ? string.Empty : rest;
        IAutomationSurface automation = _host.Automation;
        IReadOnlyList<PluginInventoryItem> carried = automation.Items.CaptureOwnedItems();
        PluginInventoryItem[] matching = carried
            .Where(item => search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["search"] = search.Length == 0 ? null : search,
            ["freeMainPackSlots"] = automation.Character.IsInWorld
                ? Facts.Observed(automation.Character.MainPackFreeSlots)
                : Facts.Unknown("no character is in the world"),
            ["carriedCount"] = carried.Count,
        };
        JsonArray items = ItemRows.Rows(matching.Take(limit));
        RowLimits.Count(fields, matching.Length, items.Count);
        fields["items"] = items;
        _publisher.Publish(RecordKinds.Inventory, fields);
        return VerbResult.Handled;
    }

    private VerbResult Equipment(CommandLine line)
    {
        var equipped = new JsonArray();
        var carried = new JsonArray();
        foreach (PluginEquipmentItem item in _host.Automation.Equipment.CaptureOwnedEquipment())
        {
            (item.IsEquipped ? equipped : carried).Add(new JsonObject
            {
                ["guid"] = Facts.Hex(item.ObjectId),
                ["name"] = item.Name,
                ["validLocations"] = item.ValidLocations,
                ["equippedLocation"] = item.IsEquipped ? item.EquippedLocation : null,
                ["stackSize"] = item.StackSize,
            });
        }
        _publisher.Publish(RecordKinds.Equipment, new JsonObject
        {
            ["id"] = line.Id,
            ["equipped"] = equipped,
            ["carried"] = carried,
        });
        return VerbResult.Handled;
    }

    private VerbResult Drop(CommandLine line)
    {
        string[] words = Words(line);
        if (words.Length is < 1 or > 2
            || !Guids.TryParse(words[0], out uint id)
            || !TryAmount(words, 1, out uint amount))
        {
            return Refuse(line, "usage: drop <item id> [amount]");
        }
        IAutomationSurface automation = _host.Automation;
        long baseline = automation.Items.LastInventoryCompletion.Revision;
        return Sent(line, automation.Items.Drop(id, amount), id, null, amount, baseline);
    }

    private VerbResult Transfer(CommandLine line, bool give)
    {
        string[] words = Words(line);
        if (words.Length is < 3 or > 4
            || !Guids.TryParse(words[0], out uint id)
            || !words[1].Equals("to", StringComparison.OrdinalIgnoreCase)
            || !Guids.TryParse(words[2], out uint target)
            || !TryAmount(words, 3, out uint amount))
        {
            return Refuse(line, give
                ? "usage: give <item id> to <object id> [amount]"
                : "usage: move <item id> to <container id> [amount]");
        }
        IAutomationSurface automation = _host.Automation;
        long baseline = automation.Items.LastInventoryCompletion.Revision;
        PluginItemCommandResult result = give
            ? automation.Items.Give(id, target, amount)
            : automation.Items.MoveToContainer(id, target, amount);
        return Sent(line, result, id, target, amount, baseline);
    }

    private VerbResult Equip(CommandLine line)
    {
        if (!Guids.TryParse(line.Arguments, out uint id))
            return Refuse(line, "usage: equip <item id>");
        IAutomationSurface automation = _host.Automation;
        long baseline = automation.Items.LastInventoryCompletion.Revision;
        PluginEquipmentCommandResult result = automation.Equipment.Equip(id);
        if (result.Status == PluginEquipmentCommandStatus.AlreadyEquipped)
        {
            Action(line, id, null, 0u);
            _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.InventoryOutcome,
                new Resolution(InventoryOutcomes.Completed, "it was already equipped"));
            return VerbResult.Handled;
        }
        if (!result.Accepted)
        {
            string word = WireNames.Kebab(result.Status.ToString());
            return Refuse(line, result.Notice ?? $"the client did not equip it ({word})", word);
        }
        Action(line, id, null, 0u);
        Watch(line, automation, baseline, id);
        return VerbResult.Handled;
    }

    private VerbResult Unequip(CommandLine line)
    {
        if (!Guids.TryParse(line.Arguments, out uint id))
            return Refuse(line, "usage: unequip <item id>");
        IAutomationSurface automation = _host.Automation;
        bool equipped = automation.Equipment.CaptureOwnedEquipment()
            .Any(item => item.ObjectId == id && item.IsEquipped);
        if (!equipped)
            return Refuse(line, "that item is not equipped");
        uint pack = automation.Character.ObjectId;
        if (pack == 0u)
            return Refuse(line, "the client has not been told the character's own id");
        long baseline = automation.Items.LastInventoryCompletion.Revision;
        return Sent(line, automation.Items.MoveToContainer(id, pack), id, pack, 0u, baseline);
    }

    private VerbResult Sent(
        CommandLine line,
        PluginItemCommandResult result,
        uint id,
        uint? target,
        uint amount,
        long baseline)
    {
        if (!result.Accepted)
        {
            string word = WireNames.Kebab(result.Status.ToString());
            return Refuse(line, result.Notice ?? $"the client did not do it ({word})", word);
        }
        Action(line, id, target, amount);
        Watch(line, _host.Automation, baseline, id);
        return VerbResult.Handled;
    }

    private void Watch(CommandLine line, IAutomationSurface automation, long baseline, uint id) =>
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.InventoryOutcome, InventoryOutcomes.WindowSeconds,
            () => LootVerbs.InventoryAnswered(automation, baseline, id));

    private void Action(CommandLine line, uint id, uint? target, uint amount) =>
        _publisher.Publish(RecordKinds.InventoryAction, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(id),
            ["target"] = target is { } to ? Facts.Hex(to) : null,
            ["amount"] = amount == 0u ? null : amount,
        });

    private static string[] Words(CommandLine line) =>
        line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool TryAmount(string[] words, int index, out uint amount)
    {
        amount = 0u;
        return words.Length <= index
            || (uint.TryParse(words[index], NumberStyles.None, CultureInfo.InvariantCulture, out amount)
                && amount > 0u);
    }

    private VerbResult Refuse(CommandLine line, string reason, string? word = null)
    {
        _publisher.Publish(RecordKinds.InventoryRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["word"] = word,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
