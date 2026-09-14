using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

internal static class Coordinates
{
    /// <summary>
    /// Decimal places a position's coordinates are stated to, in map units of 240 m: about
    /// 2.4 mm, so a position given back, such as a MossTank route point, stands where it was read.
    /// </summary>
    internal const int Decimals = 5;

    internal static JsonObject Describe(in PluginNavigationPosition position) => new()
    {
        ["cell"] = Facts.Hex(position.CellId),
        ["northSouth"] = Math.Round(position.NorthSouth, Decimals),
        ["eastWest"] = Math.Round(position.EastWest, Decimals),
        ["elevation"] = Math.Round(position.Elevation, Decimals),
        ["indoor"] = !position.IsOutdoor,
        ["text"] = Text(position),
    };

    /// <summary>A place given in map coordinates, which carries no cell, with how players write it.</summary>
    internal static JsonObject DescribePlace(in PluginNavigationPosition position) => new()
    {
        ["northSouth"] = Math.Round(position.NorthSouth, Decimals),
        ["eastWest"] = Math.Round(position.EastWest, Decimals),
        ["elevation"] = Math.Round(position.Elevation, Decimals),
        ["text"] = Text(position),
    };

    /// <summary>Map coordinates the way players write them, such as <c>42.1N, 33.6E</c>.</summary>
    internal static string Text(in PluginNavigationPosition position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(position.NorthSouth):0.0}{(position.NorthSouth < 0 ? 'S' : 'N')}, "
            + $"{Math.Abs(position.EastWest):0.0}{(position.EastWest < 0 ? 'W' : 'E')}");
}
