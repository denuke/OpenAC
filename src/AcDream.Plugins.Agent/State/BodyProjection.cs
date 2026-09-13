using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// Where the character's body is. The client's own simulation leads, and the
/// last position the server confirmed rides beside it, never blended.
/// </summary>
internal sealed class BodyProjection : IStateProjection
{
    private const string NoBody = "the client has no position for its own body";

    private readonly IPluginHost _host;

    internal BodyProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.Body;
    public double PollSeconds => 0.1d;
    public double MinimumIntervalSeconds => 0.5d;
    public double? HeartbeatSeconds => 5d;

    public JsonObject Capture()
    {
        IAutomationSurface automation = _host.Automation;
        PluginNavigationSnapshot snapshot = automation.IsAvailable
            ? automation.Navigation.Snapshot
            : default;
        if (!snapshot.IsAvailable || snapshot.LocalObjectId == 0u)
        {
            return new JsonObject
            {
                ["guid"] = Facts.Unknown(NoBody),
                ["position"] = Facts.Unknown(NoBody),
                ["heading"] = Facts.Unknown(NoBody),
                ["confirmed"] = Facts.Unknown(NoBody),
                ["moving"] = Facts.Unknown(NoBody),
                ["airborne"] = Facts.Unknown(NoBody),
                ["portalSpace"] = Facts.Unknown(NoBody),
            };
        }

        PluginNavigationPosition position = snapshot.Position;
        JsonObject confirmed;
        if (snapshot.ConfirmedPositionRevision == 0UL)
        {
            confirmed = Facts.Unknown("the server has not confirmed a position yet");
        }
        else
        {
            JsonObject described = Coordinates.Describe(snapshot.ConfirmedPosition);
            described["revision"] = snapshot.ConfirmedPositionRevision;
            confirmed = Facts.Observed(described, "the last position the server accepted");
        }

        return new JsonObject
        {
            ["guid"] = Facts.Observed(Facts.Hex(snapshot.LocalObjectId)),
            ["position"] = Facts.Observed(
                Coordinates.Describe(position),
                "the client's own simulation of its body"),
            ["heading"] = Facts.Observed(Math.Round((double)position.HeadingDegrees, 0)),
            ["confirmed"] = confirmed,
            ["moving"] = Facts.Observed(snapshot.IsMoving),
            ["airborne"] = Facts.Observed(snapshot.IsAirborne),
            ["portalSpace"] = Facts.Observed(snapshot.IsPortalSpace),
        };
    }
}
