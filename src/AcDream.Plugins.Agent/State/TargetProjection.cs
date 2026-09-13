using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>What the player has selected.</summary>
internal sealed class TargetProjection : IStateProjection
{
    private const string NothingSelected = "nothing is selected";
    private const string Undescribed =
        "the client holds no description of the selected object";

    private readonly IPluginHost _host;

    internal TargetProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.Target;
    public double PollSeconds => 0d;
    public double MinimumIntervalSeconds => 0d;
    public double? HeartbeatSeconds => null;

    public JsonObject Capture()
    {
        if (_host.Selection.SelectedObjectId is not { } id || id == 0u)
        {
            return new JsonObject
            {
                ["selected"] = false,
                ["guid"] = Facts.Unknown(NothingSelected),
                ["name"] = Facts.Unknown(NothingSelected),
                ["objectClass"] = Facts.Unknown(NothingSelected),
            };
        }

        PluginWorldObject value = default;
        bool found = _host.Automation.IsAvailable
            && _host.Automation.Objects.TryGet(id, out value);
        return new JsonObject
        {
            ["selected"] = true,
            ["guid"] = Facts.Observed(Facts.Hex(id)),
            ["name"] = found && !string.IsNullOrEmpty(value.Name)
                ? Facts.Observed(value.Name)
                : Facts.Unknown(Undescribed),
            ["objectClass"] = found && value.ObjectClass != PluginObjectClass.Unknown
                ? Facts.Observed(WireNames.Kebab(value.ObjectClass.ToString()))
                : Facts.Unknown(Undescribed),
        };
    }
}
