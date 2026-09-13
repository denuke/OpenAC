using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>loot list</c>, <c>loot corpses [range]</c> and <c>loot &lt;item id&gt;</c>, which
/// takes an item from the container that is open.
/// </summary>
internal sealed class LootVerbs : IVerbFamily
{
    internal const float DefaultCorpseRangeMeters = 30f;

    /// <summary>Items in the open container shown unless a read asks for more.</summary>
    internal const int DefaultContentRows = 100;

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal LootVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["loot"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        string[] words = line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return Refuse(line, "usage: loot list, loot corpses [range], or loot <item id>");
        return words[0].ToLowerInvariant() switch
        {
            "list" => List(line, words),
            "corpses" => Corpses(line, words),
            _ => Take(line, words[0]),
        };
    }

    private VerbResult List(CommandLine line, string[] words)
    {
        if (!RowLimits.TrySplit(string.Join(' ', words.Skip(1)), DefaultContentRows, out _, out int limit))
            return Refuse(line, RowLimits.Problem);
        ILootAutomation loot = _host.Automation.Loot;
        uint container = loot.CurrentContainerId;
        if (container == 0u)
            return Refuse(line, "no container is open; 'open <container id>' first");
        IReadOnlyList<PluginInventoryItem> contents = loot.CaptureCurrentContents();
        JsonArray items = ItemRows.Rows(contents.Take(limit));
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["container"] = Facts.Hex(container),
        };
        RowLimits.Count(fields, contents.Count, items.Count);
        fields["items"] = items;
        _publisher.Publish(RecordKinds.ContainerContents, fields);
        return VerbResult.Handled;
    }

    private VerbResult Corpses(CommandLine line, string[] words)
    {
        float range = DefaultCorpseRangeMeters;
        if (words.Length > 1
            && (!float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out range)
                || !(range > 0f)))
        {
            return Refuse(line, "usage: loot corpses [range in meters]");
        }
        var corpses = new JsonArray();
        foreach (PluginLootContainer corpse in _host.Automation.Loot.CaptureCorpses(range))
        {
            corpses.Add(new JsonObject
            {
                ["guid"] = Facts.Hex(corpse.ObjectId),
                ["name"] = corpse.Name,
                ["distance"] = Math.Round(corpse.Distance, 1),
                ["opened"] = corpse.HasBeenOpened,
                ["open"] = corpse.IsCurrent,
                ["rare"] = corpse.IsGeneratedRare,
            });
        }
        _publisher.Publish(RecordKinds.Corpses, new JsonObject
        {
            ["id"] = line.Id,
            ["range"] = range,
            ["corpses"] = corpses,
        });
        return VerbResult.Handled;
    }

    private VerbResult Take(CommandLine line, string text)
    {
        IAutomationSurface automation = _host.Automation;
        if (!Guids.TryParse(text, out uint id))
            return Refuse(line, "usage: loot list, loot corpses [range], or loot <item id>");
        uint container = automation.Loot.CurrentContainerId;
        if (container == 0u)
            return Refuse(line, "no container is open; 'open <container id>' first");
        long baseline = automation.Items.LastInventoryCompletion.Revision;
        PluginItemCommandResult result = automation.Loot.Pickup(id);
        if (!result.Accepted)
        {
            return Refuse(line,
                result.Notice ?? $"the client did not take it ({WireNames.Kebab(result.Status.ToString())})",
                WireNames.Kebab(result.Status.ToString()));
        }

        _publisher.Publish(RecordKinds.InventoryAction, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(id),
            ["container"] = Facts.Hex(container),
        });
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.InventoryOutcome, InventoryOutcomes.WindowSeconds,
            () => InventoryAnswered(automation, baseline, id));
        return VerbResult.Handled;
    }

    /// <summary>The server's answer to an inventory request about one item, if it has arrived.</summary>
    internal static Resolution? InventoryAnswered(IAutomationSurface automation, long baseline, uint id)
    {
        PluginInventoryCompletion completion = automation.Items.LastInventoryCompletion;
        if (completion.Revision <= baseline || completion.SourceObjectId != id)
            return null;
        var fields = new JsonObject { ["weenieError"] = completion.WeenieError };
        return completion.WeenieError == 0u
            ? new Resolution(InventoryOutcomes.Completed, null, fields)
            : new Resolution(InventoryOutcomes.Refused,
                $"the server refused it with error 0x{completion.WeenieError:X4}",
                fields);
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
