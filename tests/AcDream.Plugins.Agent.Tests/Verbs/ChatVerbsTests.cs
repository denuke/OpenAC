using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class ChatVerbsTests
{
    [Fact]
    public void SaySubmitsTheWordsAsSpeech()
    {
        var (host, verbs) = Build();

        VerbResult result = verbs.Handle(Line("say hello there"));

        Assert.Equal("handled", result.Outcome);
        Assert.Equal(["hello there"], host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public void SpeechCannotSmuggleACommand()
    {
        var (host, verbs) = Build();

        VerbResult result = verbs.Handle(Line("say /logout"));

        Assert.Equal("refused", result.Outcome);
        Assert.Empty(host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public void TellNeedsANameAndAMessage()
    {
        var (host, verbs) = Build();

        Assert.Equal("refused", verbs.Handle(Line("tell Friend hello")).Outcome);
        Assert.Equal("handled", verbs.Handle(Line("tell Friend, hello")).Outcome);
        Assert.Equal(["/tell Friend, hello"], host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public void EmoteUsesTheEmoteCommand()
    {
        var (host, verbs) = Build();

        verbs.Handle(Line("emote waves"));

        Assert.Equal(["/emote waves"], host.FakeAutomation.FakeChat.Submitted);
    }

    [Fact]
    public void ALineTheClientRefusesIsReportedAsRefused()
    {
        var (host, verbs) = Build();
        host.FakeAutomation.FakeChat.AcceptsSubmit = false;

        Assert.Equal("refused", verbs.Handle(Line("say hi")).Outcome);
    }

    private static CommandLine Line(string text) => CommandLine.Parse(1, text, "mcp");

    private static (FakePluginHost Host, ChatVerbs Verbs) Build()
    {
        var host = new FakePluginHost();
        return (host, new ChatVerbs(host));
    }
}
