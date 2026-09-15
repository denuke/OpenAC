using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// The order to walk a dungeon's places in, one way through, from the place nearest the
/// character to an end. The places are joined into the shortest ways out from the start,
/// and the tour walks each branch to its end before turning back: the branches that lead
/// nowhere further first, shallowest first, and the branch that leads on to the end last.
/// So it clears the side rooms it passes and never goes back and forth between ways that
/// meet again.
/// </summary>
internal static class PlaceTour
{
    /// <summary>
    /// The indices of the rooms, passages and open ground in <paramref name="places"/> in
    /// tour order, leaving out those <paramref name="leaveOut"/> names, with the place the
    /// tour ends at in <paramref name="end"/>: <paramref name="endAt"/> when a way from the
    /// start reaches it, and otherwise the place farthest from the start. Places no way from
    /// the start reaches come after the tour, nearest walk first.
    /// </summary>
    internal static List<int> Order(
        IReadOnlyList<PluginNavigationPlace> places,
        int? endAt,
        Func<int, bool> leaveOut,
        out int end)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(leaveOut);
        end = -1;
        var order = new List<int>();
        int start = -1;
        for (int index = 0; index < places.Count; index++)
        {
            if (Walkable(places[index]) && (start < 0 || places[index].WalkMeters < places[start].WalkMeters))
                start = index;
        }
        if (start < 0)
            return order;

        var distance = new double[places.Count];
        var parent = new int[places.Count];
        Array.Fill(distance, double.PositiveInfinity);
        Array.Fill(parent, -1);
        distance[start] = 0d;
        var frontier = new PriorityQueue<int, double>();
        frontier.Enqueue(start, 0d);
        while (frontier.TryDequeue(out int place, out double reached))
        {
            if (reached > distance[place])
                continue;
            foreach (int neighbour in places[place].Neighbours)
            {
                if ((uint)neighbour >= (uint)places.Count || !Walkable(places[neighbour]))
                    continue;
                double through = reached + Meters(places[place].Position, places[neighbour].Position);
                if (through < distance[neighbour])
                {
                    distance[neighbour] = through;
                    parent[neighbour] = place;
                    frontier.Enqueue(neighbour, through);
                }
            }
        }

        int finish = endAt is { } asked && (uint)asked < (uint)places.Count && double.IsFinite(distance[asked])
            ? asked
            : Farthest(distance);
        var branches = new List<int>?[places.Count];
        for (int index = 0; index < places.Count; index++)
        {
            if (parent[index] >= 0)
                (branches[parent[index]] ??= []).Add(index);
        }
        var reach = new double[places.Count];
        var holdsEnd = new bool[places.Count];
        Measure(start);
        Visit(start);
        end = finish;

        foreach (int index in Enumerable.Range(0, places.Count)
            .Where(index => Walkable(places[index]) && !double.IsFinite(distance[index]) && !leaveOut(index))
            .OrderBy(index => places[index].WalkMeters))
        {
            order.Add(index);
        }
        return order;

        void Measure(int place)
        {
            reach[place] = distance[place];
            holdsEnd[place] = place == finish;
            foreach (int branch in branches[place] ?? [])
            {
                Measure(branch);
                reach[place] = Math.Max(reach[place], reach[branch]);
                holdsEnd[place] |= holdsEnd[branch];
            }
        }

        void Visit(int place)
        {
            if (place != finish && !leaveOut(place))
                order.Add(place);
            List<int> ways = branches[place] ?? [];
            foreach (int branch in ways.Where(branch => !holdsEnd[branch]).OrderBy(branch => reach[branch]).ThenBy(branch => branch))
                Visit(branch);
            foreach (int branch in ways.Where(branch => holdsEnd[branch]))
                Visit(branch);
            if (place == finish && !leaveOut(place))
                order.Add(place);
        }
    }

    /// <summary>The straight distance between two places in meters, their elevations being in map units like the rest of their coordinates.</summary>
    private static double Meters(in PluginNavigationPosition from, in PluginNavigationPosition to)
    {
        double horizontal = from.HorizontalDistanceMeters(to);
        double vertical = (to.Elevation - from.Elevation) * 240d;
        return Math.Sqrt((horizontal * horizontal) + (vertical * vertical));
    }

    private static bool Walkable(in PluginNavigationPlace place) =>
        place.Kind is PluginPlaceKind.Room or PluginPlaceKind.Passage or PluginPlaceKind.Open
        && float.IsFinite(place.WalkMeters);

    private static int Farthest(double[] distance)
    {
        int farthest = -1;
        for (int index = 0; index < distance.Length; index++)
        {
            if (double.IsFinite(distance[index]) && (farthest < 0 || distance[index] > distance[farthest]))
                farthest = index;
        }
        return farthest;
    }
}
