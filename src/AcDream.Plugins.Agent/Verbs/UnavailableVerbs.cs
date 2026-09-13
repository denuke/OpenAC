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

    public IReadOnlyCollection<string> ReservedWords { get; } = ["go", "goto", "logout"];

    public VerbResult Handle(CommandLine line)
    {
        if (line.Verb == "logout")
            return VerbResult.Refused("ending the session is left to the player");

        const string reason =
            "going to a place needs navigation, which this plugin does not provide; "
            + "'walk', 'run' and 'strafe' move for a distance or a time, and 'turn' and 'face' aim the character";
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
