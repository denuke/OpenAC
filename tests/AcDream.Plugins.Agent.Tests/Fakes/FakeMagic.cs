using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakeSpells : ISpellCatalog
{
    public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; set; } = [];
    public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; set; } = [];
    public IReadOnlyList<PluginSpellInfo> KnownCombatSpells { get; set; } = [];

    internal Dictionary<uint, PluginSpellInfo> Catalog { get; } = [];

    public bool IsKnown(uint spellId) =>
        KnownSelfBuffs.Concat(KnownAttackSpells).Concat(KnownCombatSpells)
            .Any(spell => spell.SpellId == spellId);

    public bool TryGet(uint spellId, out PluginSpellInfo info) =>
        Catalog.TryGetValue(spellId, out info);

    internal static PluginSpellInfo Spell(
        uint id,
        string name,
        bool selfTargeted = true,
        bool beneficial = true) =>
        new(id, name, 1u, 1, 50, 10, 1800f, 4u, string.Empty, selfTargeted, beneficial);
}

internal sealed class FakeMagic : IMagicCommands
{
    public bool IsCasting { get; set; }
    public PluginCastCompletion LastCompletion { get; set; }

    internal PluginCastGate Gate { get; set; } = PluginCastGate.Ready;
    internal PluginCastRequestResult Request { get; set; } = PluginCastRequestResult.Sent;
    internal HashSet<uint> MissingComponents { get; } = [];
    internal List<(uint SpellId, uint? TargetObjectId)> Requests { get; } = [];

    public PluginCastGate EvaluateGate(uint spellId) => Gate;

    public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) => Gate;

    public bool Cast(uint spellId) => RequestCast(spellId) == PluginCastRequestResult.Sent;

    public bool Cast(uint spellId, uint targetObjectId) =>
        RequestCast(spellId, targetObjectId) == PluginCastRequestResult.Sent;

    public PluginCastRequestResult RequestCast(uint spellId)
    {
        Requests.Add((spellId, null));
        return Request;
    }

    public PluginCastRequestResult RequestCast(uint spellId, uint targetObjectId)
    {
        Requests.Add((spellId, targetObjectId));
        return Request;
    }

    public bool HasComponents(uint spellId) => !MissingComponents.Contains(spellId);
}
