using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Egress;

/// <summary>A consumer's copy of the record stream.</summary>
internal interface IRecordSink : IDisposable
{
    /// <summary>
    /// Offers one record without waiting. Returns <see langword="false"/> when
    /// the sink had to drop it; the game never waits on a consumer.
    /// </summary>
    bool TryOffer(AgentRecord record);
}
