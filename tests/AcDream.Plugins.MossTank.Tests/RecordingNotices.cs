using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

/// <summary>A notice board that keeps every notice posted to it, in order.</summary>
internal sealed class RecordingNotices : IPluginNoticeBoard
{
    private long _sequence;

    internal List<PluginNotice> Posted { get; } = [];

    public void Post(string kind, PluginNoticeSeverity severity, string message, string? detailsJson = null) =>
        Posted.Add(new PluginNotice(++_sequence, "acdream.mosstank", kind, severity, message, detailsJson));

    public IReadOnlyList<PluginNotice> Capture(long afterSequence) =>
        Posted.Where(notice => notice.Sequence > afterSequence).ToArray();
}
