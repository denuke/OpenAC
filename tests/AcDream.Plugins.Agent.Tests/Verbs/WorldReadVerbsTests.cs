using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class WorldReadVerbsTests
{
    private const uint Self = 0x50000001u;

    [Fact]
    public void NearbyListsLandscapeObjectsNearestFirstWithoutSelfOrCarriedThings()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(Self, "Tester", PluginObjectClass.Player, 0d, 0d));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000002u, "Shopkeeper", PluginObjectClass.Vendor, 0d, 0.05));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Drudge Skulker", PluginObjectClass.Monster, 0.01, 0d));
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x50000099u, 1u, "Pack Item", PluginObjectClass.Misc, 8u, Self, 0u) { IsOwned = true });
        host.FakeAutomation.FakeObjects.Add(Placed(0x50000098u, "Wielded Sword", PluginObjectClass.MeleeWeapon, 0.001, 0d) with
        {
            IsLandscape = false,
            IsOwned = true,
            WielderObjectId = Self,
        });

        Assert.Equal("handled", verbs.Handle(Line("nearby")).Outcome);

        JsonElement nearby = Latest(ring, RecordKinds.Nearby);
        JsonElement[] rows = nearby.GetProperty("entities").EnumerateArray().ToArray();
        Assert.Equal(["Drudge Skulker", "Shopkeeper"], rows.Select(row => row.GetProperty("name").GetString()));
        Assert.Equal(2.4, rows[0].GetProperty("distance").GetDouble());
        Assert.Equal(0d, rows[0].GetProperty("bearing").GetDouble());
        Assert.Equal(12d, rows[1].GetProperty("distance").GetDouble());
        Assert.Equal(90d, rows[1].GetProperty("bearing").GetDouble());
        Assert.Equal("monster", rows[0].GetProperty("objectClass").GetString());
    }

    [Fact]
    public void NearbyFiltersByKindAndRangeButCountsEveryKindInRange()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Drudge Skulker", PluginObjectClass.Monster, 0.01, 0d));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000002u, "Shopkeeper", PluginObjectClass.Vendor, 0d, 0.02));
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000003u, "Far Drudge", PluginObjectClass.Monster, 1d, 0d));

        verbs.Handle(Line("nearby monster 50"));

        JsonElement nearby = Latest(ring, RecordKinds.Nearby);
        Assert.Equal(1, nearby.GetProperty("matched").GetInt32());
        Assert.Equal("Drudge Skulker", nearby.GetProperty("entities")[0].GetProperty("name").GetString());
        Assert.Equal(1, nearby.GetProperty("kinds").GetProperty("vendor").GetInt32());
        Assert.Equal(1, nearby.GetProperty("kinds").GetProperty("monster").GetInt32());
        Assert.Equal(50d, nearby.GetProperty("filter").GetProperty("range").GetDouble());
    }

    [Fact]
    public void AWordThatIsNeitherAKindNorARangeIsRefusedListingTheKinds()
    {
        var (_, verbs, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("nearby dragons")).Outcome);

        JsonElement refusal = Latest(ring, RecordKinds.NearbyRefused);
        Assert.Contains(
            "monster",
            refusal.GetProperty("kinds").EnumerateArray().Select(word => word.GetString()));
    }

    [Fact]
    public void NearbyWithoutAPositionForTheBodyIsRefused()
    {
        var (host, verbs, _) = Build();
        host.FakeAutomation.FakeNavigation.Snapshot = default;

        Assert.Equal("refused", verbs.Handle(Line("nearby")).Outcome);
    }

    [Fact]
    public void InspectRefusesAnIdTheClientDoesNotHold()
    {
        var (_, verbs, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line("inspect 0x70000009")).Outcome);
        Assert.Equal("inspect", Latest(ring, RecordKinds.EntityRefused).GetProperty("verb").GetString());
    }

    [Fact]
    public void InspectRefusesTextThatIsNotAnId()
    {
        var (_, verbs, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line("inspect the drudge")).Outcome);
    }

    [Fact]
    public void InspectStatesPropertiesByIdAndItsDistance()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Drudge Skulker", PluginObjectClass.Monster, 0.01, 0d) with
        {
            HasAppraisalData = true,
        });
        host.FakeAutomation.FakeObjects.Properties[0x70000001u] = Properties(ints: new() { [25u] = 12 });

        verbs.Handle(Line("inspect 0x70000001"));

        JsonElement inspected = Latest(ring, RecordKinds.EntityInspected);
        Assert.Equal("0x70000001", inspected.GetProperty("guid").GetString());
        Assert.True(inspected.GetProperty("appraised").GetBoolean());
        Assert.Equal(2.4, inspected.GetProperty("distance").GetProperty("value").GetDouble());
        Assert.Equal(12, inspected.GetProperty("properties").GetProperty("value")
            .GetProperty("ints").GetProperty("25").GetInt32());
    }

    [Fact]
    public void InspectOfACarriedItemHasNoPositionAndSaysWhy()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x50000099u, 1u, "Pack Item", PluginObjectClass.Misc, 8u, Self, 0u) { IsOwned = true });
        host.FakeAutomation.FakeObjects.Properties[0x50000099u] = Properties();

        verbs.Handle(Line("inspect 0x50000099"));

        JsonElement inspected = Latest(ring, RecordKinds.EntityInspected);
        Assert.Equal("unknown", inspected.GetProperty("position").GetProperty("presence").GetString());
        Assert.Contains("carried", inspected.GetProperty("position").GetProperty("because").GetString());
        Assert.Equal("unknown", inspected.GetProperty("properties").GetProperty("presence").GetString());
    }

    private static PluginWorldObject Placed(
        uint id,
        string name,
        PluginObjectClass objectClass,
        double northSouth,
        double eastWest) =>
        new(id, 1u, name, objectClass, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0xA9B40021u, eastWest, northSouth, 0d, 0f, true),
        };

    private static PluginItemProperties Properties(Dictionary<uint, int>? ints = null) =>
        new(
            ints ?? new Dictionary<uint, int>(),
            new Dictionary<uint, long>(),
            new Dictionary<uint, bool>(),
            new Dictionary<uint, double>(),
            new Dictionary<uint, string>(),
            new Dictionary<uint, uint>(),
            new Dictionary<uint, uint>());

    private static CommandLine Line(string text) => CommandLine.Parse(7, text, "mcp");

    private static JsonElement Latest(RecordRing ring, string kind) =>
        JsonDocument.Parse(ring.Read(-1, new HashSet<string> { kind }).Records.Last().Json).RootElement;

    private static (FakePluginHost Host, WorldReadVerbs Verbs, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = new PluginNavigationSnapshot(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: Self,
            Position: new PluginNavigationPosition(0xA9B40021u, 0d, 0d, 0d, 0f, true),
            IsMoving: false,
            IsAirborne: false);
        var ring = new RecordRing(32);
        return (host, new WorldReadVerbs(host, new Publisher(new AgentClock(), ring)), ring);
    }
}
