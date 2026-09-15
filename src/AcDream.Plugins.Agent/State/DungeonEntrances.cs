using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// Where the character first stood each time it arrived in a dungeon from another
/// landblock, such as through a portal, kept as that dungeon's entrance. A dungeon the
/// character was already in when the plugin started has no entrance.
/// </summary>
internal sealed class DungeonEntrances
{
    private readonly Dictionary<uint, PluginNavigationPosition> _entrances = [];
    private uint _landblock;

    internal void Note(in PluginNavigationPosition position)
    {
        if (position.CellId == 0u)
            return;
        uint landblock = position.CellId >> 16;
        if (_landblock != 0u && landblock != _landblock && !position.IsOutdoor)
            _entrances[landblock] = position;
        _landblock = landblock;
    }

    /// <summary>The entrance of the dungeon a position stands in, when the character arrived there while the plugin ran.</summary>
    internal bool TryGet(in PluginNavigationPosition position, out PluginNavigationPosition entrance) =>
        _entrances.TryGetValue(position.CellId >> 16, out entrance);
}
