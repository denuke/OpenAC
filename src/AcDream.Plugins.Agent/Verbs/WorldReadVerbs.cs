using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>nearby</c> and <c>inspect</c>: what is around the character and what the
/// client holds about one object, each with a sight verdict for an arc spell, a
/// war bolt and an arrow; and <c>explore</c>: spots the character can walk to. None
/// sends anything to the server.
/// </summary>
internal sealed class WorldReadVerbs : IVerbFamily
{
    internal const int ShownLimit = 40;

    internal static readonly string UntracedBecause =
        $"one answer traces only the nearest {Sight.TracedObjects} objects within "
        + $"{Sight.TracedRangeMeters:0} m; inspect this one for its own";
    private const string NotInWorld = "no character is in the world";

    /// <summary>How many spots one <c>explore</c> answer shows unless asked for more.</summary>
    internal const int ExploreShown = 12;

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly VisitedGround _visited;
    private readonly DungeonEntrances _entrances;
    private readonly SeenPortals _portals;

    internal WorldReadVerbs(
        IPluginHost host,
        Publisher publisher,
        VisitedGround? visited = null,
        DungeonEntrances? entrances = null,
        SeenPortals? portals = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        _host = host;
        _publisher = publisher;
        _visited = visited ?? new VisitedGround();
        _entrances = entrances ?? new DungeonEntrances();
        _portals = portals ?? new SeenPortals();
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["nearby", "inspect", "explore"];

    public VerbResult Handle(CommandLine line) => line.Verb switch
    {
        "nearby" => Nearby(line),
        "explore" => Explore(line),
        _ => Inspect(line),
    };

    /// <summary>
    /// The places the character can walk to, from the client's navigation mesh: those it has
    /// not stood near first, then those it has, each nearest walk first, with the line that
    /// walks there.
    /// </summary>
    private VerbResult Explore(CommandLine line)
    {
        int limit = ExploreShown;
        string[] words = line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool tour = words.Length > 0 && words[0].Equals("tour", StringComparison.OrdinalIgnoreCase);
        string[]? endWords = null;
        int first = tour ? 1 : 0;
        if (tour && words.Length > 1 && words[1].Equals("to", StringComparison.OrdinalIgnoreCase))
        {
            int limitAt = Array.FindIndex(words, 2, word => word.Equals("limit", StringComparison.OrdinalIgnoreCase));
            first = limitAt < 0 ? words.Length : limitAt;
            endWords = words[2..first];
        }
        for (int index = first; index < words.Length; index++)
        {
            if (words[index].Equals("limit", StringComparison.OrdinalIgnoreCase)
                && index + 1 < words.Length
                && int.TryParse(words[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int asked)
                && asked >= 1)
            {
                limit = asked;
                index++;
            }
            else
            {
                return RefuseExplore(
                    line,
                    $"'{words[index]}' is not understood; use explore [limit <n>], or explore tour [to <north-south> <east-west> [elevation]] [limit <n>]");
            }
        }

        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return RefuseExplore(line, NotInWorld);
        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        if (!self.IsAvailable)
            return RefuseExplore(line, "the client has no position for its own body");
        PluginPlacesReport report = automation.Navigation.CapturePlaces();
        if (report.State == PluginPlacesState.Unavailable)
            return RefuseExplore(line, report.Reason ?? "the client cannot find places now");
        if (tour)
            return Tour(line, self.Position, report, endWords, limit);

        IReadOnlyList<PluginNavigationPlace> places = report.Places ?? [];
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unvisited = new List<PluginNavigationPlace>();
        var visited = new List<PluginNavigationPlace>();
        foreach (PluginNavigationPlace place in places)
        {
            string kind = KindWord(place.Kind);
            kinds[kind] = kinds.TryGetValue(kind, out int seen) ? seen + 1 : 1;
            (Visited(place) ? visited : unvisited).Add(place);
        }
        PluginNavigationPosition here = self.Position;
        double Nearness(PluginNavigationPlace place) =>
            float.IsFinite(place.WalkMeters) ? place.WalkMeters : Geometry.DistanceMeters(here, place.Position);
        PluginNavigationPlace[] ordered = [.. unvisited.OrderBy(Nearness), .. visited.OrderBy(Nearness)];
        var rows = new JsonArray();
        for (int index = 0; index < ordered.Length && rows.Count < limit; index++)
        {
            PluginNavigationPlace place = ordered[index];
            var row = new JsonObject
            {
                ["kind"] = KindWord(place.Kind),
                ["notes"] = Notes(here, place),
                ["visited"] = index >= unvisited.Count,
                ["place"] = Coordinates.DescribePlace(place.Position),
                ["walk"] = float.IsFinite(place.WalkMeters) ? Math.Round(place.WalkMeters, 1) : null,
                ["distance"] = Math.Round(Geometry.DistanceMeters(here, place.Position), 1),
                ["bearing"] = Math.Round(Geometry.BearingDegrees(here, place.Position), 0),
            };
            switch (place.Kind)
            {
                case PluginPlaceKind.Building:
                    row["doorways"] = place.Exits;
                    break;
                case PluginPlaceKind.Landblock:
                    break;
                default:
                    row["area"] = Math.Round(place.AreaSquareMeters, 0);
                    row["width"] = Math.Round(place.WidthMeters, 1);
                    row["rise"] = Math.Round(place.RiseMeters, 1);
                    row["exits"] = place.Exits;
                    break;
            }
            if (place.LandblockId != 0u)
                row["landblock"] = $"0x{place.LandblockId >> 16:X4}";
            row["go"] = GoLine(place.Position);
            rows.Add(row);
        }
        var counts = new JsonObject();
        foreach (KeyValuePair<string, int> pair in kinds)
            counts[pair.Key] = pair.Value;
        string state = report.State == PluginPlacesState.Mapping
            ? "mapping"
            : Geometry.DistanceMeters(self.Position, report.From) > StalePlacesMeters ? "refreshing" : "ready";
        var record = new JsonObject
        {
            ["id"] = line.Id,
            ["state"] = state,
            ["region"] = report.InDungeon ? "dungeon" : "land",
            ["center"] = Coordinates.Describe(self.Position),
            ["found"] = places.Count,
            ["kinds"] = counts,
            ["visited"] = visited.Count,
            ["shown"] = rows.Count,
            ["places"] = rows,
        };
        if (state == "mapping")
            record["note"] = "the client is still mapping the ground around the character; ask again in a few seconds";
        else if (state == "refreshing")
            record["note"] = "these places were found from where the character stood before; ask again in a moment for walks from here";
        _publisher.Publish(RecordKinds.Explore, record);
        return VerbResult.Handled;
    }

    /// <summary>Whether the character has stood near a place: in a landblock, near a building's origin, or within half a place's width.</summary>
    private bool Visited(in PluginNavigationPlace place) => place.Kind switch
    {
        PluginPlaceKind.Landblock => _visited.HasBeenIn(place.LandblockId),
        PluginPlaceKind.Building => _visited.IsNear(place.Position, BuildingNearMeters),
        _ => _visited.IsNear(place.Position, place.WidthMeters / 2d),
    };

    /// <summary>
    /// One way through every room, passage and open ground not yet visited, from the place
    /// nearest the character to the far end of the ground a walk reaches, or to the place
    /// nearest a given end, with the stops in order and a MossTank route that walks them once
    /// with the client's route planning.
    /// </summary>
    private VerbResult Tour(CommandLine line, in PluginNavigationPosition here, PluginPlacesReport report, string[]? endWords, int limit)
    {
        IReadOnlyList<PluginNavigationPlace> places = report.Places ?? [];
        int? endAt = null;
        if (endWords is not null)
        {
            if (!MotorVerbs.TryReadPlace(endWords, here, out PluginNavigationPosition asked))
                return RefuseExplore(line, "a tour ends at a place in map coordinates, such as explore tour to 24.30537 -101.10833 0.00002");
            endAt = NearestWalkablePlace(places, asked);
        }
        string state = report.State == PluginPlacesState.Mapping
            ? "mapping"
            : Geometry.DistanceMeters(here, report.From) > StalePlacesMeters ? "refreshing" : "ready";
        int? startAt = report.InDungeon && _entrances.TryGet(here, out PluginNavigationPosition entrance)
            ? NearestWalkablePlace(places, entrance)
            : null;
        int start = PlaceTour.Start(places, startAt);
        bool fromEntrance = startAt is { } entered && entered == start;
        string? endWhy = endAt is null ? null : "nearest the end asked for";
        if (endAt is null && report.InDungeon && start >= 0)
        {
            _portals.Note(_host.Automation.Objects.CaptureObjects());
            if (FarthestPortal(_portals.In(here), places[start].Position) is { } portal)
            {
                endAt = NearestWalkablePlace(places, portal);
                endWhy = "nearest the portal seen farthest from the start";
            }
        }
        int end = -1;
        List<int> order = state == "mapping" ? [] : PlaceTour.Order(places, endAt, index => Visited(places[index]), out end, startAt);

        var rows = new JsonArray();
        var waypoints = new JsonArray();
        List<int> routePoints = PlaceTour.RoutePoints(places, order);
        foreach (int point in routePoints)
        {
            PluginNavigationPosition position = places[point].Position;
            waypoints.Add(new JsonObject
            {
                ["point"] = new JsonObject
                {
                    ["northSouth"] = Math.Round(position.NorthSouth, Coordinates.Decimals),
                    ["eastWest"] = Math.Round(position.EastWest, Coordinates.Decimals),
                    ["elevation"] = Math.Round(position.Elevation, Coordinates.Decimals),
                },
            });
        }
        for (int stop = 0; stop < order.Count; stop++)
        {
            PluginNavigationPlace place = places[order[stop]];
            if (rows.Count >= limit)
                continue;
            rows.Add(new JsonObject
            {
                ["stop"] = stop + 1,
                ["kind"] = KindWord(place.Kind),
                ["notes"] = Notes(here, place),
                ["place"] = Coordinates.DescribePlace(place.Position),
                ["walk"] = float.IsFinite(place.WalkMeters) ? Math.Round(place.WalkMeters, 1) : null,
                ["exits"] = place.Exits,
            });
        }
        var record = new JsonObject
        {
            ["id"] = line.Id,
            ["state"] = state,
            ["region"] = report.InDungeon ? "dungeon" : "land",
            ["center"] = Coordinates.Describe(here),
            ["found"] = places.Count,
            ["stops"] = order.Count,
            ["waypoints"] = routePoints.Count,
            ["shown"] = rows.Count,
            ["start"] = start >= 0 && order.Count > 0
                ? new JsonObject
                {
                    ["kind"] = KindWord(places[start].Kind),
                    ["place"] = Coordinates.DescribePlace(places[start].Position),
                    ["why"] = fromEntrance ? "where the character came into this dungeon" : "nearest the character by walk",
                }
                : null,
            ["end"] = end >= 0 && order.Count > 0
                ? new JsonObject
                {
                    ["kind"] = KindWord(places[end].Kind),
                    ["place"] = Coordinates.DescribePlace(places[end].Position),
                    ["why"] = endAt == end
                        ? endWhy
                        : fromEntrance ? "farthest a walk reaches from where the character came in" : "farthest a walk reaches from the character",
                    ["visited"] = Visited(places[end]),
                }
                : null,
            ["tour"] = rows,
            ["route"] = new JsonObject
            {
                ["enabled"] = true,
                ["mode"] = "Once",
                ["walkLegs"] = true,
                ["waypoints"] = waypoints,
            },
        };
        record["note"] = state switch
        {
            "mapping" => "the client is still mapping the ground around the character; ask again in a few seconds",
            "refreshing" => "this tour starts from where the character stood before; ask again in a moment for one from here",
            _ when order.Count == 0 => "every place a walk reaches has been visited",
            _ => "send configure mosstank with this route as its route part and its macro running to walk the tour; "
                + "ask for a new tour after it finishes or stops, and places walked near are left out",
        };
        _publisher.Publish(RecordKinds.ExploreTour, record);
        return VerbResult.Handled;
    }

    /// <summary>The room, passage or open ground nearest a position, or null when there is none.</summary>
    /// <summary>How near a tour's start a portal may stand and still not end the tour, being most likely the way in.</summary>
    internal const double PortalNearStartMeters = 20d;

    private static PluginNavigationPosition? FarthestPortal(IReadOnlyCollection<PluginNavigationPosition> portals, in PluginNavigationPosition start)
    {
        PluginNavigationPosition? farthest = null;
        double farthestMeters = PortalNearStartMeters;
        foreach (PluginNavigationPosition portal in portals)
        {
            double meters = Geometry.DistanceMeters(start, portal);
            if (meters > farthestMeters)
            {
                farthest = portal;
                farthestMeters = meters;
            }
        }
        return farthest;
    }

    private static int? NearestWalkablePlace(IReadOnlyList<PluginNavigationPlace> places, in PluginNavigationPosition position)
    {
        int nearest = -1;
        double nearestMeters = double.MaxValue;
        for (int index = 0; index < places.Count; index++)
        {
            if (places[index].Kind is not (PluginPlaceKind.Room or PluginPlaceKind.Passage or PluginPlaceKind.Open))
                continue;
            double meters = Geometry.DistanceMeters(position, places[index].Position);
            if (meters < nearestMeters)
            {
                nearest = index;
                nearestMeters = meters;
            }
        }
        return nearest < 0 ? null : nearest;
    }

    /// <summary>How far the character may stand from where places were found before they are found again.</summary>
    internal const double StalePlacesMeters = 10d;

    /// <summary>How far above or below the character a place's floor lies before it is said to be above or below.</summary>
    internal const double OtherLevelMeters = 2.5d;

    /// <summary>A place whose floor rises at least this far is a stair or ramp.</summary>
    internal const double StairRiseMeters = 2d;

    /// <summary>How near a building's origin the character must have stood for the building to count as visited.</summary>
    internal const double BuildingNearMeters = 10d;

    internal static string KindWord(PluginPlaceKind kind) => kind switch
    {
        PluginPlaceKind.Passage => "passage",
        PluginPlaceKind.Open => "open ground",
        PluginPlaceKind.Building => "building",
        PluginPlaceKind.Landblock => "landblock",
        _ => "room",
    };

    /// <summary>Which way a landblock lies from the one a cell is in: north, south-east and so on.</summary>
    internal static string DirectionWord(uint fromCellId, uint landblockId)
    {
        int east = (int)((landblockId >> 24) & 0xFFu) - (int)((fromCellId >> 24) & 0xFFu);
        int north = (int)((landblockId >> 16) & 0xFFu) - (int)((fromCellId >> 16) & 0xFFu);
        string northSouth = north > 0 ? "north" : north < 0 ? "south" : string.Empty;
        string eastWest = east > 0 ? "east" : east < 0 ? "west" : string.Empty;
        return northSouth.Length > 0 && eastWest.Length > 0 ? $"{northSouth}-{eastWest}" : northSouth + eastWest;
    }

    /// <summary>What sets a place apart at a glance: a dead end or a junction, a stair or ramp, or floor above or below the character's.</summary>
    internal static JsonArray Notes(in PluginNavigationPosition self, in PluginNavigationPlace place)
    {
        var notes = new JsonArray();
        if (place.Kind == PluginPlaceKind.Landblock)
        {
            notes.Add(DirectionWord(self.CellId, place.LandblockId));
            if (place.IsWater)
                notes.Add("water");
            return notes;
        }
        if (place.Kind != PluginPlaceKind.Building)
        {
            if (place.Exits == 1)
                notes.Add("dead end");
            else if (place.Exits >= 3)
                notes.Add("junction");
            if (place.RiseMeters >= StairRiseMeters)
                notes.Add("stairs or ramp");
        }
        double up = (place.Position.Elevation - self.Elevation) * 240d;
        if (up >= OtherLevelMeters)
            notes.Add("above");
        else if (up <= -OtherLevelMeters)
            notes.Add("below");
        return notes;
    }

    /// <summary>The command line that walks to a place.</summary>
    internal static string GoLine(in PluginNavigationPosition position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"go to {position.NorthSouth:0.#####} {position.EastWest:0.#####} {position.Elevation:0.#####}");

    private VerbResult RefuseExplore(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.ExploreRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }

    private VerbResult Nearby(CommandLine line)
    {
        string? kind = null;
        double? range = null;
        foreach (string token in line.Arguments.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double meters)
                && meters > 0d)
            {
                range = meters;
            }
            else if (EntityKinds.IsWord(token))
            {
                kind = token.ToLowerInvariant();
            }
            else
            {
                return RefuseNearby(line, $"'{token}' is not a kind word or a range", listKinds: true);
            }
        }

        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return RefuseNearby(line, NotInWorld, listKinds: false);
        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        if (!self.IsAvailable)
            return RefuseNearby(line, "the client has no position for its own body", listKinds: false);

        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var matched = new List<PlacedObject>();
        foreach (PlacedObject placed in WorldQuery.Around(automation, self, range))
        {
            histogram[placed.Word] = histogram.TryGetValue(placed.Word, out int count) ? count + 1 : 1;
            if (kind is null || kind == placed.Word)
                matched.Add(placed);
        }

        var entities = new JsonArray();
        int traced = 0;
        foreach (PlacedObject placed in matched.Take(ShownLimit))
        {
            JsonObject row = Row(self, placed.Value, placed.Distance);
            bool trace = traced < Sight.TracedObjects && placed.Distance <= Sight.TracedRangeMeters;
            if (trace)
                traced++;
            row["sight"] = trace
                ? Sight.Trace(automation.Projectiles, placed.Value.ObjectId)
                : Sight.Untraced(UntracedBecause);
            entities.Add(row);
        }

        var kinds = new JsonObject();
        foreach (KeyValuePair<string, int> pair in histogram)
            kinds[pair.Key] = pair.Value;

        _publisher.Publish(RecordKinds.Nearby, new JsonObject
        {
            ["id"] = line.Id,
            ["filter"] = new JsonObject { ["kind"] = kind, ["range"] = range },
            ["center"] = Coordinates.Describe(self.Position),
            ["matched"] = matched.Count,
            ["shown"] = entities.Count,
            ["beyondLimit"] = matched.Count - entities.Count,
            ["unclassified"] = histogram.TryGetValue(EntityKinds.Unknown, out int unknown) ? unknown : 0,
            ["kinds"] = kinds,
            ["entities"] = entities,
        });
        return VerbResult.Handled;
    }

    internal static JsonObject Row(
        in PluginNavigationSnapshot self,
        in PluginWorldObject value,
        double distance)
    {
        double bearing = Geometry.BearingDegrees(self.Position, value.Position);
        var row = new JsonObject
        {
            ["guid"] = Facts.Hex(value.ObjectId),
            ["name"] = value.Name,
            ["objectClass"] = EntityKinds.WordFor(value.ObjectClass),
            ["distance"] = Math.Round(distance, 1),
            ["bearing"] = Math.Round(bearing, 0),
            ["relativeBearing"] = Math.Round(
                Geometry.RelativeDegrees(bearing, self.Position.HeadingDegrees), 0),
            ["position"] = Coordinates.Describe(value.Position),
        };
        if (value.StackSize > 1)
            row["stackSize"] = value.StackSize;
        if (value.ObjectClass == PluginObjectClass.Door)
            row["doorOpen"] = value.IsDoorOpen;
        return row;
    }

    private VerbResult RefuseNearby(CommandLine line, string reason, bool listKinds)
    {
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["line"] = line.Text,
            ["reason"] = reason,
        };
        if (listKinds)
            fields["kinds"] = new JsonArray(EntityKinds.Words.Select(word => (JsonNode?)word).ToArray());
        _publisher.Publish(RecordKinds.NearbyRefused, fields);
        return VerbResult.Refused(reason);
    }

    private VerbResult Inspect(CommandLine line)
    {
        if (!Guids.TryParse(line.Arguments, out uint id))
            return RefuseInspect(line, "inspect needs an object id such as 0x5000000A");
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return RefuseInspect(line, NotInWorld);
        if (!automation.Objects.TryGet(id, out PluginWorldObject value))
            return RefuseInspect(line, "the client holds no object with that id");

        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        JsonObject position;
        JsonObject distance;
        JsonObject sight;
        if (value.HasPosition)
        {
            position = Facts.Observed(Coordinates.Describe(value.Position));
            distance = self.IsAvailable
                ? Facts.Derived(
                    Math.Round(Geometry.DistanceMeters(self.Position, value.Position), 1),
                    "both positions",
                    "straight-line distance in meters")
                : Facts.Unknown("the client has no position for its own body");
            sight = self.IsAvailable
                ? Sight.Trace(automation.Projectiles, id)
                : Sight.Untraced("the client has no position for its own body");
        }
        else
        {
            string because = value.IsOwned || value.ContainerObjectId != 0u || value.WielderObjectId != 0u
                ? "the object is carried or held, so it has no position of its own"
                : "the client has not been told where this object is";
            position = Facts.Unknown(because);
            distance = Facts.Unknown(because);
            sight = Sight.Untraced(because);
        }

        _publisher.Publish(RecordKinds.EntityInspected, new JsonObject
        {
            ["id"] = line.Id,
            ["guid"] = Facts.Hex(id),
            ["name"] = value.Name,
            ["objectClass"] = EntityKinds.WordFor(value.ObjectClass),
            ["weenieClassId"] = value.WeenieClassId == 0u ? null : value.WeenieClassId,
            ["itemType"] = value.ItemType,
            ["owned"] = value.IsOwned,
            ["landscape"] = value.IsLandscape,
            ["container"] = value.ContainerObjectId == 0u ? null : Facts.Hex(value.ContainerObjectId),
            ["wielder"] = value.WielderObjectId == 0u ? null : Facts.Hex(value.WielderObjectId),
            ["stackSize"] = value.StackSize,
            ["position"] = position,
            ["distance"] = distance,
            ["sight"] = sight,
            ["appraised"] = value.HasAppraisalData,
            ["spellIds"] = new JsonArray(value.SpellIds.Select(spell => (JsonNode?)spell).ToArray()),
            ["properties"] = Properties(automation.Objects, id),
        });
        return VerbResult.Handled;
    }

    private static JsonObject Properties(IWorldObjectAutomation objects, uint id)
    {
        if (!objects.TryCaptureProperties(id, out PluginItemProperties properties))
            return Facts.Unknown("the client holds no properties for this object");
        var tables = new JsonObject
        {
            ["ints"] = Table(properties.Ints),
            ["int64s"] = Table(properties.Int64s),
            ["bools"] = Table(properties.Bools),
            ["floats"] = Table(properties.Floats),
            ["strings"] = Table(properties.Strings),
            ["dataIds"] = Table(properties.DataIds),
            ["instanceIds"] = Table(properties.InstanceIds),
        };
        bool empty = tables.All(table => table.Value!.AsObject().Count == 0);
        return empty
            ? Facts.Unknown("the server has not described this object's properties")
            : Facts.Observed(tables, "keys are the client's numeric property ids");
    }

    private static JsonObject Table<T>(IReadOnlyDictionary<uint, T>? values)
    {
        var table = new JsonObject();
        if (values is null)
            return table;
        foreach (KeyValuePair<uint, T> pair in values.OrderBy(pair => pair.Key))
            table[pair.Key.ToString(CultureInfo.InvariantCulture)] = JsonValue.Create(pair.Value);
        return table;
    }

    private VerbResult RefuseInspect(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.EntityRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
