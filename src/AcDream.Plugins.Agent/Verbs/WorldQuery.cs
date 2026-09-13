using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>An object in the world around the character.</summary>
internal readonly record struct PlacedObject(PluginWorldObject Value, double Distance, string Word);

internal static class WorldQuery
{
    /// <summary>
    /// Every object with a position that is not carried or held and is not the
    /// character itself, nearest first, optionally within a range in meters.
    /// </summary>
    internal static List<PlacedObject> Around(
        IAutomationSurface automation,
        PluginNavigationSnapshot self,
        double? range)
    {
        ArgumentNullException.ThrowIfNull(automation);
        var placed = new List<PlacedObject>();
        foreach (PluginWorldObject candidate in automation.Objects.CaptureObjects())
        {
            if (!candidate.HasPosition
                || !candidate.IsLandscape
                || candidate.ObjectId == self.LocalObjectId)
            {
                continue;
            }
            double distance = Geometry.DistanceMeters(self.Position, candidate.Position);
            if (range is { } limit && distance > limit)
                continue;
            placed.Add(new PlacedObject(
                candidate,
                distance,
                EntityKinds.WordFor(candidate.ObjectClass)));
        }
        placed.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        return placed;
    }
}
