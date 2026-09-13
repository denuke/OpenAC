using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>The character's level and six primary attributes.</summary>
internal sealed class StatsProjection : IStateProjection
{
    private static readonly string[] AttributeKeys =
        ["strength", "endurance", "quickness", "coordination", "focus", "self"];

    private readonly IPluginHost _host;

    internal StatsProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.Stats;
    public double PollSeconds => 1d;
    public double MinimumIntervalSeconds => 1d;
    public double? HeartbeatSeconds => null;

    public JsonObject Capture()
    {
        IAutomationSurface automation = _host.Automation;
        ICharacterInfo character = automation.Character;
        bool inWorld = automation.IsAvailable && character.IsInWorld;
        IReadOnlyList<PluginAttributeInfo> known = inWorld
            ? character.Attributes
            : Array.Empty<PluginAttributeInfo>();
        string attributesMissing = inWorld
            ? "the server has not sent this attribute"
            : VitalsProjection.NotInWorld;

        var attributes = new JsonObject();
        for (int kind = 0; kind < AttributeKeys.Length; kind++)
        {
            PluginAttributeInfo? found = null;
            foreach (PluginAttributeInfo candidate in known)
            {
                if (candidate.Kind == kind)
                {
                    found = candidate;
                    break;
                }
            }
            attributes[AttributeKeys[kind]] = found is { } attribute
                ? new JsonObject
                {
                    ["current"] = Facts.Observed(attribute.Current),
                    ["base"] = Facts.Observed(attribute.Base),
                }
                : new JsonObject
                {
                    ["current"] = Facts.Unknown(attributesMissing),
                    ["base"] = Facts.Unknown(attributesMissing),
                };
        }

        return new JsonObject
        {
            ["guid"] = inWorld && character.ObjectId != 0u
                ? Facts.Observed(Facts.Hex(character.ObjectId))
                : Facts.Unknown(VitalsProjection.NotInWorld),
            ["level"] = !inWorld
                ? Facts.Unknown(VitalsProjection.NotInWorld)
                : character.Level > 0
                    ? Facts.Observed(character.Level)
                    : Facts.Unknown("the server has not sent the character's level"),
            ["attributes"] = attributes,
        };
    }
}
