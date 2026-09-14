using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Navigation;

/// <summary>The local player's body, sampled from its movement controller and moved with scripted moves.</summary>
internal sealed class RuntimeNavigationWalkBody : INavigationWalkBody
{
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly IRuntimePortalView _portal;

    public RuntimeNavigationWalkBody(RuntimeLocalPlayerMovementState movement, IRuntimePortalView portal)
    {
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
    }

    public bool TrySample(out NavigationWalkBodySample sample)
    {
        if (_movement.Controller is not { CellId: not 0u } controller)
        {
            sample = default;
            return false;
        }

        RuntimePortalSnapshot portal = _portal.Snapshot;
        sample = new NavigationWalkBodySample(
            controller.Position,
            MoveToMath.HeadingFromYaw(controller.Yaw),
            NavBody.Player(controller.StepUpHeight, controller.StepDownHeight),
            _movement.ScriptedMove,
            portal.Kind != RuntimePortalKind.None && !portal.Completed && !portal.Cancelled,
            controller.CellId);
        return true;
    }

    public bool BeginMove(in RuntimeMoveRequest request) => _movement.BeginMove(request);

    public bool StopMove(RuntimeMoveChannel channel) => _movement.StopMove(channel);
}

/// <summary>
/// Finds an object by its collision in the physics world, or failing that by
/// the position the server last gave it, placed relative to the local player.
/// </summary>
internal sealed class RuntimeNavigationGoalSource : INavigationGoalSource
{
    private readonly PhysicsEngine _physics;
    private readonly GameRuntime _runtime;
    private readonly RuntimeLocalPlayerMovementState _movement;

    public RuntimeNavigationGoalSource(
        PhysicsEngine physics,
        GameRuntime runtime,
        RuntimeLocalPlayerMovementState movement)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
    }

    public bool TryLocate(uint objectId, out Vector3 position)
    {
        bool known = _runtime.EntityObjects.Entities.TryGetActive(objectId, out RuntimeEntityRecord record);
        if (known && record.PhysicsBody is { } body && body.CellPosition.ObjCellId != 0u)
        {
            position = body.Position;
            return true;
        }

        foreach (ShadowEntry entry in _physics.ShadowObjects.AllEntriesForDebug())
        {
            if (entry.EntityId == objectId)
            {
                position = entry.Position;
                return true;
            }
        }

        if (known && _movement.Controller is { } controller && record.Snapshot.Position is { } placed)
        {
            AcDream.Core.Physics.Position here = controller.CellPosition;
            int blocksEast = (int)((placed.LandblockId >> 24) & 0xFFu) - (int)((here.ObjCellId >> 24) & 0xFFu);
            int blocksNorth = (int)((placed.LandblockId >> 16) & 0xFFu) - (int)((here.ObjCellId >> 16) & 0xFFu);
            position = controller.Position + new Vector3(
                (blocksEast * NavGeometry.LandblockSize) + placed.PositionX - here.Frame.Origin.X,
                (blocksNorth * NavGeometry.LandblockSize) + placed.PositionY - here.Frame.Origin.Y,
                placed.PositionZ - here.Frame.Origin.Z);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>
    /// The server object whose collision edge comes nearest a spot. Live objects
    /// collide under the client's own ids, so each is matched back to its record;
    /// a door counts as closed while it still collides.
    /// </summary>
    public bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
    {
        uint player = _runtime.PlayerIdentity.ServerGuid;
        RuntimeEntityRecord? nearestRecord = null;
        float nearest = radius;
        foreach (ShadowEntry entry in _physics.ShadowObjects.AllEntriesForDebug())
        {
            if (!_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.ServerGuid == player)
            {
                continue;
            }
            float dx = entry.Position.X - position.X;
            float dy = entry.Position.Y - position.Y;
            float edge = MathF.Sqrt((dx * dx) + (dy * dy)) - entry.Radius;
            if (edge < nearest)
            {
                nearest = edge;
                nearestRecord = record;
            }
        }
        if (nearestRecord is null)
        {
            blocker = default;
            return false;
        }

        uint objectId = nearestRecord.ServerGuid;
        ClientObject? item = _runtime.InventoryOwner.Objects.Get(objectId);
        bool door = item is not null
            && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Door) != 0;
        bool closed = door && !nearestRecord.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal);
        blocker = new NavigationBlocker(objectId, item?.Name ?? nearestRecord.Snapshot.Name ?? $"0x{objectId:X8}", closed);
        return true;
    }
}

/// <summary>
/// Doors as the client sees them. A door is closed while its physics still
/// collides, and using one goes through the client's own use, which walks the
/// character into the door's use range first.
/// </summary>
internal sealed class RuntimeNavigationDoors : INavigationDoors
{
    /// <summary>A door more than this far above or below the character is not on its way.</summary>
    private const float DoorHeightReach = 3f;

    private readonly PhysicsEngine _physics;
    private readonly GameRuntime _runtime;
    private readonly Action<uint> _use;

    public RuntimeNavigationDoors(PhysicsEngine physics, GameRuntime runtime, Action<uint> use)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _use = use ?? throw new ArgumentNullException(nameof(use));
    }

    public bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door)
    {
        var start = new Vector2(from.X, from.Y);
        Vector2 along = new Vector2(to.X, to.Y) - start;
        float lengthSquared = along.LengthSquared();
        uint nearestId = 0u;
        float nearestAhead = float.PositiveInfinity;
        foreach (ShadowEntry entry in _physics.ShadowObjects.AllEntriesForDebug())
        {
            if (MathF.Abs(entry.Position.Z - from.Z) > DoorHeightReach
                || !_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
                || !IsDoor(record.ServerGuid))
            {
                continue;
            }
            var at = new Vector2(entry.Position.X, entry.Position.Y);
            float t = lengthSquared > 1e-6f ? Vector2.Dot(at - start, along) / lengthSquared : 0f;
            if (t < 0f)
                continue;
            t = MathF.Min(t, 1f);
            if (Vector2.Distance(at, start + (along * t)) > corridor)
                continue;
            float ahead = t * MathF.Sqrt(lengthSquared);
            if (ahead < nearestAhead)
            {
                nearestAhead = ahead;
                nearestId = record.ServerGuid;
            }
        }
        if (nearestId == 0u)
        {
            door = default;
            return false;
        }
        door = new NavigationDoor(nearestId, _runtime.InventoryOwner.Objects.Get(nearestId)?.Name ?? $"0x{nearestId:X8}");
        return true;
    }

    public bool IsOpen(uint doorId) =>
        _runtime.EntityObjects.Entities.TryGetActive(doorId, out RuntimeEntityRecord record)
        && record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal);

    public void Use(uint doorId) => _use(doorId);

    private bool IsDoor(uint objectId) =>
        _runtime.InventoryOwner.Objects.Get(objectId) is { } item
        && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Door) != 0;
}
