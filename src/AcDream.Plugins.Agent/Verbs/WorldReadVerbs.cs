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

    internal WorldReadVerbs(IPluginHost host, Publisher publisher, VisitedGround? visited = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        _host = host;
        _publisher = publisher;
        _visited = visited ?? new VisitedGround();
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
        for (int index = 0; index < words.Length; index++)
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
                return RefuseExplore(line, $"'{words[index]}' is not understood; use explore [limit <n>]");
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

        IReadOnlyList<PluginNavigationPlace> places = report.Places ?? [];
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unvisited = new List<PluginNavigationPlace>();
        var visited = new List<PluginNavigationPlace>();
        foreach (PluginNavigationPlace place in places)
        {
            string kind = KindWord(place.Kind);
            kinds[kind] = kinds.TryGetValue(kind, out int seen) ? seen + 1 : 1;
            (_visited.IsNear(place.Position, place.WidthMeters / 2d) ? visited : unvisited).Add(place);
        }
        var rows = new JsonArray();
        for (int index = 0; index < unvisited.Count + visited.Count && rows.Count < limit; index++)
        {
            bool been = index >= unvisited.Count;
            PluginNavigationPlace place = been ? visited[index - unvisited.Count] : unvisited[index];
            rows.Add(new JsonObject
            {
                ["kind"] = KindWord(place.Kind),
                ["notes"] = Notes(self.Position, place),
                ["visited"] = been,
                ["place"] = Coordinates.DescribePlace(place.Position),
                ["walk"] = Math.Round(place.WalkMeters, 1),
                ["distance"] = Math.Round(Geometry.DistanceMeters(self.Position, place.Position), 1),
                ["bearing"] = Math.Round(Geometry.BearingDegrees(self.Position, place.Position), 0),
                ["area"] = Math.Round(place.AreaSquareMeters, 0),
                ["width"] = Math.Round(place.WidthMeters, 1),
                ["rise"] = Math.Round(place.RiseMeters, 1),
                ["exits"] = place.Exits,
                ["go"] = GoLine(place.Position),
            });
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

    /// <summary>How far the character may stand from where places were found before they are found again.</summary>
    internal const double StalePlacesMeters = 10d;

    /// <summary>How far above or below the character a place's floor lies before it is said to be above or below.</summary>
    internal const double OtherLevelMeters = 2.5d;

    /// <summary>A place whose floor rises at least this far is a stair or ramp.</summary>
    internal const double StairRiseMeters = 2d;

    internal static string KindWord(PluginPlaceKind kind) => kind switch
    {
        PluginPlaceKind.Passage => "passage",
        PluginPlaceKind.Open => "open ground",
        _ => "room",
    };

    /// <summary>What sets a place apart at a glance: a dead end or a junction, a stair or ramp, or floor above or below the character's.</summary>
    internal static JsonArray Notes(in PluginNavigationPosition self, in PluginNavigationPlace place)
    {
        var notes = new JsonArray();
        if (place.Exits == 1)
            notes.Add("dead end");
        else if (place.Exits >= 3)
            notes.Add("junction");
        if (place.RiseMeters >= StairRiseMeters)
            notes.Add("stairs or ramp");
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
