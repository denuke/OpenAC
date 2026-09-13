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
    internal const string TargetSent = "target-sent";
    internal const string TargetRefused = "target-refused";
    internal const string TargetOutcome = "target-outcome";
    internal const string GoalAccepted = "goal-accepted";
    internal const string GoalResolved = "goal-resolved";
    internal const string CastSent = "cast-sent";
    internal const string CastRefused = "cast-refused";
    internal const string CastOutcome = "cast-outcome";
    internal const string ObjectAction = "object-action";
    internal const string ObjectRefused = "object-refused";
    internal const string ObjectOutcome = "object-outcome";
    internal const string ContainerContents = "container-contents";
    internal const string Corpses = "corpses";
    internal const string InventoryAction = "inventory-action";
    internal const string InventoryRefused = "inventory-refused";
    internal const string InventoryOutcome = "inventory-outcome";
    internal const string Inventory = "inventory";
    internal const string Equipment = "equipment";
    internal const string Vendor = "vendor";
    internal const string VendorRefused = "vendor-refused";
    internal const string AttackSent = "attack-sent";
    internal const string AttackRefused = "attack-refused";
    internal const string AttackOutcome = "attack-outcome";
    internal const string Characters = "characters";
    internal const string LoginSent = "login-sent";
    internal const string LoginRefused = "login-refused";
    internal const string LoginOutcome = "login-outcome";

    /// <summary>
    /// Kinds that describe current state. The latest record of each is kept
    /// for readers even after it scrolls out of the recent records.
    /// </summary>
    internal static readonly IReadOnlyList<string> State =
        [Session, Body, Vitals, Stats, Target, CombatMode];
}
