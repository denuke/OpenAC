using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// The ground the character has stood on, kept as patches <see cref="PatchMeters"/> across
/// and <see cref="PatchHeight"/> high, so <c>explore</c> can leave out spots it has been near.
/// </summary>
internal sealed class VisitedGround
{
    internal const double PatchMeters = 5d;
    internal const double PatchHeight = 3d;

    /// <summary>How many patches are kept before the ground stood on is forgotten and noted afresh.</summary>
    internal const int MaximumPatches = 200_000;

    private readonly HashSet<(long X, long Y, long Z)> _patches = [];
    private readonly HashSet<uint> _landblocks = [];

    internal void Note(in PluginNavigationPosition position)
    {
        if (_patches.Count >= MaximumPatches)
            _patches.Clear();
        _patches.Add(PatchOf(position));
        if (position.CellId != 0u)
            _landblocks.Add(position.CellId >> 16);
    }

    /// <summary>Whether the character has stood in a landblock, named by an id such as 0xA9B5FFFF.</summary>
    internal bool HasBeenIn(uint landblockId) => _landblocks.Contains(landblockId >> 16);

    /// <summary>
    /// Whether the character has stood within about <paramref name="radiusMeters"/> of a
    /// position, and at least in its patch or one beside it, on the same level.
    /// </summary>
    internal bool IsNear(in PluginNavigationPosition position, double radiusMeters = 0d)
    {
        (long x, long y, long z) = PatchOf(position);
        long reach = Math.Max(1L, (long)Math.Ceiling(radiusMeters / PatchMeters));
        for (long east = -reach; east <= reach; east++)
        {
            for (long north = -reach; north <= reach; north++)
            {
                if (_patches.Contains((x + east, y + north, z)))
                    return true;
            }
        }
        return false;
    }

    private static (long X, long Y, long Z) PatchOf(in PluginNavigationPosition position) => (
        (long)Math.Floor(position.EastWest * 240d / PatchMeters),
        (long)Math.Floor(position.NorthSouth * 240d / PatchMeters),
        (long)Math.Floor(position.Elevation * 240d / PatchHeight));
}
