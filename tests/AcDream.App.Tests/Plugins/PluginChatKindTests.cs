using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

public sealed class PluginChatKindTests
{
    /// <summary>
    /// The graphical surface hands plugins the chat log's own kind number, so
    /// the plugin enum must name every number exactly as the chat log does.
    /// </summary>
    [Fact]
    public void PluginChatKindsNameTheChatLogsKindsNumberForNumber()
    {
        Assert.Equal(
            Enum.GetValues<ChatKind>().Select(kind => (kind.ToString(), (int)kind)),
            Enum.GetValues<PluginChatKind>().Select(kind => (kind.ToString(), (int)kind)));
    }
}
