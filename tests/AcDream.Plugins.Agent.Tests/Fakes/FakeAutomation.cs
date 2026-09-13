using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakeAutomation : IAutomationSurface
{
    public bool IsAvailable { get; set; } = true;
    public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
    public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
    public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
    public IPluginChat Chat => FakeChat;

    internal FakeChat FakeChat { get; } = new();
}

internal sealed class FakeChat : IPluginChat
{
    private readonly List<PluginChatMessage> _messages = [];
    private ulong _sequence;

    internal List<string> SystemMessages { get; } = [];
    internal List<string> Submitted { get; } = [];
    internal bool AcceptsSubmit { get; set; } = true;

    public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
        _messages.Where(message => message.Sequence > afterSequence).ToArray();

    public void PostSystemMessage(string text) => SystemMessages.Add(text);

    public bool Submit(string text)
    {
        Submitted.Add(text);
        return AcceptsSubmit;
    }

    internal PluginChatMessage Receive(
        string text,
        string sender = "",
        int kind = 0,
        uint senderObjectId = 0u,
        string channelName = "")
    {
        var message = new PluginChatMessage(
            ++_sequence,
            senderObjectId,
            kind,
            sender,
            text,
            channelName);
        _messages.Add(message);
        return message;
    }
}
