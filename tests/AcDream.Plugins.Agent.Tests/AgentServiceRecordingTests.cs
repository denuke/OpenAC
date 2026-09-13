using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests;

public sealed class AgentServiceRecordingTests
{
    private static readonly string FullPath =
        Path.Combine(Path.GetTempPath(), "agent-records.jsonl");

    [Fact]
    public void NothingIsPublishedWhileNoConsumerIsAttached()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());

        service.OnTick(0.016);
        service.OnTick(0.016);

        Assert.Equal(0, service.Publisher.Published);
    }

    [Fact]
    public void RecordingStartsWithASnapshotOfCurrentState()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);

        Run(service, $"record {FullPath}");

        JsonElement session = Assert.Single(sink.OfKind("session"));
        Assert.Equal("in-world", session.GetProperty("state").GetString());
        Assert.Equal(
            RecordKinds.State.Count,
            session.GetProperty("batch").GetProperty("count").GetInt32());
        Assert.Equal(
            $"Agent: recording to {Path.GetFullPath(FullPath)}.",
            host.FakeAutomation.FakeChat.SystemMessages.Last());
    }

    [Fact]
    public void AStateChangeIsPublishedOnTheNextTick()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        Run(service, $"record {FullPath}");

        host.FakeAutomation.IsAvailable = false;
        service.OnTick(0.016);

        JsonElement[] sessions = sink.OfKind("session").ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal("out-of-world", sessions[1].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, sessions[1].GetProperty("batch").ValueKind);
    }

    [Fact]
    public void RecordOffDetachesAndClosesTheFile()
    {
        var host = new FakePluginHost();
        var sink = new RecordingSink();
        using var service = new AgentService(host, _ => sink);
        Run(service, $"record {FullPath}");

        Run(service, "record off");
        host.FakeAutomation.IsAvailable = false;
        service.OnTick(0.016);

        Assert.True(sink.Disposed);
        Assert.False(service.IsActive);
        Assert.Single(sink.OfKind("session"));
    }

    [Fact]
    public void ARelativePathIsRefusedAndNothingIsOpened()
    {
        var host = new FakePluginHost();
        int opened = 0;
        using var service = new AgentService(host, _ =>
        {
            opened++;
            return new RecordingSink();
        });

        Run(service, "record agent-records.jsonl");

        Assert.Equal(0, opened);
        Assert.False(service.IsActive);
        Assert.StartsWith(
            "Agent: give a full path",
            host.FakeAutomation.FakeChat.SystemMessages.Last(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsReportedAndNothingIsAttached()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(
            host,
            _ => throw new UnauthorizedAccessException("denied"));

        Run(service, $"record {FullPath}");

        Assert.False(service.IsActive);
        Assert.EndsWith(
            ": denied",
            host.FakeAutomation.FakeChat.SystemMessages.Last(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatusNamesTheRecordingFile()
    {
        var host = new FakePluginHost();
        using var service = new AgentService(host, _ => new RecordingSink());
        Run(service, $"record {FullPath}");

        Run(service, "status");

        Assert.Equal(
            $"Agent: not listening. Recording to {Path.GetFullPath(FullPath)}.",
            host.FakeAutomation.FakeChat.SystemMessages.Last());
    }

    private static void Run(AgentService service, string arguments) =>
        service.HandleCommand(new PluginCommand("agent", arguments, "/agent " + arguments));
}
