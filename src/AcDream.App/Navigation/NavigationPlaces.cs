using System.Numerics;
using AcDream.Core.Navigation;

namespace AcDream.App.Navigation;

/// <summary>
/// A place the character can walk to: its most open point measured from the corner of the
/// first landblock, the way a place with no cell is given, with the rest of what
/// <see cref="NavPlace"/> tells of it.
/// </summary>
internal readonly record struct NavigationPlace(
    Vector3 Global,
    float WalkMeters,
    NavPlaceKind Kind,
    float AreaSquareMeters,
    float WidthMeters,
    float RiseMeters,
    int Exits);

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
