namespace AcDream.Plugins.Agent.Contract;

/// <summary>
/// Maps each terminal outcome word, per record kind, onto its shared class.
/// Words are never renamed to fit; a new word is a new row.
/// </summary>
internal static class OutcomeTable
{
    private static readonly Dictionary<(string Kind, string Word), OutcomeClass> Rows = new()
    {
        [(RecordKinds.CommandOutcome, "handled")] = OutcomeClass.Confirmed,
        [(RecordKinds.CommandOutcome, "chat")] = OutcomeClass.Confirmed,
        [(RecordKinds.CommandOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.CommandOutcome, "failed")] = OutcomeClass.Withdrawn,
        [(RecordKinds.TargetOutcome, "completed")] = OutcomeClass.Confirmed,
        [(RecordKinds.TargetOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.TargetOutcome, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.GoalResolved, "completed")] = OutcomeClass.Confirmed,
        [(RecordKinds.GoalResolved, "cancelled")] = OutcomeClass.Withdrawn,
        [(RecordKinds.GoalResolved, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.GoalResolved, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.GoalResolved, "blocked")] = OutcomeClass.Unreachable,
        [(RecordKinds.GoalResolved, "no-route")] = OutcomeClass.Unreachable,
        [(RecordKinds.CastOutcome, "accepted")] = OutcomeClass.Confirmed,
        [(RecordKinds.CastOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.CastOutcome, "unattributable")] = OutcomeClass.Unattributable,
        [(RecordKinds.CastOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.CastOutcome, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.ObjectOutcome, "completed")] = OutcomeClass.Confirmed,
        [(RecordKinds.ObjectOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.ObjectOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.ObjectOutcome, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.InventoryOutcome, "completed")] = OutcomeClass.Confirmed,
        [(RecordKinds.InventoryOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.InventoryOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.InventoryOutcome, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.AttackOutcome, "ended")] = OutcomeClass.Confirmed,
        [(RecordKinds.AttackOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.AttackOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.AttackOutcome, "lost")] = OutcomeClass.Lost,
        [(RecordKinds.LoginOutcome, "completed")] = OutcomeClass.Confirmed,
        [(RecordKinds.LoginOutcome, "refused")] = OutcomeClass.Refused,
        [(RecordKinds.LoginOutcome, "unconfirmed")] = OutcomeClass.Unconfirmed,
        [(RecordKinds.LoginOutcome, "lost")] = OutcomeClass.Lost,
    };

    /// <summary>Every record kind that carries an outcome word.</summary>
    internal static IReadOnlyCollection<string> Kinds =>
        Rows.Keys.Select(key => key.Kind).Distinct(StringComparer.Ordinal).ToArray();

    internal static IEnumerable<string> WordsFor(string kind) =>
        Rows.Keys.Where(key => key.Kind == kind).Select(key => key.Word);

    internal static string ClassOf(string kind, string word) =>
        Rows.TryGetValue((kind, word), out OutcomeClass outcome)
            ? OutcomeClasses.WireName(outcome)
            : throw new ArgumentException(
                $"No outcome class is recorded for {kind} '{word}'.",
                nameof(word));
}
