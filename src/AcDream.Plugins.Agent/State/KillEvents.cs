using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>One record per creature the character kills, as the client hears of it.</summary>
internal sealed class KillEvents : IEventProjection
{
    private readonly IPluginHost _host;
    private long _cursor;

    internal KillEvents(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public void Rebase()
    {
        foreach (PluginKill kill in _host.Automation.Combat.CaptureKills(_cursor))
            _cursor = Math.Max(_cursor, kill.Sequence);
    }

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        foreach (PluginKill kill in _host.Automation.Combat.CaptureKills(_cursor))
        {
            if (kill.Sequence <= _cursor)
                continue;
            _cursor = kill.Sequence;
            publisher.Publish(RecordKinds.Kill, new JsonObject
            {
                ["victim"] = kill.VictimName,
                ["victimGuid"] = Facts.Hex(kill.VictimObjectId),
            });
        }
    }
}
