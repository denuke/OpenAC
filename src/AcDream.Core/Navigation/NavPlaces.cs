using System.Numerics;
using System.Runtime.InteropServices;

namespace AcDream.Core.Navigation;

/// <summary>What kind of place a stretch of walkable floor is.</summary>
public enum NavPlaceKind
{
    /// <summary>Floor wide enough to be a room, set apart from the places beside it by doorways or other narrowings.</summary>
    Room = 0,

    /// <summary>A narrow way, such as a corridor, a ramp or a stair, given in stretches along its length.</summary>
    Passage,

    /// <summary>Open floor too large to call a room, such as land outdoors or a great cavern.</summary>
    Open,
}

/// <summary>
/// A place a body can walk to: the node at its most open point and where that stands, about
/// how far a walk there goes, what kind of place it is, how much floor it holds, about how
/// wide it is at its widest, how far its floor rises from lowest to highest, and how many
/// other places it opens onto.
/// </summary>
public readonly record struct NavPlace(
    int Node,
    Vector3 Position,
    float WalkMeters,
    NavPlaceKind Kind,
    float AreaSquareMeters,
    float WidthMeters,
    float RiseMeters,
    int Exits)
{
    private readonly IReadOnlyList<int>? _neighbours;

    /// <summary>The places this one opens onto, as indices into the same list of places, lowest first.</summary>
    public IReadOnlyList<int> Neighbours
    {
        get => _neighbours ?? [];
        init => _neighbours = value;
    }
}

/// <summary>
/// The places a body can walk to from where it stands, told apart by how wide the floor is.
/// A room is floor at least <see cref="RoomContrast"/> times as wide as the widest way from it
/// to wider floor, so a doorway, a corridor or any other narrowing sets it apart, and the
/// widest floor of all is a room too. A room takes in the floor around it at least its own
/// width over <see cref="RoomContrast"/>, then the floor nearest that. Floor no room takes is
/// a passage, as is a room long and thin for its width. Floor is measured as wide past a
/// small obstacle, such as a pillar, as around it.
/// </summary>
public static class NavPlaces
{
    /// <summary>Floor at least this far from the edge of walkable floor can be a room.</summary>
    public const float RoomClearanceMeters = 1.25f;

    /// <summary>
    /// A room is at least this many times as wide as the widest way from it to wider floor, and
    /// takes in the floor around it at least its own width over this.
    /// </summary>
    public const float RoomContrast = 2f;

    /// <summary>
    /// An obstacle floor runs all the way around whose edge is at most this long, such as a
    /// pillar, a statue or a table, narrows no place: floor is measured as wide past it as around it.
    /// </summary>
    public const float SmallObstacleEdgeMeters = 24f;

    /// <summary>A room whose floor area is at least this many times the square of its clear width is long and thin enough to be a passage.</summary>
    public const float ElongatedShare = 5f;

    /// <summary>A room with at least this much floor is open ground.</summary>
    public const float OpenAreaSquareMeters = 2500f;

    /// <summary>A passage is given as one place for each stretch of about this length along a walk.</summary>
    public const float PassageStretchMeters = 20f;

    /// <summary>
    /// A place with less floor than this, such as a doorway, an alcove or the corner of a room
    /// beyond its core's reach, is taken into the place beside it that it shares the most edge with.
    /// </summary>
    public const float SmallestAreaSquareMeters = 16f;

    /// <summary>How much farther than its core's own clearance a room reaches to take in the floor nearest it, in half steps.</summary>
    private const int CoreReachSlack = 4;

    /// <summary>
    /// The places a body can walk to from <paramref name="start"/>, nearest walk first. A walk
    /// goes between neighbouring clear nodes, as a route does, and its length counts the steps
    /// by which the search first reached a node, so it can run a little longer than a route's.
    /// </summary>
    public static IReadOnlyList<NavPlace> Find(NavGrid grid, int start)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (start < 0 || start >= grid.NodeCount)
            return [];

        float halfStep = grid.CellSize * 0.5f;
        float[] walked = Walk(grid, start, out int[] reached, out int reachedCount);
        int[] clearance = Clearance(grid, walked, reached, reachedCount, pastSmallObstacles: false);
        int[] width = Clearance(grid, walked, reached, reachedCount, pastSmallObstacles: true);
        var label = new int[grid.NodeCount];
        var isRoom = new List<bool>();
        Label(grid, width, reached, reachedCount, (int)MathF.Ceiling(RoomClearanceMeters / halfStep), label, isRoom);

        // A room long and thin for its width is a passage.
        var nodes = new int[isRoom.Count];
        var widest = new int[isRoom.Count];
        var firstWalk = new float[isRoom.Count];
        Array.Fill(firstWalk, float.MaxValue);
        for (int index = 0; index < reachedCount; index++)
        {
            int node = reached[index];
            int place = label[node] - 1;
            nodes[place]++;
            widest[place] = Math.Max(widest[place], width[node]);
            firstWalk[place] = MathF.Min(firstWalk[place], walked[node]);
        }
        float stepArea = grid.CellSize * grid.CellSize;
        for (int place = 0; place < isRoom.Count; place++)
        {
            float clearWidth = 2f * widest[place] * halfStep;
            if (isRoom[place] && nodes[place] * stepArea >= ElongatedShare * clearWidth * clearWidth)
                isRoom[place] = false;
        }

        long KeyOf(int node)
        {
            int place = label[node] - 1;
            long stretch = isRoom[place] ? 0L : (long)((walked[node] - firstWalk[place]) / PassageStretchMeters);
            return ((long)place << 20) | stretch;
        }

        // How much floor each stretch holds and how much edge it shares with each beside it.
        var floor = new Dictionary<long, int>();
        var beside = new Dictionary<long, Dictionary<long, int>>();
        for (int index = 0; index < reachedCount; index++)
        {
            int node = reached[index];
            long key = KeyOf(node);
            floor[key] = floor.GetValueOrDefault(key) + 1;
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || float.IsNaN(walked[next]))
                    continue;
                long other = KeyOf(next);
                if (other == key)
                    continue;
                if (!beside.TryGetValue(key, out Dictionary<long, int>? edges))
                    beside[key] = edges = [];
                edges[other] = edges.GetValueOrDefault(other) + 1;
            }
        }

        // Small stretches join the stretch beside them they share the most edge with, smallest first.
        var into = new Dictionary<long, long>();
        long Root(long key)
        {
            while (into.TryGetValue(key, out long parent))
                key = parent;
            return key;
        }
        foreach (long key in floor.Keys.OrderBy(key => floor[key]).ToList())
        {
            if (into.ContainsKey(key) || floor[key] * stepArea >= SmallestAreaSquareMeters)
                continue;
            if (!beside.TryGetValue(key, out Dictionary<long, int>? edges))
                continue;
            long best = key;
            int most = 0;
            foreach ((long other, int shared) in edges)
            {
                long target = Root(other);
                if (target != key && shared > most)
                {
                    best = target;
                    most = shared;
                }
            }
            if (best == key)
                continue;
            into[key] = best;
            floor[best] += floor[key];
            if (!beside.TryGetValue(best, out Dictionary<long, int>? bestEdges))
                beside[best] = bestEdges = [];
            foreach ((long other, int shared) in edges)
            {
                if (Root(other) != best)
                    bestEdges[other] = bestEdges.GetValueOrDefault(other) + shared;
            }
        }

        var tallies = new Dictionary<long, Tally>();
        var joins = new HashSet<(long, long)>();
        for (int index = 0; index < reachedCount; index++)
        {
            int node = reached[index];
            long key = Root(KeyOf(node));
            ref Tally tally = ref CollectionsMarshal.GetValueRefOrAddDefault(tallies, key, out bool exists);
            if (!exists)
                tally = new Tally { Best = node, Low = float.MaxValue, High = float.MinValue };
            tally.Nodes++;
            tally.Widest = Math.Max(tally.Widest, width[node]);
            float z = grid.Position(node).Z;
            tally.Low = MathF.Min(tally.Low, z);
            tally.High = MathF.Max(tally.High, z);
            if (clearance[node] > clearance[tally.Best]
                || (clearance[node] == clearance[tally.Best] && walked[node] < walked[tally.Best]))
            {
                tally.Best = node;
            }
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || float.IsNaN(walked[next]))
                    continue;
                long other = Root(KeyOf(next));
                if (other != key)
                    joins.Add(key < other ? (key, other) : (other, key));
            }
        }

        var kept = new HashSet<long>();
        foreach ((long key, Tally tally) in tallies)
        {
            if (tally.Nodes * stepArea >= SmallestAreaSquareMeters)
                kept.Add(key);
        }
        var exits = new Dictionary<long, int>();
        foreach ((long one, long other) in joins)
        {
            if (!kept.Contains(one) || !kept.Contains(other))
                continue;
            exits[one] = exits.GetValueOrDefault(one) + 1;
            exits[other] = exits.GetValueOrDefault(other) + 1;
        }

        var found = new List<(long Key, NavPlace Place)>(kept.Count);
        foreach (long key in kept)
        {
            Tally tally = tallies[key];
            float area = tally.Nodes * stepArea;
            NavPlaceKind kind = !isRoom[(int)(key >> 20)]
                ? NavPlaceKind.Passage
                : area >= OpenAreaSquareMeters ? NavPlaceKind.Open : NavPlaceKind.Room;
            found.Add((key, new NavPlace(
                tally.Best,
                grid.Position(tally.Best),
                walked[tally.Best],
                kind,
                area,
                2f * ((tally.Widest * halfStep) + grid.NearestWall),
                tally.High - tally.Low,
                exits.GetValueOrDefault(key))));
        }
        found.Sort(static (left, right) => left.Place.WalkMeters.CompareTo(right.Place.WalkMeters));

        var indexOf = new Dictionary<long, int>(found.Count);
        for (int index = 0; index < found.Count; index++)
            indexOf[found[index].Key] = index;
        var neighbours = new List<int>[found.Count];
        for (int index = 0; index < found.Count; index++)
            neighbours[index] = [];
        foreach ((long one, long other) in joins)
        {
            if (indexOf.TryGetValue(one, out int first) && indexOf.TryGetValue(other, out int second))
            {
                neighbours[first].Add(second);
                neighbours[second].Add(first);
            }
        }
        var places = new NavPlace[found.Count];
        for (int index = 0; index < found.Count; index++)
            places[index] = found[index].Place with { Neighbours = [.. neighbours[index].Order()] };
        return places;
    }

    /// <summary>
    /// Gives every reached node a place, numbered from one. Floor is joined up from the widest
    /// down; where the floor around one widest spot first meets floor around a wider one, that
    /// spot's widest way to wider floor is as wide as the floor where they meet. A spot at least
    /// <see cref="RoomContrast"/> times as wide as that way, and the widest spot of all, is a
    /// room, widest first, taking in the floor around it at least its width over
    /// <see cref="RoomContrast"/> and then the floor nearest that, within reach. Each connected
    /// part of the floor no room takes is a passage.
    /// </summary>
    private static void Label(NavGrid grid, int[] clearance, int[] reached, int reachedCount, int roomCore, int[] label, List<bool> isRoom)
    {
        float halfStep = grid.CellSize * 0.5f;
        float WidthOf(int clear) => 2f * ((clear * halfStep) + grid.NearestWall);

        // The reached nodes, widest floor first and in the order reached among equals.
        int widest = 0;
        for (int index = 0; index < reachedCount; index++)
            widest = Math.Max(widest, clearance[reached[index]]);
        var first = new int[widest + 2];
        for (int index = 0; index < reachedCount; index++)
            first[widest - clearance[reached[index]] + 1]++;
        for (int at = 1; at < first.Length; at++)
            first[at] += first[at - 1];
        var order = new int[reachedCount];
        for (int index = 0; index < reachedCount; index++)
            order[first[widest - clearance[reached[index]]]++] = reached[index];

        // Stretches of floor joined up widest first, each known by its widest spot. Where two
        // meet, the one with the narrower widest spot ends, and the floor there is that spot's way.
        var joined = new int[grid.NodeCount];
        Array.Fill(joined, -1);
        var spotOf = new int[grid.NodeCount];
        var way = new int[grid.NodeCount];
        Array.Fill(way, -1);
        int Joined(int node)
        {
            while (joined[node] != node)
                node = joined[node] = joined[joined[node]];
            return node;
        }
        foreach (int node in order)
        {
            joined[node] = node;
            spotOf[node] = node;
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || joined[next] < 0)
                    continue;
                int mine = Joined(node);
                int theirs = Joined(next);
                if (mine == theirs)
                    continue;
                int mySpot = spotOf[mine];
                int theirSpot = spotOf[theirs];
                bool mineIsWider = clearance[mySpot] > clearance[theirSpot]
                    || (clearance[mySpot] == clearance[theirSpot] && mySpot < theirSpot);
                int ending = mineIsWider ? theirs : mine;
                if (spotOf[ending] != node)
                    way[spotOf[ending]] = clearance[node];
                joined[ending] = mineIsWider ? mine : theirs;
            }
        }

        var reachOf = new List<int>();
        var near = new int[grid.NodeCount];
        var buckets = new List<List<int>> { new() };
        foreach (int spot in order)
        {
            if (clearance[spot] < roomCore)
                break;
            if (label[spot] != 0)
                continue;
            float width = WidthOf(clearance[spot]);
            if (spot != order[0] && (way[spot] < 0 || width < RoomContrast * WidthOf(way[spot])))
                continue;
            int core = Math.Max(0, (int)MathF.Ceiling(((width / RoomContrast * 0.5f) - grid.NearestWall) / halfStep));
            isRoom.Add(true);
            reachOf.Add(core + CoreReachSlack);
            label[spot] = isRoom.Count;
            var part = new List<int> { spot };
            for (int head = 0; head < part.Count; head++)
            {
                int node = part[head];
                buckets[0].Add(node);
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next < 0 || joined[next] < 0 || label[next] != 0 || clearance[next] < core)
                        continue;
                    label[next] = isRoom.Count;
                    part.Add(next);
                }
            }
        }
        for (int distance = 0; distance < buckets.Count; distance++)
        {
            foreach (int node in buckets[distance])
            {
                if (near[node] != distance)
                    continue;
                int reach = reachOf[label[node] - 1];
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next < 0 || joined[next] < 0)
                        continue;
                    int through = distance + (IsDiagonal(direction) ? 3 : 2);
                    if (through > reach || (label[next] != 0 && near[next] <= through))
                        continue;
                    near[next] = through;
                    label[next] = label[node];
                    while (buckets.Count <= through)
                        buckets.Add([]);
                    buckets[through].Add(next);
                }
            }
            buckets[distance] = [];
        }

        var left = new List<int>();
        var member = new int[grid.NodeCount];
        for (int index = 0; index < reachedCount; index++)
        {
            int node = reached[index];
            if (label[node] != 0)
                continue;
            left.Add(node);
            member[node] = 1;
        }
        foreach (List<int> part in Parts(grid, left, member, 1, static _ => true))
        {
            isRoom.Add(false);
            foreach (int node in part)
                label[node] = isRoom.Count;
        }
    }

    /// <summary>The connected parts of the nodes of one stretch that a test lets in.</summary>
    private static List<List<int>> Parts(NavGrid grid, List<int> stretch, int[] member, int stamp, Func<int, bool> lets)
    {
        var parts = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (int first in stretch)
        {
            if (!lets(first) || !seen.Add(first))
                continue;
            var part = new List<int> { first };
            for (int head = 0; head < part.Count; head++)
            {
                int node = part[head];
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next >= 0 && member[next] == stamp && lets(next) && seen.Add(next))
                        part.Add(next);
                }
            }
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>How far a walk goes to every clear node it reaches from the start, and those nodes in the order reached.</summary>
    private static float[] Walk(NavGrid grid, int start, out int[] reached, out int reachedCount)
    {
        var walked = new float[grid.NodeCount];
        Array.Fill(walked, float.NaN);
        reached = new int[grid.NodeCount];
        reachedCount = 0;
        walked[start] = 0f;
        reached[reachedCount++] = start;
        float diagonal = grid.CellSize * MathF.Sqrt(2f);
        for (int head = 0; head < reachedCount; head++)
        {
            int node = reached[head];
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || !float.IsNaN(walked[next]) || !grid.IsClear(next))
                    continue;
                walked[next] = walked[node] + (IsDiagonal(direction) ? diagonal : grid.CellSize);
                reached[reachedCount++] = next;
            }
        }
        return walked;
    }

    /// <summary>
    /// How far each reached node stands from the edge of the floor a walk reaches, in half
    /// steps: two for a step along a row of columns and three for a diagonal one. Measured
    /// <paramref name="pastSmallObstacles"/>, edge that only runs around a small obstacle, no
    /// longer than <see cref="SmallObstacleEdgeMeters"/>, is passed over, though never the
    /// longest run of edge, giving how wide the floor is rather than how much room a body has.
    /// </summary>
    private static int[] Clearance(NavGrid grid, float[] walked, int[] reached, int reachedCount, bool pastSmallObstacles)
    {
        var clearance = new int[grid.NodeCount];
        var edge = new List<int>();
        for (int index = 0; index < reachedCount; index++)
        {
            int node = reached[index];
            clearance[node] = int.MaxValue;
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || float.IsNaN(walked[next]))
                {
                    clearance[node] = 0;
                    edge.Add(node);
                    break;
                }
            }
        }

        var buckets = new List<List<int>> { new() };
        if (!pastSmallObstacles)
            buckets[0].AddRange(edge);
        else
            StartPastSmallObstacles(grid, walked, clearance, edge, buckets[0]);
        for (int distance = 0; distance < buckets.Count; distance++)
        {
            foreach (int node in buckets[distance])
            {
                if (clearance[node] != distance)
                    continue;
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next < 0 || float.IsNaN(walked[next]))
                        continue;
                    int through = distance + (IsDiagonal(direction) ? 3 : 2);
                    if (through >= clearance[next])
                        continue;
                    clearance[next] = through;
                    while (buckets.Count <= through)
                        buckets.Add([]);
                    buckets[through].Add(next);
                }
            }
            buckets[distance] = [];
        }
        return clearance;
    }

    /// <summary>
    /// The edge nodes clearance is measured from when small obstacles are passed over: edge in
    /// runs that touch, leaving out each run short enough to go around a small obstacle, unless
    /// it is the longest. The edge nodes left out are measured as floor.
    /// </summary>
    private static void StartPastSmallObstacles(NavGrid grid, float[] walked, int[] clearance, List<int> edge, List<int> starts)
    {
        var runOf = new int[grid.NodeCount];
        var runLengths = new List<int> { 0 };
        foreach (int from in edge)
        {
            if (runOf[from] != 0)
                continue;
            int run = runLengths.Count;
            runOf[from] = run;
            var runNodes = new List<int> { from };
            for (int head = 0; head < runNodes.Count; head++)
            {
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(runNodes[head], direction);
                    if (next < 0 || float.IsNaN(walked[next]) || clearance[next] != 0 || runOf[next] != 0)
                        continue;
                    runOf[next] = run;
                    runNodes.Add(next);
                }
            }
            runLengths.Add(runNodes.Count);
        }
        int longest = 0;
        for (int run = 1; run < runLengths.Count; run++)
        {
            if (runLengths[run] > runLengths[longest])
                longest = run;
        }
        int shortRun = (int)(SmallObstacleEdgeMeters / grid.CellSize);
        foreach (int node in edge)
        {
            if (runOf[node] == longest || runLengths[runOf[node]] > shortRun)
                starts.Add(node);
            else
                clearance[node] = int.MaxValue;
        }
    }

    private static bool IsDiagonal(int direction)
    {
        (int x, int y) = NavGrid.StepOf(direction);
        return x != 0 && y != 0;
    }

    private struct Tally
    {
        public int Nodes;
        public int Best;
        public int Widest;
        public float Low;
        public float High;
    }
}
