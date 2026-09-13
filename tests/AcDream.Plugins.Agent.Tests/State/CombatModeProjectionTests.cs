using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class CombatModeProjectionTests
{
    [Fact]
    public void AnUnreportedModeIsUnknown()
    {
        var host = new FakePluginHost();

        JsonObject combat = new CombatModeProjection(host).Capture();

        Assert.Equal("unknown", combat["mode"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", combat["attacking"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void MagicModeIsObservedAndIdle()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCombat.Snapshot = Snapshot(PluginCombatMode.Magic, attacking: false);

        JsonObject combat = new CombatModeProjection(host).Capture();

        Assert.Equal("magic", combat["mode"]!["value"]!.GetValue<string>());
        Assert.False(combat["attacking"]!["value"]!.GetValue<bool>());
    }

    [Fact]
    public void AnAttackUnderWayIsAttacking()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeCombat.Snapshot = Snapshot(PluginCombatMode.Melee, attacking: true);

        JsonObject combat = new CombatModeProjection(host).Capture();

        Assert.Equal("melee", combat["mode"]!["value"]!.GetValue<string>());
        Assert.True(combat["attacking"]!["value"]!.GetValue<bool>());
    }

    private static PluginCombatSnapshot Snapshot(PluginCombatMode mode, bool attacking) =>
        new(
            SelectedObjectId: 0u,
            Mode: mode,
            AttackHeight: PluginAttackHeight.Medium,
            DesiredPower: 0.5f,
            PowerBarLevel: 0f,
            BuildInProgress: false,
            RequestInProgress: attacking,
            ServerResponsePending: false,
            RepeatAttackInProgress: false);
}
