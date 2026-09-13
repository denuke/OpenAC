using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// One record per chat line the client shows, in the order it arrived. Fields
/// that do not apply to a line, such as the channel of local speech, are null.
/// </summary>
internal sealed class ChatEvents : IEventProjection
{
    private readonly IPluginHost _host;
    private ulong _cursor;

    internal ChatEvents(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public void Rebase()
    {
        foreach (PluginChatMessage message in _host.Automation.Chat.CaptureMessages(_cursor))
            _cursor = Math.Max(_cursor, message.Sequence);
    }

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        foreach (PluginChatMessage message in _host.Automation.Chat.CaptureMessages(_cursor))
        {
            if (message.Sequence <= _cursor)
                continue;
            _cursor = message.Sequence;
            publisher.Publish(RecordKinds.Chat, new JsonObject
            {
                ["chatKind"] = Enum.IsDefined(message.ChatKind)
                    ? WireNames.Kebab(message.ChatKind.ToString())
                    : "unknown",
                ["channel"] = string.IsNullOrEmpty(message.ChannelName)
                    ? null
                    : message.ChannelName,
                ["sender"] = string.IsNullOrEmpty(message.Sender)
                    ? null
                    : message.Sender,
                ["senderGuid"] = message.SenderObjectId == 0u
                    ? null
                    : Facts.Hex(message.SenderObjectId),
                ["text"] = message.Text,
            });
        }
    }
}
