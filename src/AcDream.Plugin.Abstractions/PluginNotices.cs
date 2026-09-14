namespace AcDream.Plugin.Abstractions;

public enum PluginNoticeSeverity
{
    Info = 0,
    Warning,
    Error,
}

/// <summary>
/// Something a plugin wants a person or an agent to know, such as a combat macro finding
/// its setup wrong or its route stuck: which plugin posted it, a short kind to wait for,
/// how serious it is, a line to read, and details as a JSON object or null.
/// </summary>
public readonly record struct PluginNotice(
    long Sequence,
    string PluginId,
    string Kind,
    PluginNoticeSeverity Severity,
    string Message,
    string? DetailsJson);

/// <summary>
/// Notices plugins post for one another, such as for an agent that passes them on to a
/// model. Notices are posted and read on the update thread, and the host keeps only the
/// most recent.
/// </summary>
public interface IPluginNoticeBoard
{
    /// <summary>
    /// Posts a notice under the posting plugin's id. <paramref name="detailsJson"/> is a JSON
    /// object or null. A host without a notice board takes the notice and keeps nothing.
    /// </summary>
    void Post(string kind, PluginNoticeSeverity severity, string message, string? detailsJson = null)
    {
    }

    /// <summary>The notices still kept that were posted after <paramref name="afterSequence"/>, oldest first.</summary>
    IReadOnlyList<PluginNotice> Capture(long afterSequence) => Array.Empty<PluginNotice>();
}

public sealed class NoOpPluginNoticeBoard : IPluginNoticeBoard
{
    public static NoOpPluginNoticeBoard Instance { get; } = new();

    private NoOpPluginNoticeBoard()
    {
    }
}
