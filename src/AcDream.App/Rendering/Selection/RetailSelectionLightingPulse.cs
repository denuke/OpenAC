using System.Diagnostics;

namespace AcDream.App.Rendering.Selection;

internal readonly record struct RetailSelectionLighting(float Luminosity, float Diffuse)
{
    public static readonly RetailSelectionLighting Normal = new(0f, 1f);
    public static readonly RetailSelectionLighting Low = new(0f, 0.35f);
    public static readonly RetailSelectionLighting High = new(0.99f, 1f);
}

internal sealed class RetailSelectionLightingPulse
{
    internal const double FlipIntervalSeconds = 0.2;

    /// <summary>Flips a world pick runs through.</summary>
    internal const int WorldFlipBudget = 4;

    /// <summary>Flips the doll's selected-item flash runs through: bright, dark,
    /// done. Each is one interval, so the whole flash lasts two of them.</summary>
    internal const int PartFlipBudget = 2;

    private readonly Func<double> _now;
    private uint _serverGuid;
    private uint _localEntityId;
    private uint _partMask;
    private int _flipCount;
    private int _flipBudget = WorldFlipBudget;
    private double _nextFlip;
    private RetailSelectionLighting _lighting;

    public RetailSelectionLightingPulse(Func<double>? now = null)
        => _now = now ?? MonotonicSeconds;

    public void Start(uint serverGuid, uint localEntityId)
        => Start(serverGuid, localEntityId, partMask: 0u, WorldFlipBudget);

    /// <summary>Flash a whole figure on the doll's schedule rather than a world
    /// pick's - every part of it, however many it has.</summary>
    public void StartWholeFigure(uint serverGuid, uint localEntityId)
        => Start(serverGuid, localEntityId, partMask: 0u, PartFlipBudget);

    /// <summary>Flash only the named parts of one entity: bit N lights part N.
    /// A mask of 0 lights nothing and clears any pulse in flight.</summary>
    public void StartParts(uint serverGuid, uint localEntityId, uint partMask)
    {
        // No parts means nothing to flash. It must never fall through to the
        // whole-entity pulse, and it leaves whatever is already running alone.
        if (partMask == 0u)
            return;
        Start(serverGuid, localEntityId, partMask, PartFlipBudget);
    }

    private void Start(uint serverGuid, uint localEntityId, uint partMask, int flipBudget)
    {
        if (serverGuid == 0u || localEntityId == 0u)
        {
            Clear();
            return;
        }

        _serverGuid = serverGuid;
        _localEntityId = localEntityId;
        _partMask = partMask;
        _flipCount = 1;
        _flipBudget = flipBudget;
        _lighting = RetailSelectionLighting.High;
        _nextFlip = _now() + FlipIntervalSeconds;
    }

    public void Tick()
    {
        if (_flipCount == 0)
            return;

        double now = _now();
        if (now < _nextFlip)
            return;

        int nextCount = _flipCount + 1;
        if (nextCount > _flipBudget)
        {
            Clear();
            return;
        }

        _flipCount = nextCount;
        _lighting = (nextCount & 1) != 0
            ? RetailSelectionLighting.High
            : RetailSelectionLighting.Low;
        _nextFlip = now + FlipIntervalSeconds;
    }

    /// <summary>True while some part of this entity is lit. One test per entity
    /// keeps the per-part lookup off the draw path whenever nothing is flashing.</summary>
    public bool HasPartLighting(uint serverGuid, uint localEntityId)
        => _flipCount != 0
           && _partMask != 0u
           && serverGuid != 0u
           && localEntityId != 0u
           && serverGuid == _serverGuid
           && localEntityId == _localEntityId;

    public bool TryGetPartLighting(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        out RetailSelectionLighting lighting)
    {
        if (HasPartLighting(serverGuid, localEntityId)
            && (uint)partIndex < 32u
            && (_partMask & (1u << partIndex)) != 0u)
        {
            lighting = _lighting;
            return true;
        }

        lighting = default;
        return false;
    }

    public bool TryGet(
        uint serverGuid,
        uint localEntityId,
        out RetailSelectionLighting lighting)
    {
        // A part-masked pulse lights its parts and nothing else, so it must not
        // answer the whole-entity question the ordinary draw path asks first.
        if (_flipCount != 0
            && _partMask == 0u
            && serverGuid != 0u
            && localEntityId != 0u
            && serverGuid == _serverGuid
            && localEntityId == _localEntityId)
        {
            lighting = _lighting;
            return true;
        }

        lighting = default;
        return false;
    }

    public void Clear()
    {
        _serverGuid = 0u;
        _localEntityId = 0u;
        _partMask = 0u;
        _flipCount = 0;
        _flipBudget = WorldFlipBudget;
        _nextFlip = 0d;
        _lighting = default;
    }

    private static double MonotonicSeconds()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
