using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// How the character is doing over the last 5 and 60 minutes: experience, kills and items
/// gained per hour, items gained and spent per hour by name, and how long since each last
/// happened and since the character last moved. Sampled every <see cref="SampleSeconds"/>
/// whether or not anyone listens, so the rates are ready when a model asks.
/// </summary>
internal sealed class TrendTracker
{
    internal const double SampleSeconds = 10d;
    internal const double ShortWindowSeconds = 300d;
    internal const double LongWindowSeconds = 3600d;

    /// <summary>A rate is given only once at least this much of its window has been counted.</summary>
    internal const double LeastCountedSeconds = 60d;

    /// <summary>
    /// How long a character is in the world before counting starts, so the inventory the
    /// client hears of on entering is not counted as gained.
    /// </summary>
    internal const double SettleSeconds = 10d;

    /// <summary>A move at least this far counts as the character having moved.</summary>
    internal const double MovedMeters = 1d;

    private readonly IPluginHost _host;
    private readonly List<Reading> _history = [];
    private readonly Dictionary<string, long> _gained = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _spent = new(StringComparer.Ordinal);
    private Dictionary<string, long> _held = new(StringComparer.Ordinal);
    private double _nextSampleAt = double.NegativeInfinity;
    private double _inWorldSince = double.NaN;
    private bool _counting;
    private long _lastXp;
    private long _xpGained;
    private long _kills;
    private long _killCursor;
    private long _gainedTotal;
    private double _xpAt;
    private double _killAt;
    private double _gainAt;
    private double _movedAt;
    private PluginNavigationPosition _position;
    private bool _hasPosition;

    internal TrendTracker(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Takes a sample once <see cref="SampleSeconds"/> have passed since the last.</summary>
    internal void Sample(double now)
    {
        if (now < _nextSampleAt)
            return;
        _nextSampleAt = now + SampleSeconds;
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable || !automation.Character.IsInWorld)
        {
            _inWorldSince = double.NaN;
            _counting = false;
            return;
        }
        if (double.IsNaN(_inWorldSince))
            _inWorldSince = now;
        if (!_counting)
        {
            if (now - _inWorldSince >= SettleSeconds)
                Restart(automation, now);
            return;
        }

        long xp = automation.Character.TotalExperience;
        if (xp > _lastXp)
        {
            if (_lastXp > 0)
            {
                _xpGained += xp - _lastXp;
                _xpAt = now;
            }
            _lastXp = xp;
        }
        foreach (PluginKill kill in automation.Combat.CaptureKills(_killCursor))
        {
            if (kill.Sequence <= _killCursor)
                continue;
            _killCursor = kill.Sequence;
            _kills++;
            _killAt = now;
        }
        Dictionary<string, long> held = Held(automation);
        foreach ((string name, long count) in held)
        {
            long before = _held.GetValueOrDefault(name);
            if (count <= before)
                continue;
            _gained[name] = _gained.GetValueOrDefault(name) + (count - before);
            _gainedTotal += count - before;
            _gainAt = now;
        }
        foreach ((string name, long before) in _held)
        {
            long count = held.GetValueOrDefault(name);
            if (count < before)
                _spent[name] = _spent.GetValueOrDefault(name) + (before - count);
        }
        _held = held;
        NoteMove(automation, now);
        Keep(now);
    }

    /// <summary>Adds the trends to a record's fields, or answers false before counting has started.</summary>
    internal bool TryCapture(double now, JsonObject fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (!_counting || _history.Count == 0)
            return false;
        fields["countedSeconds"] = Windows(window => Math.Round(Span(window).Seconds));
        fields["xpPerHour"] = Windows(window => Rate(window, static reading => reading.Xp));
        fields["killsPerHour"] = Windows(window => Rate(window, static reading => reading.Kills));
        fields["itemsGainedPerHour"] = Windows(window => Rate(window, static reading => reading.GainedTotal));
        fields["gainedPerHour"] = Windows(window => ByName(window, static reading => reading.Gained));
        fields["spentPerHour"] = Windows(window => ByName(window, static reading => reading.Spent));
        fields["secondsSinceXp"] = Math.Round(now - _xpAt);
        fields["secondsSinceKill"] = Math.Round(now - _killAt);
        fields["secondsSinceGain"] = Math.Round(now - _gainAt);
        fields["secondsSinceMoved"] = Math.Round(now - _movedAt);
        return true;
    }

    /// <summary>How many of each item, by name, the character carries or wields.</summary>
    internal static Dictionary<string, long> Held(IAutomationSurface automation)
    {
        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (PluginInventoryItem item in automation.Items.CaptureOwnedItems())
        {
            if (!string.IsNullOrEmpty(item.Name))
                held[item.Name] = held.GetValueOrDefault(item.Name) + Math.Max(1, item.StackSize);
        }
        return held;
    }

    private void Restart(IAutomationSurface automation, double now)
    {
        _counting = true;
        _history.Clear();
        _gained.Clear();
        _spent.Clear();
        _xpGained = 0;
        _kills = 0;
        _gainedTotal = 0;
        _lastXp = automation.Character.TotalExperience;
        foreach (PluginKill kill in automation.Combat.CaptureKills(_killCursor))
            _killCursor = Math.Max(_killCursor, kill.Sequence);
        _held = Held(automation);
        _hasPosition = false;
        _xpAt = _killAt = _gainAt = _movedAt = now;
        NoteMove(automation, now);
        Keep(now);
    }

    private void NoteMove(IAutomationSurface automation, double now)
    {
        PluginNavigationSnapshot body = automation.Navigation.Snapshot;
        if (!body.IsAvailable)
            return;
        if (_hasPosition && body.Position.HorizontalDistanceMeters(_position) < MovedMeters)
            return;
        if (_hasPosition)
            _movedAt = now;
        _position = body.Position;
        _hasPosition = true;
    }

    private void Keep(double now)
    {
        _history.Add(new Reading(
            now,
            _xpGained,
            _kills,
            _gainedTotal,
            new Dictionary<string, long>(_gained, StringComparer.Ordinal),
            new Dictionary<string, long>(_spent, StringComparer.Ordinal)));
        int kept = _history.FindIndex(reading => now - reading.At <= LongWindowSeconds + SampleSeconds);
        if (kept > 0)
            _history.RemoveRange(0, kept);
    }

    private static JsonObject Windows(Func<double, JsonNode?> of) => new()
    {
        ["5m"] = of(ShortWindowSeconds),
        ["60m"] = of(LongWindowSeconds),
    };

    /// <summary>The oldest reading within a window of the newest, and how long before the newest it was taken.</summary>
    private (Reading From, double Seconds) Span(double window)
    {
        Reading newest = _history[^1];
        Reading from = _history.First(reading => newest.At - reading.At <= window);
        return (from, newest.At - from.At);
    }

    private JsonNode? Rate(double window, Func<Reading, long> value)
    {
        (Reading from, double seconds) = Span(window);
        return seconds < LeastCountedSeconds
            ? null
            : Math.Round((value(_history[^1]) - value(from)) * 3600d / seconds);
    }

    private JsonNode? ByName(double window, Func<Reading, Dictionary<string, long>> counts)
    {
        (Reading from, double seconds) = Span(window);
        if (seconds < LeastCountedSeconds)
            return null;
        var rates = new JsonObject();
        Dictionary<string, long> before = counts(from);
        foreach ((string name, long total) in counts(_history[^1]).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            long change = total - before.GetValueOrDefault(name);
            if (change > 0)
                rates[name] = Math.Round(change * 3600d / seconds);
        }
        return rates;
    }

    private sealed record Reading(
        double At,
        long Xp,
        long Kills,
        long GainedTotal,
        Dictionary<string, long> Gained,
        Dictionary<string, long> Spent);
}
