using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class SeenPortalsTests
{
    [Fact]
    public void PortalsShownIndoorsAreKeptByTheDungeonTheyStandIn()
    {
        var portals = new SeenPortals();
        PluginNavigationPosition exit = At(0x0156011Bu, 0.2d);
        PluginNavigationPosition mazeExit = At(0x01F80100u, 0.3d);
        portals.Note(
        [
            Object(1u, PluginObjectClass.Portal, exit),
            Object(2u, PluginObjectClass.Door, At(0x01560124u, 0.1d)),
            Object(3u, PluginObjectClass.Portal, mazeExit),
            Object(4u, PluginObjectClass.Portal, At(0xA9B40021u, 0.4d) with { IsOutdoor = true }),
            Object(5u, PluginObjectClass.Portal, At(0x01560124u, 0.5d)) with { HasPosition = false },
        ]);
        portals.Note([]);

        Assert.Equal([exit], portals.In(At(0x01560192u, 0d)));
        Assert.Equal([mazeExit], portals.In(At(0x01F80105u, 0d)));
        Assert.Empty(portals.In(At(0xA9B40021u, 0d) with { IsOutdoor = true }));
    }

    private static PluginNavigationPosition At(uint cellId, double eastWest) =>
        new(cellId, eastWest, 0d, 0d, 0f, false);

    private static PluginWorldObject Object(uint objectId, PluginObjectClass objectClass, PluginNavigationPosition position) =>
        new(objectId, 0u, objectClass.ToString(), objectClass, 0u, 0u, 0u)
        {
            HasPosition = true,
            Position = position,
        };
}
