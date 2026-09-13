using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Contract;

public sealed class OutcomeTableTests
{
    public static TheoryData<string, string[]> Families => new()
    {
        { RecordKinds.TargetOutcome, [.. TargetVerbs.OutcomeWords, .. OutcomeCorrelator.OutcomeWords] },
        { RecordKinds.GoalResolved, [.. MotorVerbs.OutcomeWords, .. OutcomeCorrelator.OutcomeWords] },
        { RecordKinds.CastOutcome, [.. CastVerbs.OutcomeWords, .. OutcomeCorrelator.OutcomeWords] },
        { RecordKinds.ObjectOutcome, [.. ObjectVerbs.OutcomeWords, .. OutcomeCorrelator.OutcomeWords] },
        { RecordKinds.InventoryOutcome, [.. InventoryOutcomes.Words, .. OutcomeCorrelator.OutcomeWords] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void EveryWordAFamilyCanEndWithHasAClassAndNoRowIsUnused(string kind, string[] words)
    {
        Assert.Equal(
            words.Order(StringComparer.Ordinal),
            OutcomeTable.WordsFor(kind).Order(StringComparer.Ordinal));
    }
}
