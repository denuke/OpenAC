using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// Words an agent will reach for that this plugin deliberately does not do.
/// Each is refused with a reason, so none of them is ever spoken as chat.
/// </summary>
internal sealed class UnavailableVerbs : IVerbFamily
{
    private readonly Publisher _publisher;

    internal UnavailableVerbs(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } =
        ["walk", "run", "go", "goto", "strafe", "stop", "logout"];

    public VerbResult Handle(CommandLine line)
    {
        switch (line.Verb)
        {
            case "logout":
                return VerbResult.Refused("ending the session is left to the player");
            case "stop":
                return VerbResult.Refused(
                    "'stop' is ambiguous; use 'cancel' to stop moving or attacking");
            default:
                const string reason =
                    "moving to a place needs navigation, which this plugin does not provide; "
                    + "'turn to <heading>', 'jump' and 'cancel' are available";
                _publisher.Publish(RecordKinds.GoalRefused, new JsonObject
                {
                    ["id"] = line.Id,
                    ["verb"] = line.Verb,
                    ["line"] = line.Text,
                    ["rung"] = "navigation",
                    ["reason"] = reason,
                });
                return VerbResult.Refused(reason);
        }
    }
}
