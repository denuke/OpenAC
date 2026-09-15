using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>How the character is doing over the last 5 and 60 minutes, as counted by <see cref="TrendTracker"/>.</summary>
internal sealed class TrendVerbs : IVerbFamily
{
    private readonly Publisher _publisher;
    private readonly TrendTracker _trends;
    private readonly AgentClock _clock;

    internal TrendVerbs(Publisher publisher, TrendTracker trends, AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(trends);
        ArgumentNullException.ThrowIfNull(clock);
        _publisher = publisher;
        _trends = trends;
        _clock = clock;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["trends"];

    public VerbResult Handle(CommandLine line)
    {
        if (line.Arguments.Length > 0)
            return VerbResult.Refused("trends takes nothing after it");
        var fields = new JsonObject { ["id"] = line.Id };
        if (!_trends.TryCapture(_clock.Now, fields))
            return VerbResult.Refused("nothing is counted yet: counting starts once a character has been in the world for 10 seconds");
        _publisher.Publish(RecordKinds.Trends, fields);
        return VerbResult.Handled;
    }
}
