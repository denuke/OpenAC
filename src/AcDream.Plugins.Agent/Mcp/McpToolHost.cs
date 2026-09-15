using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>
/// Runs MCP tool calls on the update thread. A call made from any thread is
/// queued, started on the next tick, and stepped on each tick until it has an
/// answer, so tools touch game state only where the host allows it.
/// </summary>
internal sealed class McpToolHost : IMcpTools
{
    internal const double MaximumWaitSeconds = 30d;

    /// <summary>How long one events call may wait, so a model can park until what it waits for happens.</summary>
    internal const double MaximumEventWaitSeconds = 300d;

    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingCall> _incoming = new();
    private readonly List<PendingCall> _running = [];

    internal McpToolHost(IEnumerable<IMcpTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (IMcpTool tool in tools)
            _tools.Add(tool.Name, tool);
    }

    internal int RunningCount => _running.Count;

    public JsonArray List() =>
        new(_tools.Values.Select(tool => (JsonNode?)tool.Definition()).ToArray());

    public bool Has(string name) => _tools.ContainsKey(name);

    /// <summary>
    /// Queues one call. The returned task completes on the update thread, in the
    /// tick that answers the call, or when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public Task<JsonObject> CallAsync(
        string name,
        JsonObject arguments,
        McpSession session,
        CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out IMcpTool? tool))
            return Task.FromResult(ToolResults.Error($"there is no tool named '{name}'"));
        var call = new PendingCall(tool, arguments, session);
        if (cancellationToken.CanBeCanceled)
            call.Cancellation = cancellationToken.Register(() => call.Completion.TrySetCanceled(cancellationToken));
        _incoming.Enqueue(call);
        return call.Completion.Task;
    }

    /// <summary>Starts queued calls and steps running ones. Called on the update thread.</summary>
    internal void Tick()
    {
        while (_incoming.TryDequeue(out PendingCall? call))
        {
            if (call.Completion.Task.IsCompleted)
            {
                call.Finish(null);
                continue;
            }
            try
            {
                call.Run = call.Tool.Start(call.Arguments, call.Session);
            }
            catch (Exception error)
            {
                call.Finish(ToolResults.Error($"the tool failed: {error.Message}"));
                continue;
            }
            _running.Add(call);
        }

        foreach (PendingCall call in _running.ToArray())
        {
            if (call.Completion.Task.IsCompleted)
            {
                _running.Remove(call);
                call.Finish(null);
                continue;
            }
            JsonObject? result;
            try
            {
                result = call.Run!.Step();
            }
            catch (Exception error)
            {
                result = ToolResults.Error($"the tool failed: {error.Message}");
            }
            if (result is not null)
            {
                _running.Remove(call);
                call.Finish(result);
            }
        }
    }

    /// <summary>Answers every queued and running call, for when the server stops.</summary>
    internal void EndAll(string reason)
    {
        while (_incoming.TryDequeue(out PendingCall? call))
            call.Finish(ToolResults.Error(reason));
        foreach (PendingCall call in _running)
            call.Finish(ToolResults.Error(reason));
        _running.Clear();
    }

    private sealed class PendingCall(IMcpTool tool, JsonObject arguments, McpSession session)
    {
        internal IMcpTool Tool { get; } = tool;
        internal JsonObject Arguments { get; } = arguments;
        internal McpSession Session { get; } = session;
        internal IMcpToolRun? Run { get; set; }
        internal CancellationTokenRegistration Cancellation { get; set; }
        internal TaskCompletionSource<JsonObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Finish(JsonObject? result)
        {
            if (result is not null)
                Completion.TrySetResult(result);
            Cancellation.Dispose();
        }
    }
}
