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
}
