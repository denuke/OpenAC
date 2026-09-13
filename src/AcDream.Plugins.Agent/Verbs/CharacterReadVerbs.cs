using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// Reads about the character that send nothing to the server. Each answer is a
/// record carrying the line's <c>id</c>.
/// </summary>
internal sealed class CharacterReadVerbs : IVerbFamily
{
    private const string NotInWorld = "no character is in the world";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly StateTracker _state;
    private readonly AgentClock _clock;

    internal CharacterReadVerbs(
        IPluginHost host,
        Publisher publisher,
        StateTracker state,
        AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        _host = host;
        _publisher = publisher;
        _state = state;
        _clock = clock;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } =
        ["vitals", "stats", "location", "snapshot", "skills", "buffs", "spells"];

    public VerbResult Handle(CommandLine line)
    {
        switch (line.Verb)
        {
            case "vitals":
                return Fresh(RecordKinds.Vitals, line);
            case "stats":
                return Fresh(RecordKinds.Stats, line);
            case "location":
                return Fresh(RecordKinds.Body, line);
            case "snapshot":
                _state.PublishSnapshot(_clock.Now);
                return VerbResult.Handled;
            case "skills":
                Publish(RecordKinds.Skills, line, "skills", Skills);
                return VerbResult.Handled;
            case "buffs":
                Publish(RecordKinds.Buffs, line, "enchantments", Buffs);
                return VerbResult.Handled;
            default:
                return Spells(line);
        }
    }

    private VerbResult Fresh(string kind, CommandLine line) =>
        _state.PublishNow(kind, line.Id, _clock.Now)
            ? VerbResult.Handled
            : VerbResult.Refused($"no {kind} state is tracked");

    private void Publish(string kind, CommandLine line, string field, Func<JsonArray> build)
    {
        IAutomationSurface automation = _host.Automation;
        bool inWorld = automation.IsAvailable && automation.Character.IsInWorld;
        _publisher.Publish(kind, new JsonObject
        {
            ["id"] = line.Id,
            [field] = inWorld ? Facts.Observed(build()) : Facts.Unknown(NotInWorld),
        });
    }

    private JsonArray Skills()
    {
        var skills = new JsonArray();
        foreach (PluginSkillInfo skill in _host.Automation.Character.Skills)
        {
            skills.Add(new JsonObject
            {
                ["id"] = skill.SkillId,
                ["name"] = skill.Name,
                ["training"] = WireNames.Kebab(skill.Training.ToString()),
                ["current"] = skill.Current,
                ["base"] = skill.Base,
            });
        }
        return skills;
    }

    private JsonArray Buffs()
    {
        IAutomationSurface automation = _host.Automation;
        var enchantments = new JsonArray();
        foreach (PluginActiveEnchantment enchantment in automation.Character.ActiveEnchantments)
        {
            enchantments.Add(new JsonObject
            {
                ["spellId"] = enchantment.SpellId,
                ["name"] = automation.Spells.TryGet(enchantment.SpellId, out PluginSpellInfo spell)
                    ? spell.Name
                    : null,
                ["family"] = enchantment.Family,
                ["tier"] = enchantment.Tier,
                ["secondsRemaining"] = double.IsFinite(enchantment.SecondsRemaining)
                    ? Math.Round(enchantment.SecondsRemaining, 1)
                    : null,
            });
        }
        return enchantments;
    }

    /// <summary>The known spells, narrowed to names containing the search text when one is given.</summary>
    private VerbResult Spells(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        string search = line.Arguments;
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["search"] = search.Length == 0 ? null : search,
        };
        if (!(automation.IsAvailable && automation.Character.IsInWorld))
        {
            fields["known"] = Facts.Unknown(NotInWorld);
            fields["spells"] = Facts.Unknown(NotInWorld);
        }
        else
        {
            IReadOnlyList<PluginSpellInfo> known = SpellLists.Known(automation.Spells);
            var spells = new JsonArray();
            foreach (PluginSpellInfo spell in known)
            {
                if (search.Length == 0 || spell.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    spells.Add(SpellRow(automation, spell));
            }
            fields["known"] = Facts.Observed(known.Count);
            fields["spells"] = Facts.Observed(spells);
        }
        _publisher.Publish(RecordKinds.Spells, fields);
        return VerbResult.Handled;
    }

    private static JsonObject SpellRow(IAutomationSurface automation, in PluginSpellInfo spell) => new()
    {
        ["id"] = spell.SpellId,
        ["name"] = spell.Name,
        ["school"] = spell.School,
        ["tier"] = spell.Tier,
        ["difficulty"] = spell.Difficulty,
        ["manaCost"] = spell.ManaCost,
        ["selfTargeted"] = spell.IsSelfTargeted,
        ["untargeted"] = spell.IsUntargeted,
        ["beneficial"] = spell.IsBeneficial,
        ["offensive"] = spell.IsOffensive,
        ["debuff"] = spell.IsDebuff,
        ["hasComponents"] = automation.Magic.HasComponents(spell.SpellId),
    };
}

/// <summary>
/// The spells the character knows: the host's complete list when it has one,
/// otherwise every spell in its narrower lists.
/// </summary>
internal static class SpellLists
{
    internal static IReadOnlyList<PluginSpellInfo> Known(ISpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        IReadOnlyList<PluginSpellInfo> complete = catalog.KnownSpells;
        var byId = new Dictionary<uint, PluginSpellInfo>();
        if (complete.Count > 0)
        {
            foreach (PluginSpellInfo spell in complete)
                byId.TryAdd(spell.SpellId, spell);
            return Ordered(byId.Values);
        }
        foreach (PluginSpellInfo spell in catalog.KnownSelfBuffs
            .Concat(catalog.KnownAttackSpells)
            .Concat(catalog.KnownCombatSpells))
        {
            byId.TryAdd(spell.SpellId, spell);
        }
        return Ordered(byId.Values);
    }

    private static PluginSpellInfo[] Ordered(IEnumerable<PluginSpellInfo> spells) =>
        spells
            .OrderBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(spell => spell.SpellId)
            .ToArray();
}
