using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakeAutomation : IAutomationSurface
{
    public bool IsAvailable { get; set; } = true;
    public ICharacterInfo Character => FakeCharacter;
    public ISpellCatalog Spells => FakeSpells;
    public IMagicCommands Magic => FakeMagic;
    public IPluginChat Chat => FakeChat;
    public INavigationAutomation Navigation => FakeNavigation;
    public ICombatAutomation Combat => FakeCombat;
    public IWorldObjectAutomation Objects => FakeObjects;
    public IItemAutomation Items => FakeItems;
    public ILootAutomation Loot => FakeLoot;

    internal FakeChat FakeChat { get; } = new();
    internal FakeCharacter FakeCharacter { get; } = new();
    internal FakeNavigation FakeNavigation { get; } = new();
    internal FakeCombat FakeCombat { get; } = new();
    internal FakeObjects FakeObjects { get; } = new();
    internal FakeSpells FakeSpells { get; } = new();
    internal FakeMagic FakeMagic { get; } = new();
    internal FakeItems FakeItems { get; } = new();
    internal FakeLoot FakeLoot { get; } = new();
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

internal sealed class FakeCharacter : ICharacterInfo
{
    public bool IsInWorld { get; set; } = true;
    public string Name { get; set; } = "Tester";
    public string WorldName { get; set; } = "Testworld";
    public int Level { get; set; }
    public uint ObjectId { get; set; } = 0x50000001u;
    public uint CurrentHealth { get; set; }
    public uint MaxHealth { get; set; }
    public uint CurrentStamina { get; set; }
    public uint MaxStamina { get; set; }
    public uint CurrentMana { get; set; }
    public uint MaxMana { get; set; }
    public IReadOnlyList<PluginSkillInfo> Skills { get; set; } = [];
    public IReadOnlyList<PluginAttributeInfo> Attributes { get; set; } = [];
    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; set; } = [];

    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
    {
        foreach (PluginSkillInfo candidate in Skills)
        {
            if (candidate.SkillId == skillId)
            {
                skill = candidate;
                return true;
            }
        }
        skill = default;
        return false;
    }
}
