using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Egress;

/// <summary>What one cursor read returned.</summary>
/// <param name="Records">Matching records after the cursor, in order.</param>
/// <param name="NextSeq">The cursor to pass to the next read.</param>
/// <param name="Filtered">Records considered but excluded by the kind filter.</param>
/// <param name="Missed">Records evicted before this reader could see them.</param>
/// <param name="More">Whether the read stopped at its maximum with records left.</param>
internal readonly record struct RecordRead(
    IReadOnlyList<AgentRecord> Records,
    long NextSeq,
    int Filtered,
    long Missed,
    bool More);

/// <summary>
/// The most recent records, for tools that read the stream by cursor. The
/// latest record of each pinned kind is kept even after eviction, so a long
/// session never scrolls its own state out of view.
/// </summary>
internal sealed class RecordRing
{
    private readonly object _gate = new();
    private readonly AgentRecord?[] _slots;
    private readonly HashSet<string> _pinnedKinds;
    private readonly Dictionary<string, AgentRecord> _latest =
        new(StringComparer.Ordinal);
    private int _head;
    private int _count;
    private long _lastSeq = -1;

    internal RecordRing(int capacity, IEnumerable<string>? pinnedKinds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _slots = new AgentRecord?[capacity];
        _pinnedKinds = new HashSet<string>(
            pinnedKinds ?? [],
            StringComparer.Ordinal);
    }

    internal long LastSeq
    {
        get
        {
            lock (_gate)
                return _lastSeq;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    internal void Append(AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (record.Seq <= _lastSeq)
            {
                throw new ArgumentException(
                    "Records must be appended in sequence order.",
                    nameof(record));
            }
            _slots[(_head + _count) % _slots.Length] = record;
            if (_count == _slots.Length)
                _head = (_head + 1) % _slots.Length;
            else
                _count++;
            _lastSeq = record.Seq;
            if (_pinnedKinds.Contains(record.Kind))
                _latest[record.Kind] = record;
        }
    }

    internal AgentRecord? Latest(string kind)
    {
        lock (_gate)
            return _latest.TryGetValue(kind, out AgentRecord? record) ? record : null;
    }

    internal RecordRead Read(
        long afterSeq,
        IReadOnlySet<string>? kinds = null,
        int maximum = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        lock (_gate)
        {
            long next = Math.Max(afterSeq, -1L);
            if (_count == 0)
                return new RecordRead(Array.Empty<AgentRecord>(), next, 0, 0, false);

            long oldest = _slots[_head]!.Seq;
            long missed = next + 1 < oldest ? oldest - (next + 1) : 0;
            var records = new List<AgentRecord>();
            int filtered = 0;
            bool more = false;
            for (int index = 0; index < _count; index++)
            {
                AgentRecord record = _slots[(_head + index) % _slots.Length]!;
                if (record.Seq <= afterSeq)
                    continue;
                if (records.Count == maximum)
                {
                    more = true;
                    break;
                }
                next = record.Seq;
                if (kinds is null || kinds.Contains(record.Kind))
                    records.Add(record);
                else
                    filtered++;
            }
            return new RecordRead(records.ToArray(), next, filtered, missed, more);
        }
    }
}
