using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// One record each time a vital's current value changes, with the value before,
/// so a consumer can wait for a specific moment such as health falling low.
/// </summary>
internal sealed class VitalChangeEvents : IEventProjection
{
    private static readonly string[] Names = ["health", "stamina", "mana"];

    private readonly IPluginHost _host;
    private readonly (uint Current, uint Maximum)?[] _last = new (uint, uint)?[3];

    internal VitalChangeEvents(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public void Rebase() => Read().CopyTo(_last, 0);

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        (uint Current, uint Maximum)?[] readings = Read();
        for (int index = 0; index < readings.Length; index++)
        {
            if (readings[index] is { } now
                && _last[index] is { } before
                && now.Current != before.Current)
            {
                publisher.Publish(RecordKinds.VitalChanged, new JsonObject
                {
                    ["guid"] = Facts.Hex(_host.Automation.Character.ObjectId),
                    ["vital"] = Names[index],
                    ["value"] = now.Current,
                    ["max"] = now.Maximum,
                    ["previous"] = before.Current,
                    ["change"] = (long)now.Current - before.Current,
                });
            }
            _last[index] = readings[index];
        }
    }

    private (uint Current, uint Maximum)?[] Read()
    {
        IAutomationSurface automation = _host.Automation;
        ICharacterInfo character = automation.Character;
        bool inWorld = automation.IsAvailable && character.IsInWorld;
        return
        [
            Reading(inWorld, character.CurrentHealth, character.MaxHealth),
            Reading(inWorld, character.CurrentStamina, character.MaxStamina),
            Reading(inWorld, character.CurrentMana, character.MaxMana),
        ];
    }

    private static (uint Current, uint Maximum)? Reading(
        bool inWorld,
        uint current,
        uint maximum) =>
        inWorld && maximum > 0u ? (current, maximum) : null;
}
