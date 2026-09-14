using System.Collections.Concurrent;
using System.Numerics;

namespace AcDream.Core.Navigation;

/// <summary>
/// What a body can leap: how fast it walks and runs, how high a jump at full
/// power lifts it, and the deepest drop a route may take it down. A jump at a
/// share of full power lifts the body that share as high, but never less than
/// <see cref="LowestJump"/>.
/// </summary>
public readonly record struct NavLeapAbility(float WalkSpeed, float RunSpeed, float FullJumpHeight, float MaximumDrop)
{
    public const float LowestJump = 0.35f;

    /// <summary>How high a jump at <paramref name="power"/>, from 0 to 1, lifts the body.</summary>
    public float JumpHeight(float power) => MathF.Max(LowestJump, FullJumpHeight * Math.Clamp(power, 0f, 1f));
}

/// <summary>
/// A standing long jump from one node to another: the body stands on
/// <paramref name="From"/> facing <paramref name="To"/>, charges a jump to
/// <paramref name="Power"/>, and leaves the ground at running or walking pace.
/// </summary>
public readonly record struct NavLeap(int From, int To, float Power, bool Run);

/// <summary>
/// A leap a route takes: the leg that ends at <c>Legs[LegIndex]</c> is flown from
/// <c>Legs[LegIndex - 1]</c> as a standing long jump charged to
/// <paramref name="Power"/>, at running or walking pace.
/// </summary>
public readonly record struct NavRouteLeap(int LegIndex, float Power, bool Run);

/// <summary>
/// Finds the leaps a body can take from a node: standing long jumps over the
/// ledges, rises and gaps the grid cannot step across. Each is flown along its
/// arc through the grid's walls, floors and ceilings until the body's feet come
/// down on a clear node. The gentlest jump that lands is kept for each direction.
/// </summary>
internal sealed class NavLeapFinder
{
    private const float Gravity = 9.8f;

    /// <summary>How far the body flies between the points of its arc that are tested: a column's width.</summary>
    private const float SampleStep = 0.25f;

    /// <summary>How far an arc is followed before it counts as landing nowhere.</summary>
    private const float LongestFlight = 12f;

    /// <summary>
    /// A leap down or across starts within this many steps of the edge it leaves
    /// from; a leap up onto a rise may start this many steps back from its face,
    /// since the body must be high enough by the time it reaches the face.
    /// </summary>
    private const int EdgeSteps = 2;
    private const int RiseSteps = 8;

    /// <summary>The shortest leap kept, measured flat; anything shorter is a step.</summary>
    private const float ShortestLeap = 0.5f;

    /// <summary>The paces and powers tried, gentlest first.</summary>
    private static readonly (float Power, bool Run)[] Tries =
        [(0.1f, false), (0.1f, true), (0.3f, true), (0.6f, false), (0.6f, true), (1f, false), (1f, true)];

    private readonly NavGrid _grid;
    private readonly NavLeapAbility _ability;
    private readonly ConcurrentDictionary<int, NavLeap[]> _found = new();

    public NavLeapFinder(NavGrid grid, NavLeapAbility ability)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _ability = ability;
    }

    /// <summary>The leaps a body standing on <paramref name="node"/> can take, found once for each node.</summary>
    public NavLeap[] From(int node) => _found.GetOrAdd(node, Find);

    private NavLeap[] Find(int node)
    {
        if (!_grid.IsClear(node) || _grid.BorderDistance(node) > RiseSteps)
            return [];
        List<NavLeap>? leaps = null;
        for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
        {
            if (!EdgeAhead(node, direction))
                continue;
            (int stepX, int stepY) = NavGrid.StepOf(direction);
            Vector2 heading = Vector2.Normalize(new Vector2(stepX, stepY));
            foreach ((float power, bool run) in Tries)
            {
                int landing = Fly(node, heading, power, run);
                if (landing < 0)
                    continue;
                (leaps ??= []).Add(new NavLeap(node, landing, power, run));
                break;
            }
        }
        return leaps?.ToArray() ?? [];
    }

    /// <summary>
    /// Whether the grid stops linking steps along a direction near enough ahead to
    /// leap from here: a ledge or gap within <see cref="EdgeSteps"/>, or a rise
    /// within <see cref="RiseSteps"/>.
    /// </summary>
    private bool EdgeAhead(int node, int direction)
    {
        (int stepX, int stepY) = NavGrid.StepOf(direction);
        int at = node;
        for (int step = 0; step <= RiseSteps; step++)
        {
            int next = _grid.Link(at, direction);
            if (next >= 0)
            {
                at = next;
                continue;
            }
            if (step <= EdgeSteps)
                return true;
            (int x, int y) = _grid.ColumnOf(at);
            float here = _grid.Position(at).Z;
            (int first, int count) = _grid.NodesInColumn(x + stepX, y + stepY);
            for (int beyond = first; beyond < first + count; beyond++)
            {
                if (_grid.Position(beyond).Z > here)
                    return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// Flies a standing long jump from a node along a heading and returns the clear
    /// node it lands on, or -1 when the arc strikes something, lands on ground a
    /// body could walk to, falls deeper than a route may drop, or lands nowhere.
    /// </summary>
    private int Fly(int from, Vector2 heading, float power, bool run)
    {
        float speed = run ? _ability.RunSpeed : _ability.WalkSpeed;
        if (!(speed > 0f))
            return -1;
        float rise = MathF.Sqrt(2f * Gravity * _ability.JumpHeight(power));
        Vector3 start = _grid.Position(from);
        float previous = start.Z;
        for (float flown = SampleStep; flown <= LongestFlight; flown += SampleStep)
        {
            float time = flown / speed;
            float height = start.Z + (rise * time) - (0.5f * Gravity * time * time);
            var feet = new Vector3(start.X + (heading.X * flown), start.Y + (heading.Y * flown), height);
            if (!_grid.Contains(feet) || height < start.Z - _ability.MaximumDrop)
                return -1;
            if (height < previous)
            {
                int landing = _grid.LandingUnder(feet, previous);
                if (landing >= 0)
                    return Lands(from, landing, start) ? landing : -1;
            }
            if (!_grid.IsOpenAir(feet))
                return -1;
            previous = height;
        }
        return -1;
    }

    /// <summary>Whether a landing is one to keep: clear, beyond a step's reach, and not ground the body could walk to.</summary>
    private bool Lands(int from, int landing, Vector3 start)
    {
        Vector3 there = _grid.Position(landing);
        return _grid.IsClear(landing)
            && landing != from
            && Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(there.X, there.Y)) >= ShortestLeap
            && _grid.NodesAlong(from, landing).Count == 0;
    }
}
