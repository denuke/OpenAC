namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginNavigationPosition(
    uint CellId,
    double EastWest,
    double NorthSouth,
    double Elevation,
    float HeadingDegrees,
    bool IsOutdoor)
{
    public double HorizontalDistanceMeters(in PluginNavigationPosition other)
    {
        double dx = EastWest - other.EastWest;
        double dy = NorthSouth - other.NorthSouth;
        return Math.Sqrt(dx * dx + dy * dy) * 240d;
    }
}

public readonly record struct PluginNavigationObject(
    uint ObjectId,
    string Name,
    PluginNavigationPosition Position)
{
    public bool IsDoor { get; init; }
    public bool IsOpen { get; init; }
    public bool IsLocked { get; init; }
    public bool HasLockState { get; init; }
    public int LockDifficulty { get; init; }
}

/// <summary>The local movement state sampled atomically by a plugin tick.</summary>
public readonly record struct PluginNavigationSnapshot(
    bool IsAvailable,
    bool IsPortalSpace,
    uint LocalObjectId,
    PluginNavigationPosition Position,
    bool IsMoving,
    bool IsAirborne)
{
    public PluginNavigationPosition ConfirmedPosition { get; init; }
    public ulong ConfirmedPositionRevision { get; init; }
}

public readonly record struct PluginMovementIntent(
    bool Forward = false,
    bool Backward = false,
    bool StrafeLeft = false,
    bool StrafeRight = false,
    bool TurnLeft = false,
    bool TurnRight = false,
    bool Run = true,
    bool Jump = false);

/// <summary>Which way a client-driven move goes.</summary>
public enum PluginMoveDirection
{
    Forward,
    Backward,
    StrafeLeft,
    StrafeRight,
    TurnLeft,
    TurnRight,
}

public enum PluginMovePace
{
    Walk,
    Run,
}

/// <summary>What a client-driven move's amount counts.</summary>
public enum PluginMoveUnit
{
    /// <summary>Meters going forward, backward or sideways, and degrees for a turn.</summary>
    MetersOrDegrees,

    Seconds,
}

/// <summary>
/// The parts of movement that combine the way held movement keys do: going
/// forward or backward, strafing, and turning. Each carries one client-driven
/// move at a time.
/// </summary>
public enum PluginMoveChannel
{
    Travel,
    Strafe,
    Turn,
}

/// <summary>Where a channel's most recent client-driven move stands.</summary>
public enum PluginMoveState
{
    None = 0,

    Moving,

    /// <summary>It covered its distance, angle or time.</summary>
    Completed,

    /// <summary>A stop ended it.</summary>
    Stopped,

    /// <summary>Time ran out: the limit for a move without an amount, or a move too slow to cover its distance or angle.</summary>
    TimeLimit,

    /// <summary>It stopped making progress.</summary>
    Blocked,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,
}

/// <summary>
/// One channel's most recent client-driven move. <paramref name="Sequence"/> grows
/// by one for every move begun on any channel, so a plugin can tell its own move
/// from a later one. <paramref name="Covered"/> is meters, or degrees for a turn,
/// whatever the move's unit.
/// </summary>
public readonly record struct PluginMoveProgress(
    long Sequence,
    PluginMoveState State,
    PluginMoveDirection Direction,
    PluginMovePace Pace,
    float Amount,
    PluginMoveUnit Unit,
    float Covered,
    float ElapsedSeconds);

/// <summary>
/// The most recent client-driven move on each channel, and the most recent jump.
/// <paramref name="JumpSequence"/> grows by one for every jump begun.
/// </summary>
public readonly record struct PluginMoveReport(
    PluginMoveProgress Travel,
    PluginMoveProgress Strafe,
    PluginMoveProgress Turn,
    long JumpSequence,
    bool JumpCharging)
{
    public PluginMoveProgress this[PluginMoveChannel channel] => channel switch
    {
        PluginMoveChannel.Travel => Travel,
        PluginMoveChannel.Strafe => Strafe,
        _ => Turn,
    };

    /// <summary>The channel a move in <paramref name="direction"/> occupies.</summary>
    public static PluginMoveChannel ChannelOf(PluginMoveDirection direction) => direction switch
    {
        PluginMoveDirection.Forward or PluginMoveDirection.Backward => PluginMoveChannel.Travel,
        PluginMoveDirection.StrafeLeft or PluginMoveDirection.StrafeRight => PluginMoveChannel.Strafe,
        _ => PluginMoveChannel.Turn,
    };
}

/// <summary>Where a walk the client plans to an object stands.</summary>
public enum PluginGoToState
{
    None = 0,

    /// <summary>The client is building its navigation grid or searching it for a route.</summary>
    Planning,

    Walking,

    Arrived,

    /// <summary>No route joins the character to the object.</summary>
    NoRoute,

    /// <summary>The character stopped making progress, even after the client planned again.</summary>
    Blocked,

    /// <summary>A stop, or a later walk, ended it.</summary>
    Stopped,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,

    /// <summary>
    /// The walk stopped where the character stands while something else needs the
    /// character, such as a plugin fighting a monster, and plans on from there once
    /// nothing has needed it for a moment.
    /// </summary>
    Waiting,
}

/// <summary>
/// The most recent walk the client planned to an object. <paramref name="Sequence"/>
/// grows by one for every walk asked for, so a plugin can tell its own from a later
/// one. <paramref name="RemainingMeters"/> is the length of route left while the walk
/// goes on; once it has ended, the straight-line distance from the character to the
/// object, or NaN when the client cannot tell. <paramref name="Reason"/> says what
/// the walk is doing, or why it ended.
/// </summary>
public readonly record struct PluginGoToReport(
    long Sequence,
    PluginGoToState State,
    uint ObjectId,
    float RemainingMeters,
    int Replans,
    string? Reason)
{
    /// <summary>
    /// On a walk that ended blocked, the server object, such as a door that would
    /// not open, beside the spot where it last stopped making progress; otherwise zero.
    /// </summary>
    public uint BlockedByObjectId { get; init; }
}

public enum PluginPlaceKind
{
    /// <summary>Floor wide enough to be a room, set apart from the places beside it by doorways or other narrowings.</summary>
    Room = 0,

    /// <summary>A narrow way, such as a corridor, a ramp or a stair, given in stretches of about 20 m along its length.</summary>
    Passage,

    /// <summary>Open floor too large to call a room, such as land outdoors or a great cavern.</summary>
    Open,

    /// <summary>A building outdoors, given at its origin, with its doorways as exits.</summary>
    Building,

    /// <summary>A landblock beside the character's own, given at its middle on the ground.</summary>
    Landblock,
}

/// <summary>
/// A place the character can walk to: where to stand in it, at its most open point; about
/// how far a walk there goes, or NaN for a building or landblock no walk has measured; what
/// kind of place it is; how much floor it holds; about how
/// wide it is at its widest; how far its floor rises from lowest to highest, as on a stair
/// or ramp; and how many other places it opens onto, so one is a dead end and three or more
/// a junction.
/// </summary>
public readonly record struct PluginNavigationPlace(
    PluginNavigationPosition Position,
    float WalkMeters,
    PluginPlaceKind Kind,
    float AreaSquareMeters,
    float WidthMeters,
    float RiseMeters,
    int Exits)
{
    /// <summary>For a building or a landblock, the landblock it stands in, such as 0xA9B5FFFF; otherwise zero.</summary>
    public uint LandblockId { get; init; }

    /// <summary>Whether a landblock's middle lies under water.</summary>
    public bool IsWater { get; init; }

    private readonly IReadOnlyList<int>? _neighbours;

    /// <summary>
    /// The places this one opens onto, as indices into the same report's <see cref="PluginPlacesReport.Places"/>,
    /// lowest first; none for a building or a landblock.
    /// </summary>
    public IReadOnlyList<int> Neighbours
    {
        get => _neighbours ?? [];
        init => _neighbours = value;
    }
}

public enum PluginPlacesState
{
    /// <summary>The client cannot find places now, such as out of the world or in portal space.</summary>
    Unavailable = 0,

    /// <summary>The client is mapping the ground around the character, or finding the places on it.</summary>
    Mapping,

    Ready,
}

/// <summary>
/// The spots the character can walk to, <see cref="Places"/> nearest walk first, and
/// whether they span the whole sealed dungeon the character is in.
/// </summary>
public readonly record struct PluginPlacesReport(
    PluginPlacesState State,
    IReadOnlyList<PluginNavigationPlace> Places,
    bool InDungeon,
    string? Reason)
{
    /// <summary>Where the character stood when the places were found, which their walks are measured from.</summary>
    public PluginNavigationPosition From { get; init; }
}

public enum PluginNavigationCommandStatus
{
    Unavailable = 0,
    Accepted,
    Rejected,
}

public interface INavigationAutomation
{
    PluginNavigationSnapshot Snapshot { get; }

    bool TryGetObject(uint objectId, out PluginNavigationObject value);

    bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// Detached live world-object projection used by plugin-owned proximity
    /// policies such as VTank's door opener. Hosts may return an empty list.
    /// </summary>
    IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
        Array.Empty<PluginNavigationObject>();

    PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent);

    PluginNavigationCommandStatus ClearMovementIntent();

    PluginNavigationCommandStatus FaceHeading(float headingDegrees) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Starts a move the client carries out and ends by itself. Moves on
    /// different channels are held together, the way movement keys are, so a
    /// plugin can run while it strafes or turns; a new move replaces only the
    /// move on its own channel. <paramref name="amount"/> counts
    /// <paramref name="unit"/>, and zero keeps going until stopped, for at most
    /// thirty seconds. A turn lands on its exact angle. A move also ends when it
    /// stops making progress or runs out of time, and every move ends when the
    /// player moves the character or it enters portal space.
    /// </summary>
    PluginNavigationCommandStatus Move(
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Ends every client-driven move in progress.</summary>
    PluginNavigationCommandStatus StopMoving() =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Ends the client-driven move on one channel, if there is one.</summary>
    PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Jumps with <paramref name="power"/> of a full charge, above 0 and at most 1.</summary>
    PluginNavigationCommandStatus Jump(float power) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>The most recent client-driven move on each channel, and the most recent jump.</summary>
    PluginMoveReport MoveReport => default;

    /// <summary>
    /// Walks the character to an object along a route the client plans through
    /// what it collides with: around walls and objects, through doorways, and up
    /// and down ramps and stairs. The walk ends within
    /// <paramref name="arrivalMeters"/> of the object, at a spot with no wall
    /// between the character and the object, facing it. When the character
    /// stops making progress the client plans again from where it stands,
    /// keeping out of the spot where it stuck, a few times. A later walk, <see cref="StopGoTo"/>, the player
    /// moving the character, or portal space ends it. While the character attacks,
    /// a plugin holds a movement intent, or a plugin that asked with
    /// <see cref="PauseGoToWhile"/> needs the character, the walk stops where the
    /// character stands and waits, then plans again from there and goes on. The
    /// walk steers with client-driven moves, so a plugin's own moves fight it while
    /// it lasts.
    /// </summary>
    PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Walks the character to a place the way <see cref="GoTo(uint, float)"/> walks to
    /// an object: along a route the client plans, ending within
    /// <paramref name="arrivalMeters"/> of <paramref name="position"/>, and waiting while
    /// something else needs the character. It does not turn the character to face
    /// anything on arrival. A position with no cell, such as one read from a VTank route
    /// file, is placed by its map coordinates alone. <see cref="GoToReport"/> reports the
    /// walk with no object id.
    /// </summary>
    PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// The places the character can walk to from where it stands, for choosing where to go
    /// next, told apart on the client's navigation mesh by how open the floor is: rooms,
    /// passages in stretches of about 20 m, and open ground, each at its most open point,
    /// nearest walk first, over the whole sealed dungeon the character is in, or the land
    /// around it; and outdoors, the buildings in the character's landblock and those beside
    /// it, and the landblocks beside its own. The first ask maps the ground, which takes a
    /// moment in a large dungeon,
    /// and reports <see cref="PluginPlacesState.Mapping"/> until the places are found; ask
    /// again to read them, and again once the character has moved on, since walks are
    /// measured from <see cref="PluginPlacesReport.From"/>. A place carries no cell; walk to
    /// one with <see cref="GoTo(PluginNavigationPosition, float)"/>.
    /// </summary>
    PluginPlacesReport CapturePlaces() =>
        new(PluginPlacesState.Unavailable, [], false, "this client does not find places");

    /// <summary>Ends the walk to an object under way, if there is one.</summary>
    PluginNavigationCommandStatus StopGoTo() =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>The most recent walk the client planned to an object.</summary>
    PluginGoToReport GoToReport => default;

    /// <summary>
    /// Has walks to objects wait for a plugin that sometimes needs the character,
    /// such as a combat macro. While <paramref name="need"/> returns what the plugin
    /// is doing, such as "MossTank is running Attack", a walk under way stops where
    /// the character stands and reports that it waits on that. Once nothing has
    /// needed the character for a moment, the walk plans again from where the
    /// character stands and goes on. The client asks on the update thread, every
    /// frame a walk is under way, until the result is disposed.
    /// </summary>
    IDisposable PauseGoToWhile(Func<string?> need) => NoGoToPause.Instance;
}

file sealed class NoGoToPause : IDisposable
{
    public static NoGoToPause Instance { get; } = new();

    public void Dispose()
    {
    }
}
