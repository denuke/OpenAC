using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class ChatEventsTests
{
    [Fact]
    public void EachNewLineIsPublishedOnceInOrder()
    {
        var (host, events, publisher, ring) = Build();
        events.Rebase();
        host.FakeAutomation.FakeChat.Receive("first");
        host.FakeAutomation.FakeChat.Receive("second");

        events.Poll(publisher);
        events.Poll(publisher);

        Assert.Equal(["first", "second"], Texts(ring));
    }

    [Fact]
    public void RebaseSkipsLinesThatArrivedBeforeIt()
    {
        var (host, events, publisher, ring) = Build();
        host.FakeAutomation.FakeChat.Receive("history");

        events.Rebase();
        host.FakeAutomation.FakeChat.Receive("news");
        events.Poll(publisher);

        Assert.Equal(["news"], Texts(ring));
    }

    [Fact]
    public void ATellCarriesItsKindAndSender()
    {
        var (host, events, publisher, ring) = Build();
        events.Rebase();
        host.FakeAutomation.FakeChat.Receive(
            "meet me at the lifestone",
            sender: "Friend",
            kind: (int)PluginChatKind.Tell,
            senderObjectId: 0x50000009u);

        events.Poll(publisher);

        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        JsonElement root = record.RootElement;
        Assert.Equal("chat", root.GetProperty("kind").GetString());
        Assert.Equal("tell", root.GetProperty("chatKind").GetString());
        Assert.Equal("Friend", root.GetProperty("sender").GetString());
        Assert.Equal("0x50000009", root.GetProperty("senderGuid").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("channel").ValueKind);
    }

    [Fact]
    public void AKindNumberWithNoNameIsCalledUnknown()
    {
        var (host, events, publisher, ring) = Build();
        events.Rebase();
        host.FakeAutomation.FakeChat.Receive("?", kind: 99);

        events.Poll(publisher);

        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        Assert.Equal("unknown", record.RootElement.GetProperty("chatKind").GetString());
    }

    private static string[] Texts(RecordRing ring) =>
        ring.Read(-1).Records
            .Select(record =>
            {
                using JsonDocument document = JsonDocument.Parse(record.Json);
                return document.RootElement.GetProperty("text").GetString()!;
            })
            .ToArray();

    private static (FakePluginHost Host, ChatEvents Events, Publisher Publisher, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var ring = new RecordRing(16);
        return (host, new ChatEvents(host), new Publisher(new AgentClock(), ring), ring);
    }
}
