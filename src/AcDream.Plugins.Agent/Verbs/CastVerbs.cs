using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>cast &lt;spell name or id&gt; [on &lt;object id&gt;]</c>. A self or untargeted
/// spell needs no target; any other spell uses the given object or the
/// current selection. Casting at a given object selects it, as the client does.
/// </summary>
internal sealed class CastVerbs : IVerbFamily
{
    internal const double WindowSeconds = 15d;
    internal const string Accepted = "accepted";
    internal const string Refused = "refused";
    internal const string Unattributable = "unattributable";

    internal static readonly IReadOnlyList<string> OutcomeWords =
        [Accepted, Refused, Unattributable];

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal CastVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["cast"];

    public VerbResult Handle(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return Refuse(line, "no character is in the world");

        (string spellText, uint? target) = SplitTarget(line.Arguments);
        if (spellText.Length == 0)
            return Refuse(line, "usage: cast <spell name or id> [on <object id>]");

        SpellMatch match = SpellResolution.Resolve(automation.Spells, spellText);
        if (match.Status == SpellMatchStatus.NotFound)
        {
            return Refuse(line,
                $"the character knows no spell matching '{spellText}'; 'spells' lists what it knows");
        }
        if (match.Status == SpellMatchStatus.Ambiguous)
        {
            var candidates = new JsonArray();
            foreach (PluginSpellInfo candidate in match.Candidates)
                candidates.Add(new JsonObject { ["id"] = candidate.SpellId, ["name"] = candidate.Name });
            return Refuse(line, $"which one? {match.Total} known spells match '{spellText}'",
                word: "ambiguous",
                extra: new JsonObject { ["candidates"] = candidates, ["total"] = match.Total });
        }

        PluginSpellInfo spell = match.Spell;
        bool needsTarget = !spell.IsSelfTargeted && !spell.IsUntargeted;
        if (target is { } explicitTarget)
        {
            if (!needsTarget)
                return Refuse(line, $"{spell.Name} does not take a target");
            if (!automation.Objects.TryGet(explicitTarget, out _))
                return Refuse(line, "the client holds no object with that id");
        }
        else if (needsTarget && _host.Selection.SelectedObjectId is null)
        {
            return Refuse(line, $"{spell.Name} needs a target: select one, or add 'on <object id>'",
                word: "no-target-selected");
        }

        IMagicCommands magic = automation.Magic;
        if (!magic.HasComponents(spell.SpellId))
        {
            return Refuse(line, $"the character lacks the components for {spell.Name}",
                word: "missing-components");
        }
        if (target is null)
        {
            PluginCastGate gate = magic.EvaluateGate(spell.SpellId);
            if (gate != PluginCastGate.Ready)
                return Refuse(line, GateReason(gate, spell), word: WireNames.Kebab(gate.ToString()));
        }
        else if (magic.IsCasting)
        {
            return Refuse(line, "the character is already casting", word: "busy");
        }

        long baseline = magic.LastCompletion.Revision;
        PluginCastRequestResult result = target is { } aimed
            ? magic.RequestCast(spell.SpellId, aimed)
            : magic.RequestCast(spell.SpellId);
        if (result != PluginCastRequestResult.Sent)
        {
            return Refuse(line, $"the client did not send the cast ({WireNames.Kebab(result.ToString())})",
                word: WireNames.Kebab(result.ToString()));
        }

        _publisher.Publish(RecordKinds.CastSent, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["spellId"] = spell.SpellId,
            ["name"] = spell.Name,
            ["target"] = target is { } sent ? Facts.Hex(sent) : null,
            ["selectionChanged"] = target is not null,
        });

        uint spellId = spell.SpellId;
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.CastOutcome, WindowSeconds, () =>
        {
            PluginCastCompletion completion = magic.LastCompletion;
            if (completion.Revision <= baseline)
                return null;
            if (completion.SpellId != spellId)
            {
                return new Resolution(Unattributable,
                    "the next cast answer was for a different spell",
                    new JsonObject { ["spellId"] = spellId, ["answeredSpellId"] = completion.SpellId });
            }
            var fields = new JsonObject
            {
                ["spellId"] = spellId,
                ["weenieError"] = completion.WeenieError,
            };
            return completion.WeenieError == 0u
                ? new Resolution(Accepted, null, fields)
                : new Resolution(Refused,
                    $"the server refused the cast with error 0x{completion.WeenieError:X4}",
                    fields);
        });
        return VerbResult.Handled;
    }

    /// <summary>
    /// Splits a trailing <c>on &lt;object id&gt;</c> from the spell text. The split
    /// happens only when what follows really is an id, so a spell name that
    /// contains the word "on" stays whole.
    /// </summary>
    private static (string Spell, uint? Target) SplitTarget(string arguments)
    {
        string trimmed = arguments.Trim();
        int index = trimmed.LastIndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (index > 0 && Guids.TryParse(trimmed[(index + 4)..], out uint target))
            return (trimmed[..index].Trim(), target);
        return (trimmed, null);
    }

    private static string GateReason(PluginCastGate gate, in PluginSpellInfo spell) => gate switch
    {
        PluginCastGate.NotKnown => $"the character does not know {spell.Name}",
        PluginCastGate.Busy => "the character is already casting",
        PluginCastGate.NoTargetSelected => $"{spell.Name} needs a target: select one, or add 'on <object id>'",
        PluginCastGate.TargetIncompatible => $"the selected object is not a valid target for {spell.Name}",
        PluginCastGate.Unavailable => "casting is unavailable right now",
        _ => "the client refused the cast",
    };

    private VerbResult Refuse(
        CommandLine line,
        string reason,
        string? word = null,
        JsonObject? extra = null)
    {
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["word"] = word,
            ["reason"] = reason,
        };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> field in extra.ToList())
            {
                extra.Remove(field.Key);
                fields[field.Key] = field.Value;
            }
        }
        _publisher.Publish(RecordKinds.CastRefused, fields);
        return VerbResult.Refused(reason);
    }
}
