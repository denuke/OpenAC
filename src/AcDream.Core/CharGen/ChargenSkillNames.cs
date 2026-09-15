using AcDream.Core.Player;

namespace AcDream.Core.CharGen;

/// <summary>
/// Character creation names a skill from the authored skill data, the same
/// source every other skill list uses. The built-in list is only a last
/// resort for a skill that data did not name, which cannot happen against
/// real client data — the creation tables and the skill table come from the
/// same file, so a session that has one has the other.
/// </summary>
public static class ChargenSkillNames
{
    public static string Resolve(ChargenOptions options, uint skillId)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.TryGetSkillDetail(skillId, out ChargenSkillDetail detail)
               && !string.IsNullOrWhiteSpace(detail.Name)
            ? detail.Name
            : RetailSkillNames.Describe((int)skillId);
    }
}
