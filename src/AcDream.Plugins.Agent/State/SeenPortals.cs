using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// The portals the client has shown indoors, kept by the dungeon they stand in, so a tour can
/// end at a way out the client no longer shows once the character has walked away from it.
/// </summary>
internal sealed class SeenPortals
{
    /// <summary>How many portals are kept before those seen are forgotten and noted afresh.</summary>
    internal const int MaximumPortals = 10_000;

    private readonly Dictionary<uint, Dictionary<uint, PluginNavigationPosition>> _byLandblock = [];
    private int _count;

    internal void Note(IReadOnlyList<PluginWorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        foreach (PluginWorldObject candidate in objects)
        {
            if (candidate.ObjectClass != PluginObjectClass.Portal
                || !candidate.HasPosition
                || candidate.Position.CellId == 0u
                || candidate.Position.IsOutdoor)
            {
                continue;
            }
            if (_count >= MaximumPortals)
            {
                _byLandblock.Clear();
                _count = 0;
            }
            uint landblock = candidate.Position.CellId >> 16;
            if (!_byLandblock.TryGetValue(landblock, out Dictionary<uint, PluginNavigationPosition>? portals))
                _byLandblock[landblock] = portals = [];
            if (portals.TryAdd(candidate.ObjectId, candidate.Position))
                _count++;
            else
                portals[candidate.ObjectId] = candidate.Position;
        }
    }

    /// <summary>The portals seen in the dungeon a position stands in.</summary>
    internal IReadOnlyCollection<PluginNavigationPosition> In(in PluginNavigationPosition position) =>
        position.CellId != 0u
        && _byLandblock.TryGetValue(position.CellId >> 16, out Dictionary<uint, PluginNavigationPosition>? portals)
            ? portals.Values
            : [];
}
