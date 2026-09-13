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
    };

    internal static IEnumerable<string> WordsFor(string kind) =>
        Rows.Keys.Where(key => key.Kind == kind).Select(key => key.Word);

    internal static string ClassOf(string kind, string word) =>
        Rows.TryGetValue((kind, word), out OutcomeClass outcome)
            ? OutcomeClasses.WireName(outcome)
            : throw new ArgumentException(
                $"No outcome class is recorded for {kind} '{word}'.",
                nameof(word));
}
