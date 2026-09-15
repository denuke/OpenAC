using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AcDream.Core.Player;

/// <summary>
/// The client's own built-in skill-name list — the one it uses for an
/// appraisal's weapon-skill line and for the salvage message. It is NOT the
/// same list a skill window reads: those names come from the authored skill
/// data, and the two disagree.
/// <para>
/// The disagreement is the game's, not ours. This list keeps the original
/// names for three skills the authored data later renamed or dropped
/// ("Person Appraisal" and "Creature Appraisal" against the authored "Assess
/// Person" and "Assess Creature"; "Armor Repair", which the authored data no
/// longer carries at all), and it never learned Shield. Both callers simply
/// leave out a skill this list cannot name, so do not "complete" it — a name
/// added here would appear in text the game leaves blank.
/// </para>
/// </summary>
public static class RetailSkillNames
{
    /// <summary>The built-in name for a skill, if this list has one.</summary>
    public static bool TryGetName(
        int skillId,
        [MaybeNullWhen(false)] out string name)
    {
        name = skillId switch
        {
            1 => "Axe",
            2 => "Bow",
            3 => "Crossbow",
            4 => "Dagger",
            5 => "Mace",
            6 => "Melee Defense",
            7 => "Missile Defense",
            8 => "Sling",
            9 => "Spear",
            10 => "Staff",
            11 => "Sword",
            12 => "Thrown Weapon",
            13 => "Unarmed Combat",
            14 => "Arcane Lore",
            15 => "Magic Defense",
            16 => "Mana Conversion",
            17 => "Spellcraft",
            18 => "Item Tinkering",
            19 => "Person Appraisal",
            20 => "Deception",
            21 => "Healing",
            22 => "Jump",
            23 => "Lockpick",
            24 => "Run",
            25 => "Awareness",
            26 => "Armor Repair",
            27 => "Creature Appraisal",
            28 => "Weapon Tinkering",
            29 => "Armor Tinkering",
            30 => "Magic Item Tinkering",
            31 => "Creature Enchantment",
            32 => "Item Enchantment",
            33 => "Life Magic",
            34 => "War Magic",
            35 => "Leadership",
            36 => "Loyalty",
            37 => "Fletching",
            38 => "Alchemy",
            39 => "Cooking",
            40 => "Salvaging",
            41 => "Two Handed Combat",
            42 => "Gearcraft",
            43 => "Void Magic",
            44 => "Heavy Weapons",
            45 => "Light Weapons",
            46 => "Finesse Weapons",
            47 => "Missile Weapons",
            // 48 (Shield) is absent on purpose: the built-in list has no entry.
            49 => "Dual Wield",
            50 => "Recklessness",
            51 => "Sneak Attack",
            52 => "Dirty Fighting",
            53 => "Challenge",
            54 => "Summoning",
            _ => null,
        };
        return name is not null;
    }

    /// <summary>
    /// A label for a skill no authored name was found for. Only reachable
    /// where we show a skill the authored data did not name — the game itself
    /// has no such screen, so the numbered form is ours, for a host running
    /// without client data.
    /// </summary>
    public static string Describe(int skillId)
        => TryGetName(skillId, out string? name)
            ? name
            : $"Skill {skillId.ToString(CultureInfo.InvariantCulture)}";
}
