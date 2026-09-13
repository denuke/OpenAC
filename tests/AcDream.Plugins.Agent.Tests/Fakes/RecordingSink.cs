using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class RecordingSink : IRecordSink
{
    internal List<AgentRecord> Records { get; } = [];
    internal bool Disposed { get; private set; }

    public bool TryOffer(AgentRecord record)
    {
        Records.Add(record);
        return true;
    }

    public void Dispose() => Disposed = true;

    internal IEnumerable<JsonElement> OfKind(string kind) =>
        Records.Where(record => record.Kind == kind)
            .Select(record => JsonDocument.Parse(record.Json).RootElement);
}
