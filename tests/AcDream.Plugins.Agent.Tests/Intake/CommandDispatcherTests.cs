using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Intake;

public sealed class CommandDispatcherTests
{
    [Fact]
    public void AReservedWordGoesToItsFamilyAndGetsOneOutcome()
    {
        var (host, dispatcher, ring) = Build();
        var family = new RecordingFamily(["wave"]);
        dispatcher.Register(family);

        CommandReceipt receipt = dispatcher.Deliver("Wave at everyone", "mcp");

        Assert.Equal("handled", receipt.Outcome);
        Assert.Equal("at everyone", Assert.Single(family.Lines).Arguments);
        Assert.Empty(host.FakeAutomation.FakeChat.Submitted);
        JsonElement outcome = Single(ring, RecordKinds.CommandOutcome);
        Assert.Equal(receipt.Id, outcome.GetProperty("id").GetInt64());
        Assert.Equal("wave", outcome.GetProperty("verb").GetString());
        Assert.Equal("handled", outcome.GetProperty("outcome").GetString());
        Assert.Equal("confirmed", outcome.GetProperty("class").GetString());
    }

    [Fact]
    public void AnUnreservedWordIsSubmittedAsChat()
    {
        var (host, dispatcher, ring) = Build();

        CommandReceipt receipt = dispatcher.Deliver("hello there", "mcp");

        Assert.Equal("chat", receipt.Outcome);
        Assert.Equal(["hello there"], host.FakeAutomation.FakeChat.Submitted);
        Assert.Equal("chat", Single(ring, RecordKinds.CommandOutcome).GetProperty("outcome").GetString());
    }

    [Fact]
    public void OutOfTheWorldNothingIsSubmitted()
    {
        var (host, dispatcher, _) = Build();
        host.FakeAutomation.IsAvailable = false;

        CommandReceipt receipt = dispatcher.Deliver("hello", "mcp");

        Assert.Equal("refused", receipt.Outcome);
        Assert.Empty(host.FakeAutomation.FakeChat.Submitted);
    }

    [Theory]
    [InlineData("/agent status")]
    [InlineData("@agent record off")]
    [InlineData("/logout")]
    [InlineData("/quit")]
    [InlineData("/die")]
    [InlineData("/pklite")]
    [InlineData("@pka")]
    public void AgentLoopsAndSessionEndingCommandsAreNeverSubmitted(string text)
    {
        var (host, dispatcher, _) = Build();

        CommandReceipt receipt = dispatcher.Deliver(text, "mcp");

        Assert.Equal("refused", receipt.Outcome);
        Assert.Empty(host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public void AnEmptyLineIsRefused()
    {
        var (_, dispatcher, _) = Build();

        Assert.Equal("refused", dispatcher.Deliver("   ", "mcp").Outcome);
    }

    [Fact]
    public void AVerbThatThrowsIsReportedAsFailedAndCounted()
    {
        var (_, dispatcher, ring) = Build();
        dispatcher.Register(new RecordingFamily(["boom"]) { Throws = true });

        CommandReceipt receipt = dispatcher.Deliver("boom", "mcp");

        Assert.Equal("failed", receipt.Outcome);
        Assert.Equal(1, dispatcher.Failures);
        Assert.Equal("withdrawn", Single(ring, RecordKinds.CommandOutcome).GetProperty("class").GetString());
    }

    [Fact]
    public void TwoFamiliesCannotReserveTheSameWord()
    {
        var (_, dispatcher, _) = Build();
        dispatcher.Register(new RecordingFamily(["wave"]));

        Assert.Throws<InvalidOperationException>(
            () => dispatcher.Register(new RecordingFamily(["WAVE"])));
    }

    [Fact]
    public void IdsAreDenseFromOne()
    {
        var (_, dispatcher, _) = Build();

        Assert.Equal(1, dispatcher.Deliver("a", "mcp").Id);
        Assert.Equal(2, dispatcher.Deliver("b", "mcp").Id);
    }

    [Fact]
    public void EveryWordTheDispatcherCanEmitHasAnOutcomeClass()
    {
        Assert.Equal(
            CommandDispatcher.OutcomeWords.Order(StringComparer.Ordinal),
            OutcomeTable.WordsFor(RecordKinds.CommandOutcome).Order(StringComparer.Ordinal));
    }

    private static JsonElement Single(RecordRing ring, string kind) =>
        JsonDocument.Parse(Assert.Single(ring.Read(-1, new HashSet<string> { kind }).Records).Json)
            .RootElement;

    private static (FakePluginHost Host, CommandDispatcher Dispatcher, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var ring = new RecordRing(64);
        var publisher = new Publisher(new AgentClock(), ring);
        return (host, new CommandDispatcher(host, publisher), ring);
    }

    private sealed class RecordingFamily(IReadOnlyCollection<string> words) : IVerbFamily
    {
        public IReadOnlyCollection<string> ReservedWords { get; } = words;
        internal List<CommandLine> Lines { get; } = [];
        internal bool Throws { get; init; }

        public VerbResult Handle(CommandLine line)
        {
            if (Throws)
                throw new InvalidOperationException("verb failed");
            Lines.Add(line);
            return VerbResult.Handled;
        }
    }
}
