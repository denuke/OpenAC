using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

internal static class Coordinates
{
    internal static JsonObject Describe(in PluginNavigationPosition position)
    {
        var described = new JsonObject
        {
            ["cell"] = Facts.Hex(position.CellId),
            ["northSouth"] = Math.Round(position.NorthSouth, 3),
            ["eastWest"] = Math.Round(position.EastWest, 3),
            ["elevation"] = Math.Round(position.Elevation, 2),
            ["indoor"] = !position.IsOutdoor,
            ["text"] = Text(position),
        };
        if (position.CellId != 0u)
            described["loc"] = Loc(position);
        return described;
    }

    /// <summary>
    /// The position the way the client's /loc writes it: the cell, and the point in that
    /// cell's landblock in meters, such as <c>0xA9B40019 [84 7.1 94.005]</c>.
    /// </summary>
    internal static string Loc(in PluginNavigationPosition position)
    {
        uint blockX = (position.CellId >> 24) & 0xFFu;
        uint blockY = (position.CellId >> 16) & 0xFFu;
        double x = (position.EastWest * 240d) + 84d - ((blockX - 127d) * 192d);
        double y = (position.NorthSouth * 240d) + 84d - ((blockY - 127d) * 192d);
        double z = position.Elevation * 240d;
        return string.Create(CultureInfo.InvariantCulture, $"0x{position.CellId:X8} [{x:0.###} {y:0.###} {z:0.###}]");
    }

    /// <summary>Map coordinates the way players write them, such as <c>42.1N, 33.6E</c>.</summary>
    internal static string Text(in PluginNavigationPosition position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(position.NorthSouth):0.0}{(position.NorthSouth < 0 ? 'S' : 'N')}, "
            + $"{Math.Abs(position.EastWest):0.0}{(position.EastWest < 0 ? 'W' : 'E')}");
}
