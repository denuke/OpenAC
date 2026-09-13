using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class TargetProjectionTests
{
    [Fact]
    public void WithNothingSelectedTheTargetIsUnknown()
    {
        var host = new FakePluginHost();

        JsonObject target = new TargetProjection(host).Capture();

        Assert.False(target["selected"]!.GetValue<bool>());
        Assert.Equal("unknown", target["guid"]!["presence"]!.GetValue<string>());
    }

    [Fact]
    public void ASelectedMonsterIsNamedAndClassed()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge Skulker", PluginObjectClass.Monster, 16u, 0u, 0u));
        host.FakeSelection.Select(0x70000001u);

        JsonObject target = new TargetProjection(host).Capture();

        Assert.True(target["selected"]!.GetValue<bool>());
        Assert.Equal("0x70000001", target["guid"]!["value"]!.GetValue<string>());
        Assert.Equal("Drudge Skulker", target["name"]!["value"]!.GetValue<string>());
        Assert.Equal("monster", target["objectClass"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void ASelectionTheClientCannotDescribeIsUnknownNotBlank()
    {
        var host = new FakePluginHost();
        host.FakeSelection.Select(0x70000009u);

        JsonObject target = new TargetProjection(host).Capture();

        Assert.Equal("observed", target["guid"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", target["name"]!["presence"]!.GetValue<string>());
        Assert.Equal("unknown", target["objectClass"]!["presence"]!.GetValue<string>());
    }
}
