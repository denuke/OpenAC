using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class DungeonEntrancesTests
{
    [Fact]
    public void ArrivingIndoorsFromAnotherLandblockIsThatDungeonsEntrance()
    {
        var entrances = new DungeonEntrances();
        entrances.Note(Outdoors(0xA9B40021u));
        PluginNavigationPosition arrival = Indoors(0x01560124u, 0.1d);
        entrances.Note(arrival);
        entrances.Note(Indoors(0x0156011Bu, 0.3d));

        Assert.True(entrances.TryGet(Indoors(0x01560192u, 0.5d), out PluginNavigationPosition entrance));
        Assert.Equal(arrival, entrance);
        Assert.False(entrances.TryGet(Outdoors(0xA9B40021u), out _));
    }

    [Fact]
    public void ADungeonTheCharacterWasAlreadyInOrGroundWalkedAcrossOutdoorsHasNoEntrance()
    {
        var entrances = new DungeonEntrances();
        entrances.Note(Indoors(0x01560124u, 0.1d));
        entrances.Note(Outdoors(0xA9B40021u));
        entrances.Note(Outdoors(0xA9B50021u));

        Assert.False(entrances.TryGet(Indoors(0x01560124u, 0.1d), out _));
        Assert.False(entrances.TryGet(Outdoors(0xA9B50021u), out _));
    }

    private static PluginNavigationPosition Indoors(uint cellId, double eastWest) =>
        new(cellId, eastWest, 0d, 0d, 0f, false);

    private static PluginNavigationPosition Outdoors(uint cellId) =>
        new(cellId, 0d, 0d, 0d, 0f, true);
}
