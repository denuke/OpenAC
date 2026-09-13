using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// Captures each state projection on its own cadence and publishes a record
/// when the state changes, never more often than the projection allows and
/// never losing the last change. Every record says how long its state has
/// held, in <c>ageSeconds</c>.
/// </summary>
internal sealed class StateTracker
{
    private readonly Publisher _publisher;
    private readonly List<Entry> _entries = [];

    internal StateTracker(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    /// <summary>Captures that threw; the projection is skipped for that tick.</summary>
    internal long CaptureFailures { get; private set; }

    internal void Add(IStateProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _entries.Add(new Entry(projection));
    }

    internal void Tick(double now)
    {
        foreach (Entry entry in _entries)
        {
            IStateProjection projection = entry.Projection;
            if (entry.CapturedJson is null
                || now - entry.CapturedAt >= projection.PollSeconds)
            {
                if (!TryCapture(entry, now))
                    continue;
            }

            if (entry.CapturedJson != entry.PublishedJson)
            {
                if (entry.PublishedJson is null
                    || now - entry.PublishedAt >= projection.MinimumIntervalSeconds)
                {
                    Publish(entry, now, batch: null);
                }
            }
            else if (projection.HeartbeatSeconds is { } heartbeat
                && now - entry.PublishedAt >= heartbeat)
            {
                Publish(entry, now, batch: null);
            }
        }
    }

    /// <summary>
    /// Publishes every current state as one batch, sized before the first
    /// record is written, so a consumer that just arrived holds the whole
    /// picture and can tell from any one record whether it holds all of it.
    /// </summary>
    internal void PublishSnapshot(double now)
    {
        List<Entry> captured = _entries
            .Where(entry => TryCapture(entry, now))
            .ToList();
        long id = _publisher.Published;
        for (int ordinal = 0; ordinal < captured.Count; ordinal++)
        {
            Publish(captured[ordinal], now, new JsonObject
            {
                ["id"] = id,
                ["ordinal"] = ordinal,
                ["count"] = captured.Count,
            });
        }
    }

    /// <summary>
    /// Captures and publishes one state now, changed or not, carrying the
    /// <paramref name="id"/> of the command line that asked for it.
    /// </summary>
    internal bool PublishNow(string kind, long id, double now)
    {
        Entry? entry = _entries.Find(candidate => candidate.Projection.Kind == kind);
        if (entry is null || !TryCapture(entry, now))
            return false;
        Publish(entry, now, batch: null, id);
        return true;
    }

    private bool TryCapture(Entry entry, double now)
    {
        string json;
        try
        {
            json = entry.Projection.Capture().ToJsonString(AgentJson.Options);
        }
        catch (Exception)
        {
            CaptureFailures++;
            return false;
        }
        if (json != entry.CapturedJson)
        {
            entry.CapturedJson = json;
            entry.Since = now;
        }
        entry.CapturedAt = now;
        return true;
    }

    private void Publish(Entry entry, double now, JsonObject? batch, long? id = null)
    {
        JsonObject captured = JsonNode.Parse(entry.CapturedJson!)!.AsObject();
        var fields = new JsonObject();
        if (id is { } commandId)
            fields["id"] = commandId;
        foreach (KeyValuePair<string, JsonNode?> field in captured.ToList())
        {
            captured.Remove(field.Key);
            fields[field.Key] = field.Value;
        }
        fields["ageSeconds"] = Math.Round(now - entry.Since, 3);
        _publisher.Publish(entry.Projection.Kind, fields, batch);
        entry.PublishedJson = entry.CapturedJson;
        entry.PublishedAt = now;
    }

    private sealed class Entry(IStateProjection projection)
    {
        internal IStateProjection Projection { get; } = projection;
        internal string? CapturedJson { get; set; }
        internal double CapturedAt { get; set; }
        internal double Since { get; set; }
        internal string? PublishedJson { get; set; }
        internal double PublishedAt { get; set; }
    }
}
