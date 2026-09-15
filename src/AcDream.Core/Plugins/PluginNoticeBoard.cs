using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>Process-local board of the notices plugins post, keeping the most recent.</summary>
public sealed class PluginNoticeBoard : IPluginNoticeBoard
{
    /// <summary>How many notices the board keeps.</summary>
    public const int Capacity = 500;

    private readonly object _gate = new();
    private readonly Queue<PluginNotice> _notices = new();
    private long _sequence;

    public void Post(string kind, PluginNoticeSeverity severity, string message, string? detailsJson = null) =>
        Post("host", kind, severity, message, detailsJson);

    /// <summary>Posts a notice under a plugin's id.</summary>
    public void Post(string pluginId, string kind, PluginNoticeSeverity severity, string message, string? detailsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            _notices.Enqueue(new PluginNotice(++_sequence, pluginId, kind.Trim(), severity, message, detailsJson));
            while (_notices.Count > Capacity)
                _notices.Dequeue();
        }
    }

    public IReadOnlyList<PluginNotice> Capture(long afterSequence)
    {
        lock (_gate)
            return _notices.Where(notice => notice.Sequence > afterSequence).ToArray();
    }
}
