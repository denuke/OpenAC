using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Egress;

/// <summary>
/// Stamps records into one dense stream and hands each line to every attached
/// sink. Called only on the host's update thread.
/// </summary>
internal sealed class Publisher
{
    private static readonly HashSet<string> EnvelopeNames =
        new(StringComparer.Ordinal) { "schema", "seq", "at", "kind", "batch" };

    private readonly AgentClock _clock;
    private readonly RecordRing _ring;
    private readonly List<IRecordSink> _sinks = [];
    private long _nextSeq;

    internal Publisher(AgentClock clock, RecordRing ring)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ring);
        _clock = clock;
        _ring = ring;
    }

    internal long Published => _nextSeq;

    /// <summary>Records sinks declined, summed across sinks.</summary>
    internal long Dropped { get; private set; }

    internal void Attach(IRecordSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!_sinks.Contains(sink))
            _sinks.Add(sink);
    }

    internal bool Detach(IRecordSink sink) => _sinks.Remove(sink);

    /// <summary>
    /// Publishes one record. The envelope fields come first; <paramref name="fields"/>
    /// follow in their own order and may not reuse an envelope name.
    /// </summary>
    internal AgentRecord Publish(
        string kind,
        JsonObject? fields = null,
        JsonObject? batch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        double at = Math.Round(_clock.Now, 3);
        var envelope = new JsonObject
        {
            ["schema"] = AgentJson.Schema,
            ["seq"] = _nextSeq,
            ["at"] = at,
            ["kind"] = kind,
            ["batch"] = batch,
        };
        if (fields is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> field in fields.ToList())
            {
                if (EnvelopeNames.Contains(field.Key))
                {
                    throw new ArgumentException(
                        $"Record field '{field.Key}' is reserved for the envelope.",
                        nameof(fields));
                }
                fields.Remove(field.Key);
                envelope[field.Key] = field.Value;
            }
        }

        var record = new AgentRecord(
            _nextSeq,
            at,
            kind,
            envelope.ToJsonString(AgentJson.Options));
        _nextSeq++;
        _ring.Append(record);
        foreach (IRecordSink sink in _sinks)
        {
            if (!sink.TryOffer(record))
                Dropped++;
        }
        return record;
    }
}
