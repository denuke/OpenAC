using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

internal static class Geometry
{
    internal static double DistanceMeters(
        in PluginNavigationPosition from,
        in PluginNavigationPosition to)
    {
        double horizontal = from.HorizontalDistanceMeters(to);
        double vertical = (to.Elevation - from.Elevation) * 240d;
        return Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    /// <summary>Compass bearing from one position to another: 0 is north, 90 is east.</summary>
    internal static double BearingDegrees(
        in PluginNavigationPosition from,
        in PluginNavigationPosition to)
    {
        double north = to.NorthSouth - from.NorthSouth;
        double east = to.EastWest - from.EastWest;
        if (north == 0d && east == 0d)
            return 0d;
        double degrees = Math.Atan2(east, north) * 180d / Math.PI;
        return degrees < 0d ? degrees + 360d : degrees;
    }

    /// <summary>How far to turn from a heading to face a bearing, in (-180, 180].</summary>
    internal static double RelativeDegrees(double bearing, double heading)
    {
        double delta = (bearing - heading) % 360d;
        if (delta > 180d)
            delta -= 360d;
        else if (delta <= -180d)
            delta += 360d;
        return delta;
    }
}
