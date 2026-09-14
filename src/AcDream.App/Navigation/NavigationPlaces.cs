using System.Numerics;
using AcDream.Core.Navigation;

namespace AcDream.App.Navigation;

internal enum NavigationPlaceKind
{
    Room = 0,
    Passage,
    Open,

    /// <summary>A building, given at its origin, with its doorways as exits.</summary>
    Building,

    /// <summary>A landblock beside the character's own, given at its middle.</summary>
    Landblock,
}

/// <summary>
/// A place the character can walk to: where it stands measured from the corner of the first
/// landblock, the way a place with no cell is given, with what <see cref="NavPlace"/> tells of
/// a room, passage or open ground. A building or landblock has no walk measured.
/// </summary>
internal readonly record struct NavigationPlace(
    Vector3 Global,
    float WalkMeters,
    NavigationPlaceKind Kind,
    float AreaSquareMeters,
    float WidthMeters,
    float RiseMeters,
    int Exits)
{
    /// <summary>For a building or a landblock, the landblock it stands in, such as 0xA9B5FFFF; otherwise zero.</summary>
    public uint LandblockId { get; init; }

    /// <summary>Whether a landblock's middle lies under water.</summary>
    public bool IsWater { get; init; }

    internal static NavigationPlaceKind KindOf(NavPlaceKind kind) => kind switch
    {
        NavPlaceKind.Passage => NavigationPlaceKind.Passage,
        NavPlaceKind.Open => NavigationPlaceKind.Open,
        _ => NavigationPlaceKind.Room,
    };
}

internal enum NavigationPlacesState
{
    Unavailable = 0,
    Mapping,
    Ready,
}

/// <summary>The places the character can walk to, nearest walk first, and whether they span a sealed dungeon.</summary>
internal sealed record NavigationPlacesReport(
    NavigationPlacesState State,
    IReadOnlyList<NavigationPlace> Places,
    bool InDungeon,
    string Reason)
{
    internal static readonly NavigationPlacesReport None =
        new(NavigationPlacesState.Unavailable, [], false, "no places have been asked for");

    /// <summary>Where the character stood when the places were found, measured from the corner of the first landblock.</summary>
    public Vector3 FromGlobal { get; init; }
}
