using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.State;

/// <summary>Health, stamina and mana, each with its maximum.</summary>
internal sealed class VitalsProjection : IStateProjection
{
    internal const string NotInWorld = "no character is in the world";
    internal const string NotReceived = "the server has not sent this vital";

    private readonly IPluginHost _host;

    internal VitalsProjection(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string Kind => RecordKinds.Vitals;
    public double PollSeconds => 0d;
    public double MinimumIntervalSeconds => 0d;
    public double? HeartbeatSeconds => null;

    public JsonObject Capture()
    {
        IAutomationSurface automation = _host.Automation;
        ICharacterInfo character = automation.Character;
        bool inWorld = automation.IsAvailable && character.IsInWorld;
        return new JsonObject
        {
            ["guid"] = inWorld && character.ObjectId != 0u
                ? Facts.Observed(Facts.Hex(character.ObjectId))
                : Facts.Unknown(NotInWorld),
            ["health"] = Vital(inWorld, character.CurrentHealth, character.MaxHealth),
            ["stamina"] = Vital(inWorld, character.CurrentStamina, character.MaxStamina),
            ["mana"] = Vital(inWorld, character.CurrentMana, character.MaxMana),
        };
    }

    /// <summary>
    /// A maximum of zero means the vital was never received, because a real
    /// maximum is never zero. A current of zero with a known maximum is real.
    /// </summary>
    private static JsonObject Vital(bool inWorld, uint current, uint maximum)
    {
        if (!inWorld || maximum == 0u)
        {
            string because = inWorld ? NotReceived : NotInWorld;
            return new JsonObject
            {
                ["current"] = Facts.Unknown(because),
                ["max"] = Facts.Unknown(because),
                ["fraction"] = Facts.Unknown(because),
            };
        }
        return new JsonObject
        {
            ["current"] = Facts.Observed(current),
            ["max"] = Facts.Observed(maximum),
            ["fraction"] = Facts.Derived(
                Math.Round((double)current / maximum, 3),
                "current and max",
                "current divided by max"),
        };
    }
}
