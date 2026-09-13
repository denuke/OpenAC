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
    public IEquipmentAutomation Equipment => FakeEquipment;
    public IProjectileAutomation Projectiles => FakeProjectiles;
    public ILoginAutomation Login => FakeLogin;

    internal FakeChat FakeChat { get; } = new();
    internal FakeCharacter FakeCharacter { get; } = new();
    internal FakeNavigation FakeNavigation { get; } = new();
    internal FakeCombat FakeCombat { get; } = new();
    internal FakeObjects FakeObjects { get; } = new();
    internal FakeSpells FakeSpells { get; } = new();
    internal FakeMagic FakeMagic { get; } = new();
    internal FakeItems FakeItems { get; } = new();
    internal FakeLoot FakeLoot { get; } = new();
    internal FakeEquipment FakeEquipment { get; } = new();
    internal FakeProjectiles FakeProjectiles { get; } = new();
    internal FakeLogin FakeLogin { get; } = new();
}

/// <summary>A character list the test arranges. An accepted enter moves it on to entering the world.</summary>
internal sealed class FakeLogin : ILoginAutomation
{
    internal bool Available { get; set; } = true;
    internal PluginLoginSnapshot State { get; set; } = new(PluginLoginStage.InWorld, "account", "Testworld", 0u, null);
    internal List<PluginLoginCharacter> Roster { get; } = [];
    internal PluginLoginCommandStatus EnterStatus { get; set; } = PluginLoginCommandStatus.Accepted;
    internal PluginLoginCommandStatus LogOutStatus { get; set; } = PluginLoginCommandStatus.Accepted;
    internal List<uint> Entered { get; } = [];
    internal int LogOuts { get; private set; }

    public bool IsAvailable => Available;

    public PluginLoginSnapshot Snapshot => State;

    public IReadOnlyList<PluginLoginCharacter> CaptureRoster() => Roster.ToArray();

    public PluginLoginCommandStatus EnterWorld(uint characterObjectId)
    {
        Entered.Add(characterObjectId);
        if (EnterStatus == PluginLoginCommandStatus.Accepted)
            State = State with { Stage = PluginLoginStage.EnteringWorld, ChosenObjectId = characterObjectId, Error = null };
        return EnterStatus;
    }

    public PluginLoginCommandStatus LogOut()
    {
        LogOuts++;
        return LogOutStatus;
    }
}

/// <summary>Traces every projectile path clear unless a result is set for its object and kind.</summary>
internal sealed class FakeProjectiles : IProjectileAutomation
{
    internal Dictionary<(uint ObjectId, PluginProjectilePathKind Kind), PluginProjectilePathResult> Results { get; } = [];
    internal List<(uint ObjectId, PluginProjectilePathKind Kind, PluginAttackHeight Aim)> Traced { get; } = [];

    public bool IsAvailable => true;

    public PluginProjectilePathResult EvaluatePath(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks)
    {
        Traced.Add((targetObjectId, kind, targetHeight));
        return Results.TryGetValue((targetObjectId, kind), out PluginProjectilePathResult result)
            ? result
            : new PluginProjectilePathResult(PluginProjectilePathStatus.Clear);
    }
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
    public int MainPackFreeSlots { get; set; }
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
