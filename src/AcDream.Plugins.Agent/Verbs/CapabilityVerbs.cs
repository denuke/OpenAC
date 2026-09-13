using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>One command line an agent could send, and whether this client would take it now.</summary>
internal readonly record struct Capability(string Verb, string Label, string Line, string Verdict, string? Because);

/// <summary>
/// <c>capabilities [id]</c>: what the character can do right now, so one read
/// replaces a round of nearby, inspect and trying lines. With an id, every line
/// this client would take for that object. Without one, the same answer for
/// every object <c>nearby</c> would list, with the lines graded alike for all of
/// them said once in a legend, and the lines that act on the character itself.
/// Each line is graded available, unavailable with the reason, or unknown with
/// what is missing, by the checks the verbs make before they send. Nothing is
/// sent to the server.
/// </summary>
internal sealed class CapabilityVerbs : IVerbFamily
{
    internal const string Available = "available";
    internal const string Unavailable = "unavailable";
    internal const string Unknown = "unknown";

    internal const string VerdictMeans =
        "whether this client would take the line now, judged from what it already holds; the server can still "
        + "refuse a line the client takes, and that answer arrives on the action's own outcome";

    /// <summary>Hostile creatures are looked for this far away, beyond any object a read lists.</summary>
    internal const float HostileSearchMeters = 1000f;

    internal const string GuidHole = "<guid>";

    private const string NotInWorld = "no character is in the world";

    private const string NotAnId =
        "capabilities takes an object id such as 0x70000001; leave it out to ask about everything around the character";

    private const string CastDepends =
        "whether a spell can be cast depends on the spell; 'spells' lists what the character knows and whether it "
        + "carries the components";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal CapabilityVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["capabilities"];

    public VerbResult Handle(CommandLine line)
    {
        string argument = line.Arguments.Trim();
        bool one = Guids.TryParse(argument, out uint id);
        if (argument.Length > 0 && !one)
            return Refuse(line, NotAnId);
        if (!_host.Automation.IsAvailable)
            return Refuse(line, NotInWorld);
        Situation situation = Situation.Read(_host, _outcomes);
        return one ? One(line, situation, id) : Everything(line, situation);
    }

    private VerbResult One(CommandLine line, Situation situation, uint id)
    {
        if (!situation.Automation.Objects.TryGet(id, out PluginWorldObject value))
            return Refuse(line, TargetVerbs.NoSuchObject);
        (string reach, string? because) = Reach(value);
        _publisher.Publish(RecordKinds.Capabilities, new JsonObject
        {
            ["id"] = line.Id,
            ["guid"] = Facts.Hex(id),
            ["name"] = value.Name,
            ["objectClass"] = EntityKinds.WordFor(value.ObjectClass),
            ["reach"] = reach,
            ["reachBecause"] = because,
            ["verdictMeans"] = VerdictMeans,
            ["actions"] = Rows(Grade(situation, value)),
        });
        return VerbResult.Handled;
    }

    private VerbResult Everything(CommandLine line, Situation situation)
    {
        if (!situation.Self.IsAvailable)
            return Refuse(line, MotorVerbs.NoOwnPosition);
        IAutomationSurface automation = situation.Automation;
        List<PlacedObject> around = WorldQuery.Around(automation, situation.Self, null);
        var graded = around
            .Take(WorldReadVerbs.ShownLimit)
            .Select(placed => (Placed: placed, Actions: Grade(situation, placed.Value)))
            .ToList();
        List<Capability> legend = Legend(graded.Select(row => (row.Placed.Value.ObjectId, row.Actions)).ToList());
        HashSet<string> said = legend.Select(capability => capability.Verb).ToHashSet(StringComparer.Ordinal);

        var entities = new JsonArray();
        int traced = 0;
        foreach ((PlacedObject placed, List<Capability> actions) in graded)
        {
            JsonObject row = WorldReadVerbs.Row(situation.Self, placed.Value, placed.Distance);
            bool trace = traced < Sight.TracedObjects && placed.Distance <= Sight.TracedRangeMeters;
            if (trace)
                traced++;
            row["sight"] = trace
                ? Sight.Trace(automation.Projectiles, placed.Value.ObjectId)
                : Sight.Untraced(WorldReadVerbs.UntracedBecause);
            row["actions"] = Rows(actions.Where(capability => !said.Contains(capability.Verb)));
            entities.Add(row);
        }

        var kinds = new JsonObject();
        foreach (IGrouping<string, PlacedObject> kind in around
            .GroupBy(placed => placed.Word)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            kinds[kind.Key] = kind.Count();
        }

        _publisher.Publish(RecordKinds.PlayerCapabilities, new JsonObject
        {
            ["id"] = line.Id,
            ["verdictMeans"] = VerdictMeans,
            ["center"] = Coordinates.Describe(situation.Self.Position),
            ["matched"] = around.Count,
            ["shown"] = entities.Count,
            ["beyondLimit"] = around.Count - entities.Count,
            ["kinds"] = kinds,
            ["legend"] = Rows(legend),
            ["entities"] = entities,
            ["character"] = Character(situation, around.Count),
        });
        return VerbResult.Handled;
    }

    /// <summary>
    /// The one grader. The answer for one object and every row of the whole view
    /// come from here, so the two never disagree.
    /// </summary>
    private static List<Capability> Grade(Situation situation, in PluginWorldObject value)
    {
        string hex = Facts.Hex(value.ObjectId);
        var lines = new List<Capability>
        {
            new("inspect", "read everything the client holds about it", $"inspect {hex}", Available, null),
            Check("target", "select it", $"target {hex}", TargetVerbs.TargetProblem(situation.Automation, value.ObjectId)),
        };
        if (value.IsOwned)
        {
            lines.Add(new("use", "use it", $"use {hex}", Available, null));
            lines.Add(new("drop", "drop it on the ground", $"drop {hex}", Available, null));
            lines.Add(Sale(situation, value.ObjectId));
            return lines;
        }
        if (value.ContainerObjectId != 0u)
        {
            lines.Add(Check("loot", "take it from the open container", $"loot {hex}", LootProblem(situation, value)));
            return lines;
        }
        lines.Add(Check("face", "turn to face it", $"face {hex}", MotorVerbs.FaceProblem(situation.Self, value)));
        lines.Add(Check("go to", "walk to it along a route the client plans", $"go to {hex}",
            MotorVerbs.GoToProblem(situation.Self, value)));
        lines.Add(Check("use", "use it, as double-clicking it does", $"use {hex}",
            ObjectVerbs.WorldUseProblem(situation.Self, value)));
        lines.Add(Check("open", "open it and list what is inside", $"open {hex}", ObjectVerbs.OpenProblem(value)));
        lines.Add(Check("attack", "attack it", $"attack {hex}",
            AttackVerbs.AttackProblem(situation.Stance, situation.Hostile.Contains(value.ObjectId))));
        lines.Add(new("cast", "cast a spell at it", $"cast <spell name or id> on {hex}", Unknown, CastDepends));
        return lines;
    }

    /// <summary>The lines that act on the character rather than on an object around it.</summary>
    private static List<Capability> CharacterLines(Situation situation, int nearby)
    {
        PluginNavigationSnapshot self = situation.Self;
        string? noBody = !self.IsAvailable
            ? MotorVerbs.NoOwnPosition
            : self.IsPortalSpace ? MotorVerbs.InPortalSpace : null;
        return
        [
            new("stance combat", "enter the stance the wielded weapon or held caster calls for", "stance combat", Available, null),
            new("stance peace", "leave combat", "stance peace", Available, null),
            Check("target nearest", "select the nearest object of a kind", "target nearest <kind>",
                nearby == 0 ? "nothing is nearby" : null),
            Check("untarget", "clear the selection", "untarget",
                situation.Selected is null ? "nothing is selected" : null),
            Check("attack", "attack the selection", "attack",
                situation.Selected is { } aimed
                    ? AttackVerbs.AttackProblem(situation.Stance, situation.Hostile.Contains(aimed))
                    : "nothing is selected"),
            new("cast", "cast a spell on the character, or at the selection", "cast <spell name or id>", Unknown, CastDepends),
            Check("buy", situation.VendorName is { } seller ? $"buy from {seller}" : "buy from the open vendor",
                "buy <listing id or name>", situation.Vendor == 0u ? VendorVerbs.NoVendor : null),
            Check("loot", "take an item from the open container", "loot <item id>",
                situation.Container == 0u ? LootVerbs.NoContainer : null),
            Check("run", "walk, run, strafe or turn by a distance, an angle or a time", "run forward <meters>", noBody),
            Check("jump", "jump", "jump", noBody),
            new("stop", "stop moving and end a walk", "stop", Available, null),
            new("cancel", "call off every action in flight", "cancel", Available, null),
            Check("logout", "log out to the character list", "logout", self.IsAirborne ? LoginVerbs.MidAir : null),
        ];
    }

    private static JsonObject Character(Situation situation, int nearby) => new()
    {
        ["guid"] = situation.Self.LocalObjectId == 0u ? null : Facts.Hex(situation.Self.LocalObjectId),
        ["stance"] = WireNames.Kebab(situation.Stance.ToString()),
        ["selection"] = situation.Selected is { } selected ? Facts.Id(selected, situation.NameOf(selected)) : null,
        ["vendor"] = situation.Vendor == 0u ? null : Facts.Id(situation.Vendor, situation.VendorName),
        ["container"] = situation.Container == 0u ? null : Facts.Id(situation.Container, situation.NameOf(situation.Container)),
        ["actionsInFlight"] = situation.Pending,
        ["actions"] = Rows(CharacterLines(situation, nearby)),
    };

    /// <summary>
    /// The lines graded alike, id aside, for every object in one answer, said once
    /// with the id left as a hole. Worked out per answer, so an object that
    /// differs keeps its own line on its row.
    /// </summary>
    internal static List<Capability> Legend(IReadOnlyList<(uint ObjectId, List<Capability> Actions)> rows)
    {
        var legend = new List<Capability>();
        if (rows.Count == 0)
            return legend;
        foreach (Capability first in rows[0].Actions)
        {
            Capability shape = Abstract(first, rows[0].ObjectId);
            bool everywhere = rows.All(row => row.Actions.Any(capability =>
                capability.Verb == first.Verb && Abstract(capability, row.ObjectId) == shape));
            if (everywhere)
                legend.Add(shape);
        }
        return legend;
    }

    private static Capability Abstract(Capability capability, uint objectId)
    {
        string hex = Facts.Hex(objectId);
        return capability with
        {
            Line = capability.Line.Replace(hex, GuidHole, StringComparison.Ordinal),
            Because = capability.Because?.Replace(hex, GuidHole, StringComparison.Ordinal),
        };
    }

    /// <summary>Selling a carried item, graded by the client's own check of the sale.</summary>
    private static Capability Sale(Situation situation, uint objectId)
    {
        string label = situation.VendorName is { } vendor ? $"sell it to {vendor}" : "sell it to the open vendor";
        string line = $"sell {Facts.Hex(objectId)}";
        if (situation.Vendor == 0u)
            return new("sell", label, line, Unavailable, VendorVerbs.NoVendor);
        PluginItemCommandResult check = situation.Automation.Items.CheckSell(objectId);
        return check.Status switch
        {
            PluginItemCommandStatus.Started => new("sell", label, line, Available, null),
            PluginItemCommandStatus.Unavailable =>
                new("sell", label, line, Unknown, "this client cannot check a sale before the item is offered"),
            _ => new("sell", label, line, Unavailable,
                check.Notice ?? $"the client would not offer it ({WireNames.Kebab(check.Status.ToString())})"),
        };
    }

    /// <summary>Why taking an item from a container would be refused, or null when it would be taken.</summary>
    private static string? LootProblem(Situation situation, in PluginWorldObject value)
    {
        if (situation.Container == 0u)
            return LootVerbs.NoContainer;
        uint holder = value.ContainerObjectId;
        for (int depth = 0; holder != 0u && depth < 4; depth++)
        {
            if (holder == situation.Container)
                return null;
            holder = situation.Automation.Objects.TryGet(holder, out PluginWorldObject parent)
                ? parent.ContainerObjectId
                : 0u;
        }
        return $"it is inside {Facts.Hex(value.ContainerObjectId)}, which is not the open container";
    }

    private static Capability Check(string verb, string label, string line, string? problem) =>
        new(verb, label, line, problem is null ? Available : Unavailable, problem);

    private static (string Word, string? Because) Reach(in PluginWorldObject value) =>
        value.IsOwned
            ? ("carried", "the character carries or wields it, so it has no place of its own")
            : value.ContainerObjectId != 0u
                ? ("inside", $"it is inside {Facts.Hex(value.ContainerObjectId)}")
                : !value.HasPosition
                    ? ("unplaced", "the client has not been told where it is")
                    : ("placed", null);

    private static JsonArray Rows(IEnumerable<Capability> capabilities)
    {
        var rows = new JsonArray();
        foreach (Capability capability in capabilities)
        {
            rows.Add(new JsonObject
            {
                ["verb"] = capability.Verb,
                ["label"] = capability.Label,
                ["line"] = capability.Line,
                ["verdict"] = capability.Verdict,
                ["because"] = capability.Because,
            });
        }
        return rows;
    }

    private VerbResult Refuse(CommandLine line, string reason)
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

    /// <summary>What one answer grades against, read once so every line is judged at the same moment.</summary>
    private sealed class Situation
    {
        private Situation(IAutomationSurface automation) => Automation = automation;

        internal IAutomationSurface Automation { get; }

        internal PluginNavigationSnapshot Self { get; private init; }

        internal PluginCombatMode Stance { get; private init; }

        internal HashSet<uint> Hostile { get; private init; } = [];

        internal uint? Selected { get; private init; }

        internal uint Vendor { get; private init; }

        internal uint Container { get; private init; }

        internal int Pending { get; private init; }

        internal string? VendorName => Vendor == 0u ? null : NameOf(Vendor);

        internal string? NameOf(uint id) =>
            Automation.Objects.TryGet(id, out PluginWorldObject value) ? value.Name : null;

        internal static Situation Read(IPluginHost host, OutcomeCorrelator outcomes)
        {
            IAutomationSurface automation = host.Automation;
            return new Situation(automation)
            {
                Self = automation.Navigation.Snapshot,
                Stance = automation.Combat.Snapshot.Mode,
                Hostile = automation.Combat.CaptureHostileTargets(HostileSearchMeters)
                    .Select(target => target.ObjectId)
                    .ToHashSet(),
                Selected = host.Selection.SelectedObjectId,
                Vendor = automation.Items.ActiveVendorObjectId,
                Container = automation.Loot.CurrentContainerId,
                Pending = outcomes.PendingCount,
            };
        }
    }
}
