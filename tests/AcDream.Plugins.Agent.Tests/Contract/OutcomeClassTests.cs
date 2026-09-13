using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Tests.Contract;

public sealed class OutcomeClassTests
{
    [Fact]
    public void EveryClassHasADistinctWireName()
    {
        string[] names = Enum.GetValues<OutcomeClass>()
            .Select(OutcomeClasses.WireName)
            .ToArray();

        Assert.Equal(
            [
                "confirmed",
                "partial",
                "refused",
                "withdrawn",
                "unconfirmed",
                "unattributable",
                "unreachable",
                "lost",
                "in-flight",
            ],
            names);
    }
}
