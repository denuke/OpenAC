using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

internal static class Coordinates
{
    internal static JsonObject Describe(in PluginNavigationPosition position) => new()
    {
        ["cell"] = Facts.Hex(position.CellId),
        ["northSouth"] = Math.Round(position.NorthSouth, 3),
        ["eastWest"] = Math.Round(position.EastWest, 3),
        ["elevation"] = Math.Round(position.Elevation, 2),
        ["indoor"] = !position.IsOutdoor,
        ["text"] = Text(position),
    };

    /// <summary>Map coordinates the way players write them, such as <c>42.1N, 33.6E</c>.</summary>
    internal static string Text(in PluginNavigationPosition position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(position.NorthSouth):0.0}{(position.NorthSouth < 0 ? 'S' : 'N')}, "
            + $"{Math.Abs(position.EastWest):0.0}{(position.EastWest < 0 ? 'W' : 'E')}");
}
