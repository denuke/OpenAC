using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>vendor</c>, <c>buy &lt;listing&gt; [quantity]</c> and <c>sell &lt;item id&gt; [amount]</c>
/// with the vendor that is open. A purchase completes when the items arrive in
/// the pack, and a sale when the item leaves it, since the client gets no
/// separate answer for either.
/// </summary>
internal sealed class VendorVerbs : IVerbFamily
{
    internal const double CheckSeconds = 0.5d;
    private const string NoVendor = "no vendor is open; 'use <vendor id>' opens one";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;
    private readonly AgentClock _clock;

    internal VendorVerbs(
        IPluginHost host,
        Publisher publisher,
        OutcomeCorrelator outcomes,
        AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(clock);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
        _clock = clock;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["vendor", "buy", "sell"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        return line.Verb switch
        {
            "vendor" => Listing(line),
            "buy" => Buy(line),
            _ => Sell(line),
        };
    }

    private VerbResult Listing(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        uint vendor = automation.Items.ActiveVendorObjectId;
        if (vendor == 0u)
        {
            _publisher.Publish(RecordKinds.Vendor, new JsonObject
            {
                ["id"] = line.Id,
                ["open"] = false,
                ["vendor"] = Facts.Unknown(NoVendor),
                ["items"] = Facts.Unknown(NoVendor),
            });
            return VerbResult.Handled;
        }

        var items = new JsonArray();
        foreach (PluginVendorItem listing in automation.Items.CaptureVendorStock())
        {
            items.Add(new JsonObject
            {
                ["guid"] = Facts.Hex(listing.ObjectId),
                ["name"] = listing.Name,
                ["pluralName"] = listing.PluralName.Length == 0 ? null : listing.PluralName,
                ["unitPrice"] = listing.UnitPrice,
                ["stock"] = listing.IsUnlimited ? null : listing.StockCount,
                ["unlimited"] = listing.IsUnlimited,
            });
        }
        _publisher.Publish(RecordKinds.Vendor, new JsonObject
        {
            ["id"] = line.Id,
            ["open"] = true,
            ["vendor"] = Facts.Observed(Facts.Id(
                vendor,
                automation.Objects.TryGet(vendor, out PluginWorldObject value) ? value.Name : null)),
            ["items"] = Facts.Observed(items),
        });
        return VerbResult.Handled;
    }

    private VerbResult Buy(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        (string text, uint quantity, bool quantityValid) = SplitQuantity(line.Arguments, defaultQuantity: 1u);
        if (text.Length == 0 || !quantityValid)
            return Refuse(line, "usage: buy <listing id or name> [quantity]");
        if (automation.Items.ActiveVendorObjectId == 0u)
            return Refuse(line, NoVendor);

        IReadOnlyList<PluginVendorItem> stock = automation.Items.CaptureVendorStock();
        PluginVendorItem[] matches = Guids.TryParse(text, out uint listingId)
            ? stock.Where(listing => listing.ObjectId == listingId).ToArray()
            : Match(stock, text);
        if (matches.Length == 0)
            return Refuse(line, $"the open vendor sells nothing matching '{text}'");
        if (matches.Length > 1)
        {
            var candidates = new JsonArray();
            foreach (PluginVendorItem candidate in matches.Take(10))
                candidates.Add(new JsonObject { ["guid"] = Facts.Hex(candidate.ObjectId), ["name"] = candidate.Name });
            return Refuse(line, $"which one? {matches.Length} listings match '{text}'", "ambiguous", candidates);
        }

        PluginVendorItem chosen = matches[0];
        int before = Carried(automation, chosen);
        PluginItemCommandResult result = automation.Items.Buy(chosen.ObjectId, quantity);
        if (!result.Accepted)
            return RefuseStatus(line, result);

        _publisher.Publish(RecordKinds.InventoryAction, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(chosen.ObjectId),
            ["name"] = chosen.Name,
            ["amount"] = quantity,
            ["unitPrice"] = chosen.UnitPrice,
            ["price"] = Facts.Derived(
                (long)chosen.UnitPrice * quantity,
                "unit price and amount",
                "unit price times amount"),
        });
        Watch(line, () =>
        {
            int carried = Carried(automation, chosen);
            return carried >= before + quantity
                ? new Resolution(InventoryOutcomes.Completed, "the items arrived in the pack",
                    new JsonObject { ["carried"] = carried })
                : null;
        });
        return VerbResult.Handled;
    }

    private VerbResult Sell(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        (string text, uint amount, bool amountValid) = SplitQuantity(line.Arguments, defaultQuantity: 0u);
        if (!amountValid || !Guids.TryParse(text, out uint id))
            return Refuse(line, "usage: sell <item id> [amount]");
        if (automation.Items.ActiveVendorObjectId == 0u)
            return Refuse(line, NoVendor);
        PluginInventoryItem item = automation.Items.CaptureOwnedItems()
            .FirstOrDefault(owned => owned.ObjectId == id);
        if (item.ObjectId == 0u)
            return Refuse(line, "the character does not carry that item");

        int before = Math.Max(1, item.StackSize);
        int leaving = amount == 0u ? before : (int)Math.Min(amount, (uint)before);
        PluginItemCommandResult result = automation.Items.Sell(id, amount);
        if (!result.Accepted)
            return RefuseStatus(line, result);

        _publisher.Publish(RecordKinds.InventoryAction, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(id),
            ["name"] = item.Name,
            ["amount"] = leaving,
        });
        Watch(line, () =>
        {
            PluginInventoryItem now = automation.Items.CaptureOwnedItems()
                .FirstOrDefault(owned => owned.ObjectId == id);
            if (now.ObjectId == 0u)
                return new Resolution(InventoryOutcomes.Completed, "the item left the pack");
            return Math.Max(1, now.StackSize) <= before - leaving
                ? new Resolution(InventoryOutcomes.Completed, "the stack shrank")
                : null;
        });
        return VerbResult.Handled;
    }

    /// <summary>Checks for the effect every half second rather than on every frame.</summary>
    private void Watch(CommandLine line, Func<Resolution?> effect)
    {
        double next = _clock.Now;
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.InventoryOutcome, InventoryOutcomes.WindowSeconds, () =>
        {
            if (_clock.Now < next)
                return null;
            next = _clock.Now + CheckSeconds;
            return effect();
        });
    }

    private static int Carried(IAutomationSurface automation, PluginVendorItem listing) =>
        automation.Items.CaptureOwnedItems()
            .Where(item => listing.WeenieClassId != 0u
                ? item.WeenieClassId == listing.WeenieClassId
                : item.Name.Equals(listing.Name, StringComparison.OrdinalIgnoreCase))
            .Sum(item => Math.Max(1, item.StackSize));

    private static PluginVendorItem[] Match(IReadOnlyList<PluginVendorItem> stock, string text)
    {
        PluginVendorItem[] exact = stock
            .Where(listing => listing.Name.Equals(text, StringComparison.OrdinalIgnoreCase)
                || listing.PluralName.Equals(text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length > 0
            ? exact
            : stock.Where(listing => listing.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static (string Text, uint Quantity, bool Valid) SplitQuantity(string arguments, uint defaultQuantity)
    {
        string trimmed = arguments.Trim();
        int space = trimmed.LastIndexOf(' ');
        if (space > 0
            && uint.TryParse(trimmed[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out uint quantity))
        {
            return (trimmed[..space].Trim(), quantity, quantity > 0u);
        }
        return (trimmed, defaultQuantity, true);
    }

    private VerbResult RefuseStatus(CommandLine line, PluginItemCommandResult result)
    {
        string word = WireNames.Kebab(result.Status.ToString());
        return Refuse(line, result.Notice ?? $"the client did not do it ({word})", word);
    }

    private VerbResult Refuse(CommandLine line, string reason, string? word = null, JsonArray? candidates = null)
    {
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["word"] = word,
            ["reason"] = reason,
        };
        if (candidates is not null)
            fields["candidates"] = candidates;
        _publisher.Publish(RecordKinds.VendorRefused, fields);
        return VerbResult.Refused(reason);
    }
}
