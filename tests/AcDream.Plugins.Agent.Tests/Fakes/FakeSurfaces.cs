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
}
