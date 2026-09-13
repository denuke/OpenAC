using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Verbs;

internal enum SpellMatchStatus
{
    Found,
    Ambiguous,
    NotFound,
}

/// <summary>How a spell name or id resolved against what the character knows.</summary>
internal readonly record struct SpellMatch(
    SpellMatchStatus Status,
    PluginSpellInfo Spell,
    IReadOnlyList<PluginSpellInfo> Candidates,
    int Total);

internal static class SpellResolution
{
    internal const int CandidateLimit = 10;

    /// <summary>
    /// Finds a known spell by id, by exact name, or by the only name containing
    /// the text. Several matches are ambiguous and are returned as candidates.
    /// </summary>
    internal static SpellMatch Resolve(ISpellCatalog catalog, string text)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(text);
        string wanted = text.Trim();
        IReadOnlyList<PluginSpellInfo> known = SpellLists.Known(catalog);

        if (uint.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
        {
            foreach (PluginSpellInfo spell in known)
            {
                if (spell.SpellId == id)
                    return new SpellMatch(SpellMatchStatus.Found, spell, [spell], 1);
            }
            return new SpellMatch(SpellMatchStatus.NotFound, default, [], 0);
        }

        PluginSpellInfo[] exact = known
            .Where(spell => spell.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        PluginSpellInfo[] matches = exact.Length > 0
            ? exact
            : known
                .Where(spell => spell.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        return matches.Length switch
        {
            0 => new SpellMatch(SpellMatchStatus.NotFound, default, [], 0),
            1 => new SpellMatch(SpellMatchStatus.Found, matches[0], matches, 1),
            _ => new SpellMatch(
                SpellMatchStatus.Ambiguous,
                default,
                matches.Take(CandidateLimit).ToArray(),
                matches.Length),
        };
    }
}
