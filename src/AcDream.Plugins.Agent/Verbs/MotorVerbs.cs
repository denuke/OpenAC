using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// Moving in place: <c>turn to &lt;heading&gt;</c>, <c>face &lt;guid&gt;</c>, <c>jump</c>,
/// <c>stance &lt;mode&gt;</c> and <c>cancel</c>. Travel to a place is not provided.
/// </summary>
internal sealed class MotorVerbs : IVerbFamily
{
    internal const double HeadingToleranceDegrees = 5d;
    internal const double TurnWindowSeconds = 5d;
    internal const double JumpWindowSeconds = 2d;
    internal const double StanceWindowSeconds = 5d;
    internal const string Completed = "completed";
    internal const string Cancelled = "cancelled";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Completed, Cancelled];

    private static readonly Dictionary<string, PluginCombatMode> Stances =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["peace"] = PluginCombatMode.Peace,
            ["melee"] = PluginCombatMode.Melee,
            ["missile"] = PluginCombatMode.Missile,
            ["magic"] = PluginCombatMode.Magic,
        };

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal MotorVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } =
        ["turn", "face", "jump", "stance", "cancel"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        return line.Verb switch
        {
            "turn" => Turn(line),
            "face" => Face(line),
            "jump" => Jump(line),
            "stance" => Stance(line),
            _ => Cancel(line),
        };
    }

    private VerbResult Turn(CommandLine line)
    {
        string[] words = line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2
            || !words[0].Equals("to", StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double heading)
            || !double.IsFinite(heading))
        {
            return Refuse(line, "usage: turn to <compass heading in degrees>");
        }
        return TurnToward(line, ((heading % 360d) + 360d) % 360d, facing: null);
    }

    private VerbResult Face(CommandLine line)
    {
        if (!Guids.TryParse(line.Arguments, out uint id))
            return Refuse(line, "face needs an object id such as 0x70000001");
        IAutomationSurface automation = _host.Automation;
        if (!automation.Objects.TryGet(id, out PluginWorldObject value) || !value.HasPosition)
            return Refuse(line, "the client holds no position for that object");
        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        if (!self.IsAvailable)
            return Refuse(line, "the client has no position for its own body");
        return TurnToward(line, Geometry.BearingDegrees(self.Position, value.Position), Facts.Hex(id));
    }

    private VerbResult TurnToward(CommandLine line, double heading, string? facing)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        if (navigation.FaceHeading((float)heading) != PluginNavigationCommandStatus.Accepted)
            return Refuse(line, "the client did not accept the turn");
        Accepted(line, new JsonObject
        {
            ["heading"] = Math.Round(heading, 1),
            ["facing"] = facing,
        });
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, TurnWindowSeconds, () =>
        {
            PluginNavigationSnapshot snapshot = navigation.Snapshot;
            return snapshot.IsAvailable
                && Math.Abs(Geometry.RelativeDegrees(heading, snapshot.Position.HeadingDegrees))
                    <= HeadingToleranceDegrees
                ? new Resolution(Completed)
                : null;
        });
        return VerbResult.Handled;
    }

    private VerbResult Jump(CommandLine line)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        if (navigation.SetMovementIntent(new PluginMovementIntent(Jump: true))
            != PluginNavigationCommandStatus.Accepted)
        {
            return Refuse(line, "the client did not accept the jump");
        }
        navigation.ClearMovementIntent();
        Accepted(line, new JsonObject());
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, JumpWindowSeconds,
            () => navigation.Snapshot.IsAirborne ? new Resolution(Completed) : null);
        return VerbResult.Handled;
    }

    private VerbResult Stance(CommandLine line)
    {
        if (!Stances.TryGetValue(line.Arguments.Trim(), out PluginCombatMode mode))
            return Refuse(line, "usage: stance peace|melee|missile|magic");
        ICombatAutomation combat = _host.Automation.Combat;
        PluginCombatCommandResult result = combat.EnterMode(mode);
        if (!result.Accepted)
        {
            return Refuse(line, result.Notice
                ?? $"the client did not change stance ({WireNames.Kebab(result.Status.ToString())})");
        }
        Accepted(line, new JsonObject { ["stance"] = WireNames.Kebab(mode.ToString()) });
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, StanceWindowSeconds,
            () => combat.Snapshot.Mode == mode ? new Resolution(Completed) : null);
        return VerbResult.Handled;
    }

    private VerbResult Cancel(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        automation.Navigation.ClearMovementIntent();
        automation.Combat.AbortPhysicalAttack();
        int ended = _outcomes.EndAll(
            RecordKinds.GoalResolved,
            Cancelled,
            "a later 'cancel' stopped it");
        Accepted(line, new JsonObject { ["cancelled"] = ended });
        _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.GoalResolved, new Resolution(Completed));
        return VerbResult.Handled;
    }

    private void Accepted(CommandLine line, JsonObject details)
    {
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
        };
        foreach (KeyValuePair<string, JsonNode?> detail in details.ToList())
        {
            details.Remove(detail.Key);
            fields[detail.Key] = detail.Value;
        }
        _publisher.Publish(RecordKinds.GoalAccepted, fields);
    }

    private VerbResult Refuse(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.GoalRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
