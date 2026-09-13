using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests;

public sealed class AgentServiceStateTests
{
    private static readonly string FullPath =
        Path.Combine(Path.GetTempPath(), "agent-records.jsonl");

    [Fact]
    public void TheSnapshotCoversEveryStateKindInOrder()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);

        Run(service, $"record {FullPath}");

        Assert.Equal(RecordKinds.State, sink.Records.Select(record => record.Kind));
    }

    [Fact]
    public void AVitalChangeAfterRecordingStartsIsPublished()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        Run(service, $"record {FullPath}");

        host.FakeAutomation.FakeCharacter.CurrentHealth = 200u;
        service.OnTick(0.016);

        JsonElement change = Assert.Single(sink.OfKind(RecordKinds.VitalChanged));
        Assert.Equal(200, change.GetProperty("value").GetInt32());
        Assert.Equal(300, change.GetProperty("previous").GetInt32());
        Assert.Equal(2, sink.OfKind(RecordKinds.Vitals).Count());
    }

    [Fact]
    public void AVitalChangeBeforeRecordingIsNotReplayed()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCharacter.MaxHealth = 300u;
        host.FakeAutomation.FakeCharacter.CurrentHealth = 300u;
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        service.OnTick(0.016);
        host.FakeAutomation.FakeCharacter.CurrentHealth = 100u;
        service.OnTick(0.016);

        Run(service, $"record {FullPath}");
        service.OnTick(0.016);

        Assert.Empty(sink.OfKind(RecordKinds.VitalChanged));
    }

    [Fact]
    public void AnEventSourceThatThrowsIsCountedAndStateStillFlows()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        service.Add(new ThrowingEvents());
        Run(service, $"record {FullPath}");

        host.FakeAutomation.IsAvailable = false;
        service.OnTick(0.016);

        Assert.Equal(1, service.EventFailures);
        Assert.Equal(2, sink.OfKind(RecordKinds.Session).Count());
    }

    [Fact]
    public void ATellAfterRecordingStartsIsPublished()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        Run(service, $"record {FullPath}");

        host.FakeAutomation.FakeChat.Receive(
            "hello",
            sender: "Friend",
            kind: (int)PluginChatKind.Tell);
        service.OnTick(0.016);

        JsonElement line = Assert.Single(sink.OfKind(RecordKinds.Chat));
        Assert.Equal("tell", line.GetProperty("chatKind").GetString());
        Assert.Equal("hello", line.GetProperty("text").GetString());
    }

    [Fact]
    public void ChatFromBeforeRecordingIsNotReplayed()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        host.FakeAutomation.FakeChat.Receive("old news");

        Run(service, $"record {FullPath}");
        service.OnTick(0.016);

        Assert.Empty(sink.OfKind(RecordKinds.Chat));
    }

    [Fact]
    public void AgentDoRunsOneLineAndEchoesItsOutcome()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        Run(service, "do say hello");

        Assert.Equal(["hello"], host.FakeAutomation.FakeChat.Submitted);
        Assert.Equal(
            "Agent [1]: handled.",
            host.FakeAutomation.FakeChat.SystemMessages.Last());
    }

    [Fact]
    public void AgentDoNeverSpeaksAReservedWord()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        Run(service, "do go to Mite Maze");

        Assert.Empty(host.FakeAutomation.FakeChat.Submitted);
        Assert.StartsWith(
            "Agent [1]: refused",
            host.FakeAutomation.FakeChat.SystemMessages.Last(),
            StringComparison.Ordinal);
    }

    private static void Run(AgentService service, string arguments) =>
        service.HandleCommand(new PluginCommand("agent", arguments, "/agent " + arguments));

    private sealed class ThrowingEvents : IEventProjection
    {
        public void Poll(Publisher publisher) =>
            throw new InvalidOperationException("poll failed");

        public void Rebase()
        {
        }
    }
}
