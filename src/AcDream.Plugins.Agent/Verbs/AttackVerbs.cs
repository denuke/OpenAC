using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>attack [id] [power] [high|medium|low]</c>: a melee or missile attack on the
/// given object or the current selection. The attack charges and is released
/// once the power bar reaches the requested power, from 0 to 1. With the
/// client's repeat attack on, further swings follow by themselves, and the
/// outcome waits until they stop and counts them.
/// </summary>
internal sealed class AttackVerbs : IVerbFamily
{
    /// <summary>How long an attack's swings are waited on to stop, repeats included.</summary>
    internal const double WindowSeconds = 120d;

    internal const string WrongStance =
        "enter a melee or missile stance first: 'stance combat' with a melee or missile weapon wielded";
    internal const float DefaultPower = 0.5f;
    internal const string Ended = "ended";
    internal const string Refused = "refused";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Ended, Refused];

    private static readonly Dictionary<string, PluginAttackHeight> Heights =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = PluginAttackHeight.High,
            ["medium"] = PluginAttackHeight.Medium,
            ["low"] = PluginAttackHeight.Low,
        };

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal AttackVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["attack"];

    public VerbResult Handle(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return Refuse(line, "no character is in the world");

        uint? target = null;
        float power = DefaultPower;
        PluginAttackHeight height = PluginAttackHeight.Medium;
        foreach (string word in line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guids.TryParse(word, out uint id) && word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                target = id;
            else if (Heights.TryGetValue(word, out PluginAttackHeight named))
                height = named;
            else if (float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float asked)
                && asked is >= 0f and <= 1f)
                power = asked;
            else
                return Refuse(line, "usage: attack [object id] [power from 0 to 1] [high|medium|low]");
        }

        if ((target ?? _host.Selection.SelectedObjectId) is not { } aimed)
            return Refuse(line, "attack needs a target: select one, or give an object id", "no-target");

        ICombatAutomation combat = automation.Combat;
        PluginCombatSnapshot before = combat.Snapshot;
        if (before.Mode is not (PluginCombatMode.Melee or PluginCombatMode.Missile))
            return Refuse(line, WrongStance, "wrong-mode");

        long baseline = before.CompletionRevision;
        PluginCombatCommandResult result = combat.BeginPhysicalAttack(aimed, height, power);
        if (result.Status != PluginCombatCommandStatus.Started)
        {
            string word = WireNames.Kebab(result.Status.ToString());
            return Refuse(line, result.Notice ?? $"the client did not start the attack ({word})", word);
        }

        _publisher.Publish(RecordKinds.AttackSent, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["target"] = Facts.Hex(aimed),
            ["power"] = Math.Round(power, 2),
            ["height"] = WireNames.Kebab(height.ToString()),
        });

        bool released = false;
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.AttackOutcome, WindowSeconds, () =>
        {
            PluginCombatSnapshot now = combat.Snapshot;
            if (!released
                && (now.BuildInProgress || now.RequestInProgress)
                && now.PowerBarLevel >= power)
            {
                combat.ReleasePhysicalAttack();
                released = true;
            }
            if (now.CompletionRevision <= baseline
                || now.BuildInProgress
                || now.RequestInProgress
                || now.ServerResponsePending
                || now.RepeatAttackInProgress)
            {
                return null;
            }
            long swings = now.CompletionRevision - baseline;
            var fields = new JsonObject
            {
                ["swings"] = swings,
                ["weenieError"] = now.CompletionWeenieError,
            };
            if (now.CompletionWeenieError == 0u)
                return new Resolution(Ended, null, fields);
            return swings == 1
                ? new Resolution(Refused,
                    $"the server refused the attack with error 0x{now.CompletionWeenieError:X4}",
                    fields)
                : new Resolution(Ended,
                    $"the swings stopped on the server's answer, error 0x{now.CompletionWeenieError:X4}",
                    fields);
        });
        return VerbResult.Handled;
    }

    private VerbResult Refuse(CommandLine line, string reason, string? word = null)
    {
        _publisher.Publish(RecordKinds.AttackRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["word"] = word,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
