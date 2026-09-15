using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class PluginNoticeEventsTests
{
    [Fact]
    public void EachNoticeAfterTheRebaseIsPublishedOnceWithItsDetailsAsAnObject()
    {
        var (host, events, publisher, ring) = Build();
        host.FakeNotices.Post("macro-started", PluginNoticeSeverity.Info, "Macro started.");
        events.Rebase();
        host.FakeNotices.Post("route-waypoint", PluginNoticeSeverity.Info, "Waypoint 3 of 10 reached", """{"waypoint":2,"count":10}""");

        events.Poll(publisher);
        events.Poll(publisher);

        using JsonDocument record = JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json);
        JsonElement root = record.RootElement;
        Assert.Equal("plugin-notice", root.GetProperty("kind").GetString());
        Assert.Equal("test.plugin", root.GetProperty("plugin").GetString());
        Assert.Equal("route-waypoint", root.GetProperty("notice").GetString());
        Assert.Equal("info", root.GetProperty("severity").GetString());
        Assert.Equal("Waypoint 3 of 10 reached", root.GetProperty("message").GetString());
        Assert.Equal(2, root.GetProperty("details").GetProperty("waypoint").GetInt32());
    }

    [Fact]
    public void DetailsThatAreNotAJsonObjectAreKeptAsText()
    {
        var (host, events, publisher, ring) = Build();
        events.Rebase();
        host.FakeNotices.Post("odd", PluginNoticeSeverity.Warning, "odd details", "not json {");
        host.FakeNotices.Post("list", PluginNoticeSeverity.Error, "a list", "[1,2]");
        host.FakeNotices.Post("none", PluginNoticeSeverity.Info, "no details");

        events.Poll(publisher);

        JsonElement[] records = ring.Read(-1).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement.Clone())
            .ToArray();
        Assert.Equal("not json {", records[0].GetProperty("details").GetString());
        Assert.Equal("warning", records[0].GetProperty("severity").GetString());
        Assert.Equal("[1,2]", records[1].GetProperty("details").GetString());
        Assert.Equal("error", records[1].GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, records[2].GetProperty("details").ValueKind);
    }

    private static (FakePluginHost Host, PluginNoticeEvents Events, Publisher Publisher, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        var ring = new RecordRing(16);
        return (host, new PluginNoticeEvents(host), new Publisher(new AgentClock(), ring), ring);
    }
}
