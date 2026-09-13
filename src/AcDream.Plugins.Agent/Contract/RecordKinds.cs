namespace AcDream.Plugins.Agent.Contract;

internal static class RecordKinds
{
    internal const string Session = "session";
    internal const string Body = "body";
    internal const string Vitals = "vitals";
    internal const string Stats = "stats";
    internal const string Target = "target";
    internal const string CombatMode = "combat-mode";
    internal const string VitalChanged = "vital-changed";
    internal const string Chat = "chat";
    internal const string CommandOutcome = "command-outcome";
    internal const string GoalRefused = "goal-refused";
    internal const string Skills = "skills";
    internal const string Buffs = "buffs";
    internal const string Spells = "spells";
    internal const string Nearby = "nearby";
    internal const string NearbyRefused = "nearby-refused";
    internal const string EntityInspected = "entity-inspected";
    internal const string EntityRefused = "entity-refused";

    /// <summary>
    /// Kinds that describe current state. The latest record of each is kept
    /// for readers even after it scrolls out of the recent records.
    /// </summary>
    internal static readonly IReadOnlyList<string> State =
        [Session, Body, Vitals, Stats, Target, CombatMode];
}
