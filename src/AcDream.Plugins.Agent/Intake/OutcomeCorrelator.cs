using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Intake;

/// <summary>How an action ended, as its family's own outcome word.</summary>
internal readonly record struct Resolution(
    string Word,
    string? Reason = null,
    JsonObject? Fields = null);

/// <summary>
/// Watches each accepted action until it resolves, and publishes exactly one
/// terminal record for it. An action that does not resolve within its window
/// is <c>unconfirmed</c>, never assumed to have worked; an action pending when
/// the character leaves the world is <c>lost</c>.
/// </summary>
internal sealed class OutcomeCorrelator
{
    internal const string Unconfirmed = "unconfirmed";
    internal const string Lost = "lost";

    /// <summary>Words the correlator itself can end any action with.</summary>
    internal static readonly IReadOnlyList<string> OutcomeWords = [Unconfirmed, Lost];

    private readonly Publisher _publisher;
    private readonly AgentClock _clock;
    private readonly List<Pending> _pending = [];

    internal OutcomeCorrelator(Publisher publisher, AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(clock);
        _publisher = publisher;
        _clock = clock;
    }

    internal int PendingCount => _pending.Count;

    /// <summary>The actions still waiting for an answer, oldest first.</summary>
    internal IReadOnlyList<(long Id, string Verb, string OutcomeKind)> PendingActions =>
        _pending.Select(pending => (pending.Id, pending.Verb, pending.OutcomeKind)).ToArray();

    /// <summary>Probes that threw; the action is ended as unconfirmed.</summary>
    internal long ProbeFailures { get; private set; }

    /// <summary>
    /// Watches line <paramref name="id"/> until <paramref name="probe"/> returns
    /// a resolution. The probe runs on the update thread and returns
    /// <see langword="null"/> while the action is still pending.
    /// </summary>
    internal void Watch(
        long id,
        string verb,
        string outcomeKind,
        double windowSeconds,
        Func<Resolution?> probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeKind);
        ArgumentNullException.ThrowIfNull(probe);
        _pending.Add(new Pending(id, verb, outcomeKind, _clock.Now + windowSeconds, probe));
    }

    internal void ResolveNow(long id, string verb, string outcomeKind, Resolution resolution) =>
        Publish(id, verb, outcomeKind, resolution);

    /// <summary>Ends every pending action of one outcome kind with the same word.</summary>
    internal int EndAll(string outcomeKind, string word, string reason)
    {
        Pending[] ending = _pending.Where(pending => pending.OutcomeKind == outcomeKind).ToArray();
        foreach (Pending pending in ending)
        {
            _pending.Remove(pending);
            Publish(pending.Id, pending.Verb, pending.OutcomeKind, new Resolution(word, reason));
        }
        return ending.Length;
    }

    /// <summary>Probes every pending action. Called on the update thread.</summary>
    internal void Tick(bool inWorld)
    {
        double now = _clock.Now;
        foreach (Pending pending in _pending.ToArray())
        {
            Resolution? resolution;
            if (!inWorld)
            {
                resolution = new Resolution(Lost, "the character left the world before an answer arrived");
            }
            else
            {
                try
                {
                    resolution = pending.Probe();
                }
                catch (Exception error)
                {
                    ProbeFailures++;
                    resolution = new Resolution(
                        Unconfirmed,
                        $"checking for an answer failed inside the plugin: {error.Message}");
                }
                if (resolution is null && now >= pending.Deadline)
                {
                    resolution = new Resolution(
                        Unconfirmed,
                        "no answer arrived in time; the action may still have happened");
                }
            }

            if (resolution is { } ended)
            {
                _pending.Remove(pending);
                Publish(pending.Id, pending.Verb, pending.OutcomeKind, ended);
            }
        }
    }

    private void Publish(long id, string verb, string outcomeKind, Resolution resolution)
    {
        var fields = new JsonObject
        {
            ["id"] = id,
            ["verb"] = verb,
            ["outcome"] = resolution.Word,
            ["class"] = OutcomeTable.ClassOf(outcomeKind, resolution.Word),
            ["reason"] = resolution.Reason,
        };
        if (resolution.Fields is { } extra)
        {
            foreach (KeyValuePair<string, JsonNode?> field in extra.ToList())
            {
                extra.Remove(field.Key);
                fields[field.Key] = field.Value;
            }
        }
        _publisher.Publish(outcomeKind, fields);
    }

    private sealed record Pending(
        long Id,
        string Verb,
        string OutcomeKind,
        double Deadline,
        Func<Resolution?> Probe);
}
