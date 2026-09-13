using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>The combat stance, and whether an attack is under way.</summary>
internal sealed class CombatModeProjection : IStateProjection
{
    private readonly IPluginHost _host;

    internal CombatModeProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.CombatMode;
    public double PollSeconds => 0d;
    public double MinimumIntervalSeconds => 0d;
    public double? HeartbeatSeconds => null;

    public JsonObject Capture()
    {
        IAutomationSurface automation = _host.Automation;
        PluginCombatSnapshot snapshot = automation.IsAvailable
            ? automation.Combat.Snapshot
            : default;
        bool known = automation.IsAvailable
            && snapshot.Mode != PluginCombatMode.Unknown;
        string because = automation.IsAvailable
            ? "the client has not been told the combat mode"
            : VitalsProjection.NotInWorld;
        bool attacking = snapshot.BuildInProgress
            || snapshot.RequestInProgress
            || snapshot.ServerResponsePending
            || snapshot.RepeatAttackInProgress;
        return new JsonObject
        {
            ["mode"] = known
                ? Facts.Observed(WireNames.Kebab(snapshot.Mode.ToString()))
                : Facts.Unknown(because),
            ["attacking"] = known
                ? Facts.Observed(attacking)
                : Facts.Unknown(because),
        };
    }
}
