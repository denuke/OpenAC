using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakeNavigation : INavigationAutomation
{
    public PluginNavigationSnapshot Snapshot { get; set; }

    internal List<PluginMovementIntent> Intents { get; } = [];
    internal List<float> Headings { get; } = [];
    internal int Clears { get; private set; }

    public bool TryGetObject(uint objectId, out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    public PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent)
    {
        Intents.Add(intent);
        return PluginNavigationCommandStatus.Accepted;
    }

    public PluginNavigationCommandStatus ClearMovementIntent()
    {
        Clears++;
        return PluginNavigationCommandStatus.Accepted;
    }

    public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
    {
        Headings.Add(headingDegrees);
        return PluginNavigationCommandStatus.Accepted;
    }
}

internal sealed class FakeCombat : ICombatAutomation
{
    public PluginCombatSnapshot Snapshot { get; set; }

    internal List<PluginCombatTarget> Hostiles { get; } = [];
    internal List<string> Calls { get; } = [];
    internal PluginCombatCommandStatus NextStatus { get; set; } =
        PluginCombatCommandStatus.Started;

    public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
        float maximumDistance) =>
        Hostiles.Where(target => target.Distance <= maximumDistance).ToArray();

    public PluginCombatCommandResult EnterDefaultMode()
    {
        Calls.Add("default");
        return new(NextStatus);
    }

    public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
    {
        Calls.Add($"mode:{mode}");
        return new(NextStatus);
    }

    public PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId,
        PluginAttackHeight height,
        float power)
    {
        Calls.Add($"attack:{targetObjectId:X8}");
        return new(NextStatus);
    }

    public PluginCombatCommandResult ReleasePhysicalAttack()
    {
        Calls.Add("release");
        return new(NextStatus);
    }

    public PluginCombatCommandResult AbortPhysicalAttack()
    {
        Calls.Add("abort");
        return new(NextStatus);
    }
}

internal sealed class FakeObjects : IWorldObjectAutomation
{
    internal Dictionary<uint, PluginWorldObject> Known { get; } = [];
    internal Dictionary<uint, PluginItemProperties> Properties { get; } = [];

    public bool IsAvailable => true;

    public IReadOnlyList<PluginWorldObject> CaptureObjects() =>
        Known.Values.ToArray();

    public bool TryGet(uint objectId, out PluginWorldObject value) =>
        Known.TryGetValue(objectId, out value);

    public bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties) =>
        Properties.TryGetValue(objectId, out properties);

    internal void Add(PluginWorldObject value) => Known[value.ObjectId] = value;

    internal List<uint> Uses { get; } = [];
    internal PluginItemCommandStatus UseStatus { get; set; } = PluginItemCommandStatus.Started;

    public PluginItemCommandResult Use(uint objectId)
    {
        Uses.Add(objectId);
        return new PluginItemCommandResult(UseStatus);
    }
}

internal sealed class FakeItems : IItemAutomation
{
    public bool IsAvailable => true;
    public bool IsBusy { get; set; }
    public uint ActiveVendorObjectId { get; set; }
    public PluginItemUseCompletion LastCompletion { get; set; }
    public PluginInventoryCompletion LastInventoryCompletion { get; set; }

    internal List<PluginInventoryItem> Owned { get; } = [];
    internal List<PluginVendorItem> Stock { get; } = [];
    internal List<string> Calls { get; } = [];
    internal PluginItemCommandStatus NextStatus { get; set; } = PluginItemCommandStatus.Started;

    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => Owned.ToArray();

    public IReadOnlyList<PluginVendorItem> CaptureVendorStock() => Stock.ToArray();

    public PluginItemCommandResult Use(uint objectId) => Record($"use:{objectId:X8}");

    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId) =>
        Record($"apply:{objectId:X8}:{targetObjectId:X8}");

    public PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount = 0u,
        int placement = 0) =>
        Record($"move:{objectId:X8}:{containerObjectId:X8}:{amount}");

    public PluginItemCommandResult Drop(uint objectId, uint amount = 0u) =>
        Record($"drop:{objectId:X8}:{amount}");

    public PluginItemCommandResult Give(uint objectId, uint targetObjectId, uint amount = 0u) =>
        Record($"give:{objectId:X8}:{targetObjectId:X8}:{amount}");

    public PluginItemCommandResult Sell(uint objectId, uint amount = 0u) =>
        Record($"sell:{objectId:X8}:{amount}");

    public PluginItemCommandResult Buy(uint objectId, uint amount = 1u) =>
        Record($"buy:{objectId:X8}:{amount}");

    private PluginItemCommandResult Record(string call)
    {
        Calls.Add(call);
        return new PluginItemCommandResult(NextStatus);
    }
}

internal sealed class FakeLoot : ILootAutomation
{
    public bool IsAvailable => true;
    public uint CurrentContainerId { get; set; }

    internal List<PluginLootContainer> Corpses { get; } = [];
    internal List<PluginInventoryItem> Contents { get; } = [];
    internal List<string> Calls { get; } = [];
    internal PluginItemCommandStatus NextStatus { get; set; } = PluginItemCommandStatus.Started;

    public IReadOnlyList<PluginLootContainer> CaptureCorpses(float maximumDistance) =>
        Corpses.Where(corpse => corpse.Distance <= maximumDistance).ToArray();

    public IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() => Contents.ToArray();

    public PluginItemCommandResult Open(uint containerObjectId)
    {
        Calls.Add($"open:{containerObjectId:X8}");
        return new PluginItemCommandResult(NextStatus);
    }

    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false)
    {
        Calls.Add($"pickup:{objectId:X8}");
        return new PluginItemCommandResult(NextStatus);
    }
}

internal sealed class FakeEquipment : IEquipmentAutomation
{
    public bool IsAvailable => true;

    internal List<PluginEquipmentItem> Owned { get; } = [];
    internal List<string> Calls { get; } = [];
    internal PluginEquipmentCommandStatus NextStatus { get; set; } =
        PluginEquipmentCommandStatus.Started;

    public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() => Owned.ToArray();

    public PluginEquipmentCommandResult Equip(uint objectId, uint requestedLocation = 0u)
    {
        Calls.Add($"equip:{objectId:X8}");
        return new PluginEquipmentCommandResult(NextStatus);
    }
}
