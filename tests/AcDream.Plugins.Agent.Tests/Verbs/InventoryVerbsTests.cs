using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class InventoryVerbsTests
{
    private const uint Self = 0x50000001u;
    private const uint Sword = 0x50000200u;
    private const uint Scarabs = 0x50000201u;

    [Fact]
    public void InventoryListsWhatIsCarriedAndTheFreeSlots()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeCharacter.MainPackFreeSlots = 12;
        host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(Scarabs, "Lead Scarab", Self));

        verbs.Handle(Line("inventory"));

        JsonElement inventory = Records(ring, RecordKinds.Inventory).Single();
        Assert.Equal(12, inventory.GetProperty("freeMainPackSlots").GetProperty("value").GetInt32());
        Assert.Equal("Lead Scarab", inventory.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void InventoryNarrowsToNamesContainingTheSearchAndCountsEverythingCarried()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(Scarabs, "Lead Scarab", Self));
        host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(0x50000203u, "Iron Scarab", Self));
        host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(0x50000204u, "Mana Stone", Self));

        verbs.Handle(Line("inventory SCARAB"));

        JsonElement inventory = Records(ring, RecordKinds.Inventory).Single();
        Assert.Equal("SCARAB", inventory.GetProperty("search").GetString());
        Assert.Equal(3, inventory.GetProperty("carriedCount").GetInt32());
        Assert.Equal(
            ["Lead Scarab", "Iron Scarab"],
            inventory.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }

    [Fact]
    public void InventoryListIsTheWholeInventory()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeItems.Owned.Add(LootVerbsTests.Item(Scarabs, "Lead Scarab", Self));

        verbs.Handle(Line("inventory list"));

        JsonElement inventory = Records(ring, RecordKinds.Inventory).Single();
        Assert.Equal(JsonValueKind.Null, inventory.GetProperty("search").ValueKind);
        Assert.Single(inventory.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void EquipmentSeparatesWhatIsWornFromWhatIsCarried()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeEquipment.Owned.Add(Equippable(Sword, "Iron Sword", equipped: true));
        host.FakeAutomation.FakeEquipment.Owned.Add(Equippable(0x50000202u, "Spare Shield", equipped: false));

        verbs.Handle(Line("equipment"));

        JsonElement equipment = Records(ring, RecordKinds.Equipment).Single();
        Assert.Equal("Iron Sword", equipment.GetProperty("equipped")[0].GetProperty("name").GetString());
        Assert.Equal("Spare Shield", equipment.GetProperty("carried")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void DropSendsTheAmountAndCompletesOnTheItemsAnswer()
    {
        var (host, verbs, correlator, _, ring) = Build();

        Assert.Equal("handled", verbs.Handle(Line($"drop 0x{Scarabs:X8} 5")).Outcome);
        host.FakeAutomation.FakeItems.LastInventoryCompletion =
            new PluginInventoryCompletion(1, PluginInventoryCommandKind.SplitToWorld, Scarabs, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"drop:{Scarabs:X8}:5"], host.FakeAutomation.FakeItems.Calls);
        Assert.Equal(5, Records(ring, RecordKinds.InventoryAction).Single().GetProperty("amount").GetInt32());
        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("give 0x50000201 0x70000001")]
    [InlineData("give 0x50000201 to")]
    [InlineData("give 0x50000201 to 0x70000001 zero")]
    [InlineData("drop 0x50000201 0")]
    [InlineData("move 0x50000201 into 0x50000300")]
    public void AMalformedTransferIsRefusedAndNothingIsSent(string text)
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
        Assert.Single(Records(ring, RecordKinds.InventoryRefused));
    }

    [Fact]
    public void GiveAndMoveSendToTheirTarget()
    {
        var (host, verbs, _, _, _) = Build();

        verbs.Handle(Line($"give 0x{Scarabs:X8} to 0x70000001 3"));
        verbs.Handle(Line($"move 0x{Scarabs:X8} to 0x50000300"));

        Assert.Equal(
            [$"give:{Scarabs:X8}:70000001:3", $"move:{Scarabs:X8}:50000300:0"],
            host.FakeAutomation.FakeItems.Calls);
    }

    [Fact]
    public void EquippingSomethingAlreadyWornCompletesAtOnce()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeEquipment.NextStatus = PluginEquipmentCommandStatus.AlreadyEquipped;

        verbs.Handle(Line($"equip 0x{Sword:X8}"));

        JsonElement outcome = Records(ring, RecordKinds.InventoryOutcome).Single();
        Assert.Equal("completed", outcome.GetProperty("outcome").GetString());
        Assert.Equal("it was already equipped", outcome.GetProperty("reason").GetString());
    }

    [Fact]
    public void EquippingCompletesOnTheItemsAnswer()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line($"equip 0x{Sword:X8}"));
        correlator.Tick(inWorld: true);
        Assert.Empty(Records(ring, RecordKinds.InventoryOutcome));
        host.FakeAutomation.FakeItems.LastInventoryCompletion =
            new PluginInventoryCompletion(1, PluginInventoryCommandKind.Wield, Sword, 0u);
        correlator.Tick(inWorld: true);

        Assert.Equal([$"equip:{Sword:X8}"], host.FakeAutomation.FakeEquipment.Calls);
        Assert.Equal("completed", Records(ring, RecordKinds.InventoryOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AnEquipTheClientDeclinesIsRefusedWithItsWord()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeEquipment.NextStatus = PluginEquipmentCommandStatus.Busy;

        Assert.Equal("refused", verbs.Handle(Line($"equip 0x{Sword:X8}")).Outcome);
        Assert.Equal("busy", Records(ring, RecordKinds.InventoryRefused).Single().GetProperty("word").GetString());
    }

    [Fact]
    public void UnequippingMovesTheItemIntoTheCharactersPack()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeEquipment.Owned.Add(Equippable(Sword, "Iron Sword", equipped: true));

        Assert.Equal("handled", verbs.Handle(Line($"unequip 0x{Sword:X8}")).Outcome);
        Assert.Equal([$"move:{Sword:X8}:{Self:X8}:0"], host.FakeAutomation.FakeItems.Calls);
    }

    [Fact]
    public void UnequippingSomethingNotWornIsRefused()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeEquipment.Owned.Add(Equippable(Sword, "Iron Sword", equipped: false));

        Assert.Equal("refused", verbs.Handle(Line($"unequip 0x{Sword:X8}")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeItems.Calls);
    }

    private static PluginEquipmentItem Equippable(uint id, string name, bool equipped) =>
        new(id, name, 1u, 0x1u, equipped ? 0x1u : 0u, equipped ? 0u : Self, equipped ? Self : 0u,
            1, 0, 0, 0, 0d);

    private static CommandLine Line(string text) => CommandLine.Parse(40, text, "mcp");

    private static IEnumerable<JsonElement> Records(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, InventoryVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.ObjectId = Self;
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new InventoryVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
