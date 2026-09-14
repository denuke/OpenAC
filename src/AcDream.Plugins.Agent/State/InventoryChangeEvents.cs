using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>
/// One record each time the count of an item the character holds, by name, goes up, such as
/// loot arriving, or down, such as components spent, with how many and how many it holds now.
/// Splitting, stacking or moving items between packs changes nothing. Counts are compared every
/// <see cref="SampleSeconds"/> once the character has been in the world for
/// <see cref="TrendTracker.SettleSeconds"/>, so the inventory the client hears of on entering
/// is not reported as gained.
/// </summary>
internal sealed class InventoryChangeEvents : IEventProjection
{
    internal const double SampleSeconds = 2d;

    private readonly IPluginHost _host;
    private readonly AgentClock _clock;
    private Dictionary<string, long>? _held;
    private double _inWorldSince = double.NaN;
    private double _nextAt = double.NegativeInfinity;

    internal InventoryChangeEvents(IPluginHost host, AgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(clock);
        _host = host;
        _clock = clock;
    }

    public void Rebase()
    {
        _nextAt = _clock.Now + SampleSeconds;
        if (!InWorld())
        {
            _held = null;
            _inWorldSince = double.NaN;
            return;
        }
        if (double.IsNaN(_inWorldSince))
            _inWorldSince = _clock.Now - TrendTracker.SettleSeconds;
        _held = Settled() ? TrendTracker.Held(_host.Automation) : null;
    }

    public void Poll(Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (_clock.Now < _nextAt)
            return;
        _nextAt = _clock.Now + SampleSeconds;
        if (!InWorld())
        {
            _held = null;
            _inWorldSince = double.NaN;
            return;
        }
        if (double.IsNaN(_inWorldSince))
            _inWorldSince = _clock.Now;
        if (!Settled())
            return;
        Dictionary<string, long> held = TrendTracker.Held(_host.Automation);
        if (_held is { } before)
        {
            foreach ((string name, long count) in held.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                long had = before.GetValueOrDefault(name);
                if (count > had)
                    publisher.Publish(RecordKinds.ItemGained, Change(name, count - had, count));
            }
            foreach ((string name, long had) in before.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                long count = held.GetValueOrDefault(name);
                if (count < had)
                    publisher.Publish(RecordKinds.ItemSpent, Change(name, had - count, count));
            }
        }
        _held = held;
    }

    private bool InWorld() =>
        _host.Automation.IsAvailable && _host.Automation.Character.IsInWorld;

    private bool Settled() => _clock.Now - _inWorldSince >= TrendTracker.SettleSeconds;

    private static JsonObject Change(string name, long count, long total) => new()
    {
        ["name"] = name,
        ["count"] = count,
        ["total"] = total,
    };
}
