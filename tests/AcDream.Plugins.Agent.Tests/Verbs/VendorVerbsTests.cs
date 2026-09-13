using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class VendorVerbsTests
{
    private const uint Self = 0x50000001u;
    private const uint Shopkeeper = 0x70000010u;
    private const uint TaperListing = 0x70000011u;
    private const uint Scarab = 0x50000300u;

    [Fact]
    public void VendorListsTheOpenVendorsStock()
    {
        var (host, verbs, _, _, ring) = Build(vendorOpen: true);

        verbs.Handle(Line("vendor"));

        JsonElement vendor = Records(ring, RecordKinds.Vendor).Single();
        Assert.True(vendor.GetProperty("open").GetBoolean());
        Assert.Equal("Shopkeeper", vendor.GetProperty("vendor").GetProperty("value").GetProperty("name").GetString());
        JsonElement taper = vendor.GetProperty("items").GetProperty("value")[0];
        Assert.Equal("Prismatic Taper", taper.GetProperty("name").GetString());
        Assert.True(taper.GetProperty("unlimited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, taper.GetProperty("stock").ValueKind);
    }

    [Fact]
    public void WithNoVendorOpenTheStockIsUnknownNotEmpty()
    {
        var (_, verbs, _, _, ring) = Build(vendorOpen: false);

        verbs.Handle(Line("vendor"));

        JsonElement vendor = Records(ring, RecordKinds.Vendor).Single();
        Assert.False(vendor.GetProperty("open").GetBoolean());
        Assert.Equal("unknown", vendor.GetProperty("items").GetProperty("presence").GetString());
    }

    [Fact]
    public void BuyingByNameCompletesWhenTheItemsArriveInThePack()
    {
        var (host, verbs, correlator, clock, ring) = Build(vendorOpen: true);

        Assert.Equal("handled", verbs.Handle(Line("buy prismatic taper 10")).Outcome);
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.InventoryOutcome));

        host.FakeAutomation.FakeItems.Owned.Add(Item(0x50000400u, 691u, "Prismatic Taper", stack: 10));
        clock.Advance(VendorVerbs.CheckSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"buy:{TaperListing:X8}:10"], host.FakeAutomation.FakeItems.Calls);
        JsonElement action = Records(ring, RecordKinds.InventoryAction).Single();
        Assert.Equal(50, action.GetProperty("price").GetProperty("value").GetInt32());
        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void APurchaseThatNeverArrivesIsUnconfirmed()
    {
        var (_, verbs, correlator, clock, ring) = Build(vendorOpen: true);

        verbs.Handle(Line("buy prismatic taper"));
        clock.Advance(InventoryOutcomes.WindowSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("unconfirmed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void BuyingWithNoVendorOpenIsRefused()
    {
        var (host, verbs, _, _, _) = Build(vendorOpen: false);

        Assert.Equal("refused", verbs.Handle(Line("buy prismatic taper")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
    }

    [Fact]
    public void AnAmbiguousListingIsRefusedWithCandidates()
    {
        var (host, verbs, _, _, ring) = Build(vendorOpen: true);
        host.FakeAutomation.FakeItems.Stock.Add(new PluginVendorItem(0x70000012u, 692u, "Prismatic Taper Bundle", 1u, 45, -1));

        Assert.Equal("refused", verbs.Handle(Line("buy taper")).Outcome);

        JsonElement refusal = Records(ring, RecordKinds.VendorRefused).Single();
        Assert.Equal("ambiguous", refusal.GetProperty("word").GetString());
        Assert.Equal(2, refusal.GetProperty("candidates").GetArrayLength());
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
    }

    [Fact]
    public void ABuyTheClientDeclinesIsRefusedWithItsWord()
    {
        var (host, verbs, _, _, ring) = Build(vendorOpen: true);
        host.FakeAutomation.FakeItems.NextStatus = PluginItemCommandStatus.Busy;

        Assert.Equal("refused", verbs.Handle(Line("buy prismatic taper")).Outcome);
        Assert.Equal("busy", Records(ring, RecordKinds.VendorRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void SellingCompletesWhenTheItemLeavesThePack()
    {
        var (host, verbs, correlator, clock, ring) = Build(vendorOpen: true);
        host.FakeAutomation.FakeItems.Owned.Add(Item(Scarab, 400u, "Lead Scarab", stack: 1));

        Assert.Equal("handled", verbs.Handle(Line($"sell 0x{Scarab:X8}")).Outcome);
        host.FakeAutomation.FakeItems.Owned.Clear();
        clock.Advance(VendorVerbs.CheckSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"sell:{Scarab:X8}:0"], host.FakeAutomation.FakeItems.Calls);
        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void SellingPartOfAStackCompletesWhenTheStackShrinks()
    {
        var (host, verbs, correlator, clock, ring) = Build(vendorOpen: true);
        host.FakeAutomation.FakeItems.Owned.Add(Item(Scarab, 400u, "Lead Scarab", stack: 10));

        verbs.Handle(Line($"sell 0x{Scarab:X8} 4"));
        host.FakeAutomation.FakeItems.Owned[0] = Item(Scarab, 400u, "Lead Scarab", stack: 8);
        clock.Advance(VendorVerbs.CheckSeconds);
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.InventoryOutcome));

        host.FakeAutomation.FakeItems.Owned[0] = Item(Scarab, 400u, "Lead Scarab", stack: 6);
        clock.Advance(VendorVerbs.CheckSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void SellingSomethingNotCarriedIsRefused()
    {
        var (host, verbs, _, _, _) = Build(vendorOpen: true);

        Assert.Equal("refused", verbs.Handle(Line($"sell 0x{Scarab:X8}")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
    }

    private static PluginInventoryItem Item(uint id, uint weenieClassId, string name, int stack) =>
        new(id, weenieClassId, name, 1u, Self, 0u, 0u, 0u, 0u, 0u, 0u,
            stack, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static CommandLine Line(string text) => CommandLine.Parse(50, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, VendorVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build(bool vendorOpen)
    {
        var host = new FakePluginHost();
        if (vendorOpen)
        {
            host.FakeAutomation.FakeItems.ActiveVendorObjectId = Shopkeeper;
            host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
                Shopkeeper, 1u, "Shopkeeper", PluginObjectClass.Vendor, 16u, 0u, 0u));
            host.FakeAutomation.FakeItems.Stock.Add(new PluginVendorItem(TaperListing, 691u, "Prismatic Taper", 1u, 5, -1)
            {
                PluralName = "Prismatic Tapers",
            });
        }
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new VendorVerbs(host, publisher, correlator, clock), correlator, clock, ring);
    }
}
