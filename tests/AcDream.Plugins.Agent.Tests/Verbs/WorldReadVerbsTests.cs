using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;
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

    [Fact]
    public void NearbyCarriesASightVerdictPerProjectileKindAimedAtItsOwnHeight()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Drudge Skulker", PluginObjectClass.Monster, 0.01, 0d));
        FakeProjectiles projectiles = host.FakeAutomation.FakeProjectiles;
        projectiles.Results[(0x70000001u, PluginProjectilePathKind.Straight)] =
            new(PluginProjectilePathStatus.Blocked, 12, 0x7A000001u);
        projectiles.Results[(0x70000001u, PluginProjectilePathKind.Missile)] = new(PluginProjectilePathStatus.Blocked, 9);

        verbs.Handle(Line("nearby"));

        JsonElement sight = Latest(ring, RecordKinds.Nearby).GetProperty("entities")[0].GetProperty("sight");
        Assert.Equal("visible", sight.GetProperty("arc").GetString());
        Assert.Equal("blocked", sight.GetProperty("bolt").GetString());
        Assert.Equal("blocked", sight.GetProperty("arrow").GetString());
        Assert.Equal("0x7A000001", sight.GetProperty("blockedBy").GetProperty("bolt").GetString());
        Assert.Equal(Sight.Geometry, sight.GetProperty("blockedBy").GetProperty("arrow").GetString());
        Assert.False(sight.GetProperty("blockedBy").TryGetProperty("arc", out _));
        Assert.Equal(JsonValueKind.Null, sight.GetProperty("because").ValueKind);
        Assert.Equal(
            [
                (0x70000001u, PluginProjectilePathKind.Arc, PluginAttackHeight.High),
                (0x70000001u, PluginProjectilePathKind.Straight, PluginAttackHeight.Medium),
                (0x70000001u, PluginProjectilePathKind.Missile, PluginAttackHeight.Low),
            ],
            projectiles.Traced);
    }

    [Fact]
    public void ASightTheClientCannotTraceIsCannotSayWithTheReasonAndNeverVisible()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Drudge Skulker", PluginObjectClass.Monster, 0.01, 0d));
        foreach (PluginProjectilePathKind kind in Enum.GetValues<PluginProjectilePathKind>())
            host.FakeAutomation.FakeProjectiles.Results[(0x70000001u, kind)] = new(PluginProjectilePathStatus.Unavailable);

        verbs.Handle(Line("nearby"));

        JsonElement sight = Latest(ring, RecordKinds.Nearby).GetProperty("entities")[0].GetProperty("sight");
        foreach (string kind in new[] { "arc", "bolt", "arrow" })
            Assert.Equal("cannot-say", sight.GetProperty(kind).GetString());
        Assert.Contains("cannot trace", sight.GetProperty("because").GetString());
    }

    [Fact]
    public void OnlyTheNearestObjectsInRangeAreTracedAndTheRestSayWhy()
    {
        var (host, verbs, ring) = Build();
        for (uint index = 0; index < Sight.TracedObjects + 2; index++)
        {
            host.FakeAutomation.FakeObjects.Add(
                Placed(0x70000100u + index, $"Drudge {index}", PluginObjectClass.Monster, 0.001 * (index + 1), 0d));
        }

        verbs.Handle(Line("nearby"));

        JsonElement[] rows = [.. Latest(ring, RecordKinds.Nearby).GetProperty("entities").EnumerateArray()];
        Assert.Equal(Sight.TracedObjects * Sight.Kinds.Count, host.FakeAutomation.FakeProjectiles.Traced.Count);
        Assert.Equal("visible", rows[0].GetProperty("sight").GetProperty("bolt").GetString());
        JsonElement untraced = rows[Sight.TracedObjects].GetProperty("sight");
        Assert.Equal("cannot-say", untraced.GetProperty("bolt").GetString());
        Assert.Contains("inspect", untraced.GetProperty("because").GetString());
    }

    [Fact]
    public void InspectCarriesASightVerdictPerProjectileKind()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeObjects.Add(Placed(0x70000001u, "Shopkeeper", PluginObjectClass.Vendor, 0.01, 0d));
        host.FakeAutomation.FakeProjectiles.Results[(0x70000001u, PluginProjectilePathKind.Arc)] =
            new(PluginProjectilePathStatus.Blocked);

        verbs.Handle(Line("inspect 0x70000001"));

        JsonElement sight = Latest(ring, RecordKinds.EntityInspected).GetProperty("sight");
        Assert.Equal("blocked", sight.GetProperty("arc").GetString());
        Assert.Equal("visible", sight.GetProperty("bolt").GetString());
        Assert.Equal("visible", sight.GetProperty("arrow").GetString());
    }

    [Fact]
    public void ExploreListsPlacesByKindUnvisitedFirstAndMarksWhereTheCharacterHasBeen()
    {
        var visited = new VisitedGround();
        var (host, verbs, ring) = Build(visited);
        visited.Note(new PluginNavigationPosition(0u, 0.1d, 0d, 0d, 0f, true));
        host.FakeAutomation.FakeNavigation.PlacesReport = new PluginPlacesReport(
            PluginPlacesState.Ready,
            [
                new PluginNavigationPlace(new PluginNavigationPosition(0u, 0.1d, 0d, 0d, 0f, true), 26f, PluginPlaceKind.Room, 144f, 11f, 0f, 1),
                new PluginNavigationPlace(new PluginNavigationPosition(0u, 0d, 0.2d, 0d, 0f, true), 50f, PluginPlaceKind.Passage, 30f, 2f, 2.5f, 3),
            ],
            InDungeon: true,
            "found")
        {
            From = new PluginNavigationPosition(0u, 0d, 0d, 0d, 0f, true),
        };

        Assert.Equal("handled", verbs.Handle(Line("explore")).Outcome);

        JsonElement explore = Latest(ring, RecordKinds.Explore);
        Assert.Equal("ready", explore.GetProperty("state").GetString());
        Assert.Equal("dungeon", explore.GetProperty("region").GetString());
        Assert.Equal(1, explore.GetProperty("visited").GetInt32());
        Assert.Equal(1, explore.GetProperty("kinds").GetProperty("room").GetInt32());
        JsonElement[] places = [.. explore.GetProperty("places").EnumerateArray()];
        Assert.Equal(["passage", "room"], places.Select(place => place.GetProperty("kind").GetString()));
        Assert.Equal([false, true], places.Select(place => place.GetProperty("visited").GetBoolean()));
        Assert.Equal("go to 0.2 0 0", places[0].GetProperty("go").GetString());
        Assert.Equal(["junction", "stairs or ramp"], places[0].GetProperty("notes").EnumerateArray().Select(note => note.GetString()));
        Assert.Equal(48d, places[0].GetProperty("distance").GetDouble());
        Assert.Equal(["dead end"], places[1].GetProperty("notes").EnumerateArray().Select(note => note.GetString()));
    }

    [Fact]
    public void ExploreTourGivesOneWayThroughEveryUnvisitedPlaceWithARouteMossTankWalks()
    {
        var visited = new VisitedGround();
        var (host, verbs, ring) = Build(visited);
        visited.Note(new PluginNavigationPosition(0u, 0d, 0d, 0d, 0f, false));
        host.FakeAutomation.FakeNavigation.PlacesReport = new PluginPlacesReport(
            PluginPlacesState.Ready,
            [
                TourPlace(0d, 0d, 0f, 1),
                TourPlace(0.1d, 0d, 24f, 0, 2, 3),
                TourPlace(0.1d, 0.05d, 36f, 1),
                TourPlace(0.25d, 0d, 60f, 1),
            ],
            InDungeon: true,
            "found")
        {
            From = new PluginNavigationPosition(0u, 0d, 0d, 0d, 0f, false),
        };

        Assert.Equal("handled", verbs.Handle(Line("explore tour")).Outcome);

        JsonElement tour = Latest(ring, RecordKinds.ExploreTour);
        Assert.Equal("ready", tour.GetProperty("state").GetString());
        Assert.Equal(3, tour.GetProperty("stops").GetInt32());
        Assert.Equal(3, tour.GetProperty("waypoints").GetInt32());
        Assert.Equal([1, 2, 3], tour.GetProperty("tour").EnumerateArray().Select(stop => stop.GetProperty("stop").GetInt32()));
        Assert.Equal("farthest a walk reaches from the character", tour.GetProperty("end").GetProperty("why").GetString());
        JsonElement route = tour.GetProperty("route");
        Assert.Equal("Once", route.GetProperty("mode").GetString());
        Assert.True(route.GetProperty("walkLegs").GetBoolean());
        JsonElement[] points = [.. route.GetProperty("waypoints").EnumerateArray().Select(waypoint => waypoint.GetProperty("point"))];
        Assert.Equal([0.1d, 0.1d, 0.25d], points.Select(point => point.GetProperty("eastWest").GetDouble()));
        Assert.Equal([0d, 0.05d, 0d], points.Select(point => point.GetProperty("northSouth").GetDouble()));

        Assert.Equal("handled", verbs.Handle(Line("explore tour to 0.05 0.1")).Outcome);

        JsonElement toward = Latest(ring, RecordKinds.ExploreTour);
        Assert.Equal("nearest the end asked for", toward.GetProperty("end").GetProperty("why").GetString());
        Assert.Equal(
            [0.1d, 0.25d, 0.1d],
            toward.GetProperty("route").GetProperty("waypoints").EnumerateArray().Select(waypoint => waypoint.GetProperty("point").GetProperty("eastWest").GetDouble()));
        Assert.Equal("refused", verbs.Handle(Line("explore tour to nowhere")).Outcome);
    }

    [Fact]
    public void ATourOfADungeonTheCharacterCameIntoStartsAtTheEntranceAndEndsAtTheFarthestPortalSeen()
    {
        var entrances = new DungeonEntrances();
        entrances.Note(new PluginNavigationPosition(0xA9B40021u, 0d, 0d, 0d, 0f, true));
        entrances.Note(new PluginNavigationPosition(0x01560100u, 0d, 0d, 0d, 0f, false));
        var (host, verbs, ring) = Build(entrances: entrances, portals: new SeenPortals());
        var here = new PluginNavigationPosition(0x01560124u, 0.3d, 0d, 0d, 0f, false);
        host.FakeAutomation.FakeNavigation.Snapshot = new PluginNavigationSnapshot(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: Self,
            Position: here,
            IsMoving: false,
            IsAirborne: false);
        host.FakeAutomation.FakeNavigation.PlacesReport = new PluginPlacesReport(
            PluginPlacesState.Ready,
            [
                TourPlace(0d, 0d, 50f, 1),
                TourPlace(0.1d, 0d, 30f, 0, 2, 3),
                TourPlace(0.1d, 0.4d, 90f, 1),
                TourPlace(0.3d, 0d, 5f, 1),
            ],
            InDungeon: true,
            "found")
        {
            From = here,
        };
        host.FakeAutomation.FakeObjects.Add(DungeonPortal(1u, 0.005d));
        host.FakeAutomation.FakeObjects.Add(DungeonPortal(2u, 0.31d));

        Assert.Equal("handled", verbs.Handle(Line("explore tour")).Outcome);

        JsonElement tour = Latest(ring, RecordKinds.ExploreTour);
        Assert.Equal("where the character came into this dungeon", tour.GetProperty("start").GetProperty("why").GetString());
        Assert.Equal("nearest the portal seen farthest from the start", tour.GetProperty("end").GetProperty("why").GetString());
        Assert.Equal(4, tour.GetProperty("stops").GetInt32());
        Assert.Equal(4, tour.GetProperty("waypoints").GetInt32());
        Assert.Equal(
            [0d, 0.1d, 0.1d, 0.3d],
            tour.GetProperty("route").GetProperty("waypoints").EnumerateArray().Select(waypoint => waypoint.GetProperty("point").GetProperty("eastWest").GetDouble()));

        Assert.Equal("handled", verbs.Handle(Line("explore tour to 0.4 0.1")).Outcome);

        JsonElement toward = Latest(ring, RecordKinds.ExploreTour);
        Assert.Equal("where the character came into this dungeon", toward.GetProperty("start").GetProperty("why").GetString());
        Assert.Equal("nearest the end asked for", toward.GetProperty("end").GetProperty("why").GetString());
        Assert.Equal(
            [0d, 0.1d, 0.3d, 0.1d],
            toward.GetProperty("route").GetProperty("waypoints").EnumerateArray().Select(waypoint => waypoint.GetProperty("point").GetProperty("eastWest").GetDouble()));
    }

    private static PluginWorldObject DungeonPortal(uint objectId, double eastWest) =>
        new(objectId, 0u, "Portal", PluginObjectClass.Portal, 0u, 0u, 0u)
        {
            HasPosition = true,
            Position = new PluginNavigationPosition(0x01560105u, eastWest, 0d, 0d, 0f, false),
        };

    private static PluginNavigationPlace TourPlace(double eastWest, double northSouth, float walkMeters, params int[] neighbours) =>
        new(new PluginNavigationPosition(0u, eastWest, northSouth, 0d, 0f, false), walkMeters, PluginPlaceKind.Room, 60f, 6f, 0f, neighbours.Length)
        {
            Neighbours = neighbours,
        };

    [Fact]
    public void ExploreSaysToAskAgainWhileTheClientMapsTheGroundOrOnceTheCharacterHasMovedOn()
    {
        var (host, verbs, ring) = Build();
        FakeNavigation navigation = host.FakeAutomation.FakeNavigation;
        navigation.PlacesReport = new PluginPlacesReport(
            PluginPlacesState.Mapping, [], false, "mapping the ground around the character");

        Assert.Equal("handled", verbs.Handle(Line("explore")).Outcome);
        JsonElement mapping = Latest(ring, RecordKinds.Explore);
        Assert.Equal("mapping", mapping.GetProperty("state").GetString());
        Assert.Contains("ask again", mapping.GetProperty("note").GetString());

        navigation.PlacesReport = new PluginPlacesReport(PluginPlacesState.Ready, [], false, "found")
        {
            From = new PluginNavigationPosition(0u, 0.2d, 0d, 0d, 0f, true),
        };
        verbs.Handle(Line("explore"));
        Assert.Equal("refreshing", Latest(ring, RecordKinds.Explore).GetProperty("state").GetString());

        Assert.NotEqual("handled", verbs.Handle(Line("explore nowhere")).Outcome);
    }

    [Fact]
    public void ExploreOutdoorsGivesBuildingsAndTheLandblocksBesideTheCharacters()
    {
        var (host, verbs, ring) = Build();
        host.FakeAutomation.FakeNavigation.PlacesReport = new PluginPlacesReport(
            PluginPlacesState.Ready,
            [
                new PluginNavigationPlace(new PluginNavigationPosition(0u, 0.4d, 0.4d, 0d, 0f, true), float.NaN, PluginPlaceKind.Landblock, 36864f, 192f, 0f, 0)
                {
                    LandblockId = 0xAAB5FFFFu,
                    IsWater = true,
                },
                new PluginNavigationPlace(new PluginNavigationPosition(0u, 0.05d, 0d, 0d, 0f, true), float.NaN, PluginPlaceKind.Building, 0f, 0f, 0f, 2)
                {
                    LandblockId = 0xA9B4FFFFu,
                },
            ],
            InDungeon: false,
            "found")
        {
            From = new PluginNavigationPosition(0u, 0d, 0d, 0d, 0f, true),
        };

        Assert.Equal("handled", verbs.Handle(Line("explore")).Outcome);

        JsonElement[] places = [.. Latest(ring, RecordKinds.Explore).GetProperty("places").EnumerateArray()];
        Assert.Equal(["building", "landblock"], places.Select(place => place.GetProperty("kind").GetString()));
        Assert.Equal(2, places[0].GetProperty("doorways").GetInt32());
        Assert.Equal(JsonValueKind.Null, places[0].GetProperty("walk").ValueKind);
        Assert.Equal("0xAAB5", places[1].GetProperty("landblock").GetString());
        Assert.Equal(["north-east", "water"], places[1].GetProperty("notes").EnumerateArray().Select(note => note.GetString()));
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

    private static (FakePluginHost Host, WorldReadVerbs Verbs, RecordRing Ring) Build(
        VisitedGround? visited = null,
        DungeonEntrances? entrances = null,
        SeenPortals? portals = null)
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
        return (host, new WorldReadVerbs(host, new Publisher(new AgentClock(), ring), visited, entrances, portals), ring);
    }
}
