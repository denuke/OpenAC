using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>Whether a character is in the world, and which one.</summary>
internal sealed class SessionProjection : IStateProjection
{
    private readonly IPluginHost _host;
    private string? _state;
    private string? _from;

    internal SessionProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.Session;
    public double PollSeconds => 0d;
    public double MinimumIntervalSeconds => 0d;
    public double? HeartbeatSeconds => null;

    public JsonObject Capture()
    {
        IAutomationSurface automation = _host.Automation;
        ICharacterInfo character = automation.Character;
        bool inWorld = automation.IsAvailable && character.IsInWorld;
        string state = inWorld ? "in-world" : "out-of-world";
        if (_state is not null && _state != state)
            _from = _state;
        _state = state;

        return new JsonObject
        {
            ["state"] = state,
            ["from"] = _from,
            ["character"] = Named(
                inWorld,
                character.Name,
                "the client has not been told the character's name"),
            ["guid"] = inWorld && character.ObjectId != 0u
                ? Facts.Observed(Facts.Hex(character.ObjectId))
                : Facts.Unknown(NotInWorld),
            ["world"] = Named(
                inWorld,
                character.WorldName,
                "the client has not been told the world's name"),
        };
    }

    private const string NotInWorld = "no character is in the world";

    private static JsonObject Named(bool inWorld, string name, string unnamed) =>
        !inWorld
            ? Facts.Unknown(NotInWorld)
            : string.IsNullOrEmpty(name)
                ? Facts.Unknown(unnamed)
                : Facts.Observed(name);
}
