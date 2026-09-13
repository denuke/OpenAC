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
/// war bolt and an arrow. Neither sends anything to the server.
/// </summary>
internal sealed class WorldReadVerbs : IVerbFamily
{
    internal const int ShownLimit = 40;
    private const string NotInWorld = "no character is in the world";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;

    internal WorldReadVerbs(IPluginHost host, Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        _host = host;
        _publisher = publisher;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["nearby", "inspect"];

    public VerbResult Handle(CommandLine line) =>
        line.Verb == "nearby" ? Nearby(line) : Inspect(line);

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
                : Sight.Untraced(
                    $"one answer traces only the nearest {Sight.TracedObjects} objects within "
                    + $"{Sight.TracedRangeMeters:0} m; inspect this one for its own");
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

    private static JsonObject Row(
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
