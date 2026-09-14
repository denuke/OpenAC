using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>A trends record every <see cref="PublishSeconds"/>, so a model can wait on a rate falling or rising.</summary>
internal sealed class TrendEvents : IEventProjection
{
    internal const double PublishSeconds = 60d;

    private readonly TrendTracker _trends;
    private readonly AgentClock _clock;
    private double _nextAt = double.NegativeInfinity;

    internal TrendEvents(TrendTracker trends, AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(trends);
        ArgumentNullException.ThrowIfNull(clock);
        _trends = trends;
        _clock = clock;
    }

    public void Rebase() => _nextAt = _clock.Now + PublishSeconds;

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (_clock.Now < _nextAt)
            return;
        _nextAt = _clock.Now + PublishSeconds;
        var trends = new JsonObject();
        if (_trends.TryCapture(_clock.Now, trends))
            publisher.Publish(RecordKinds.Trends, trends);
    }
}
