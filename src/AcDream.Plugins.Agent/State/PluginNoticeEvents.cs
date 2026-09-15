using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>One record per notice a plugin posts, such as a combat macro finding its setup wrong, in the order posted.</summary>
internal sealed class PluginNoticeEvents : IEventProjection
{
    private readonly IPluginHost _host;
    private long _cursor;

    internal PluginNoticeEvents(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public void Rebase()
    {
        foreach (PluginNotice notice in _host.Notices.Capture(_cursor))
            _cursor = Math.Max(_cursor, notice.Sequence);
    }

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        foreach (PluginNotice notice in _host.Notices.Capture(_cursor))
        {
            if (notice.Sequence <= _cursor)
                continue;
            _cursor = notice.Sequence;
            publisher.Publish(RecordKinds.PluginNotice, new JsonObject
            {
                ["plugin"] = notice.PluginId,
                ["notice"] = notice.Kind,
                ["severity"] = WireNames.Kebab(notice.Severity.ToString()),
                ["message"] = notice.Message,
                ["details"] = Details(notice.DetailsJson),
            });
        }
    }

    /// <summary>A notice's details as an object, or as the text given when it is not a JSON object.</summary>
    private static JsonNode? Details(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) is JsonObject details ? details : JsonValue.Create(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}
