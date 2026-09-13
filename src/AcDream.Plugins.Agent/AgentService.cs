using System.Globalization;
using System.Net.Sockets;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Mcp;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent;

/// <summary>
/// Owns everything the plugin does. Game state is only read, and actions are
/// only taken, from <see cref="OnTick"/> and <see cref="HandleCommand"/>, both
/// of which the host calls on its update thread.
/// </summary>
internal sealed class AgentService : IDisposable
{
    internal const string Verb = "agent";
    internal const int RetainedRecords = 20_000;
    internal const int DefaultPort = 31337;
    internal const string PortSettingKey = "settings/listen-port";

    private static readonly string[] HelpLines =
    [
        "/agent listen [port] - let AI clients on this computer connect over MCP (port 31337 unless given)",
        "/agent stop - stop letting AI clients connect",
        "/agent status - whether AI clients can connect, and what is recorded",
        "/agent record <path> - write every record to a file, one JSON object per line",
        "/agent record off - stop writing the file",
        "/agent do <line> - run one agent command line, as an AI client would",
        "/agent help - this list",
    ];

    private readonly IPluginHost _host;
    private readonly Func<string, IRecordSink> _openRecording;
    private readonly AgentClock _clock = new();
    private readonly StateTracker _state;
    private readonly List<IEventProjection> _events = [];
    private readonly Func<McpEndpoint, int, IMcpListener> _startListener;
    private readonly McpSessions _sessions = new();
    private readonly McpEndpoint _endpoint;
    private IMcpListener? _listener;
    private IRecordSink? _recording;
    private string? _recordingPath;
    private bool _disposed;

    internal AgentService(IPluginHost host)
        : this(host, static path => new FileSink(path))
    {
    }

    internal AgentService(
        IPluginHost host,
        Func<string, IRecordSink> openRecording,
        Func<McpEndpoint, int, IMcpListener>? startListener = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(openRecording);
        _host = host;
        _openRecording = openRecording;
        _startListener = startListener ?? (static (endpoint, port) => McpServer.Start(endpoint, port));
        Ring = new RecordRing(RetainedRecords, RecordKinds.State);
        Publisher = new Publisher(_clock, Ring);
        _state = new StateTracker(Publisher);
        _state.Add(new SessionProjection(host));
        _state.Add(new BodyProjection(host));
        _state.Add(new VitalsProjection(host));
        _state.Add(new StatsProjection(host));
        _state.Add(new TargetProjection(host));
        _state.Add(new CombatModeProjection(host));
        _events.Add(new VitalChangeEvents(host));
        _events.Add(new ChatEvents(host));
        Outcomes = new OutcomeCorrelator(Publisher, _clock);
        Commands = new CommandDispatcher(host, Publisher);
        Commands.Register(new ChatVerbs(host));
        Commands.Register(new UnavailableVerbs(Publisher));
        Commands.Register(new CharacterReadVerbs(host, Publisher, _state, _clock));
        Commands.Register(new WorldReadVerbs(host, Publisher));
        Commands.Register(new TargetVerbs(host, Publisher, Outcomes));
        Commands.Register(new MotorVerbs(host, Publisher, Outcomes));
        Commands.Register(new CastVerbs(host, Publisher, Outcomes));
        Commands.Register(new ObjectVerbs(host, Publisher, Outcomes));
        Commands.Register(new LootVerbs(host, Publisher, Outcomes));
        Commands.Register(new InventoryVerbs(host, Publisher, Outcomes));
        Commands.Register(new VendorVerbs(host, Publisher, Outcomes, _clock));
        Commands.Register(new AttackVerbs(host, Publisher, Outcomes));
        Context = new AgentContext(host, Commands, Ring, Outcomes, _clock, Publisher);
        Tools = McpTools.Create(Context);
        _endpoint = new McpEndpoint(_sessions, Tools);
    }

    internal RecordRing Ring { get; }

    internal Publisher Publisher { get; }

    internal CommandDispatcher Commands { get; }

    internal OutcomeCorrelator Outcomes { get; }

    /// <summary>What consumers such as the MCP tools act and read through.</summary>
    internal AgentContext Context { get; }

    /// <summary>The MCP tools, run from <see cref="OnTick"/>.</summary>
    internal McpToolHost Tools { get; }

    /// <summary>Where AI clients connect, or <see langword="null"/> when not listening.</summary>
    internal string? ListenerUrl => _listener?.Url;

    /// <summary>Event polls or rebases that threw; the source is skipped for that tick.</summary>
    internal long EventFailures { get; private set; }

    /// <summary>Whether any consumer is attached, so records are worth building.</summary>
    internal bool IsActive => _recording is not null || _listener is not null;

    internal void HandleCommand(PluginCommand command)
    {
        if (_disposed)
            return;

        (string subcommand, string rest) = Split(command.Arguments);
        switch (subcommand.ToLowerInvariant())
        {
            case "":
            case "help":
                foreach (string line in HelpLines)
                    Say(line);
                break;
            case "status":
                Status();
                break;
            case "listen":
                Listen(rest);
                break;
            case "stop":
                StopListening(announce: true);
                break;
            case "record":
                Record(rest);
                break;
            case "do":
                Do(rest);
                break;
            default:
                Say($"Unknown /agent command '{subcommand}'. Use /agent help.");
                break;
        }
    }

    internal void OnTick(double elapsedSeconds)
    {
        if (_disposed)
            return;
        _clock.Advance(elapsedSeconds);
        IAutomationSurface automation = _host.Automation;
        Outcomes.Tick(automation.IsAvailable && automation.Character.IsInWorld);
        if (!IsActive)
            return;
        _state.Tick(_clock.Now);
        foreach (IEventProjection source in _events)
        {
            try
            {
                source.Poll(Publisher);
            }
            catch (Exception)
            {
                EventFailures++;
            }
        }
        Tools.Tick();
    }

    internal void Add(IEventProjection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _events.Add(source);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopListening(announce: false);
        StopRecording(announce: false);
    }

    private void Status()
    {
        string listening = _listener is null
            ? "not listening"
            : $"listening on {_listener.Url} with {Sessions(_sessions.Count)}";
        string recording = _recording is null ? "Not recording." : $"Recording to {_recordingPath}.";
        Say($"Agent: {listening}. {recording}");
        if (_listener is not null)
            Say(ConnectLine(_listener.Url));
    }

    private void Listen(string argument)
    {
        if (_listener is not null)
        {
            Say($"Agent: already listening on {_listener.Url}.");
            return;
        }
        int port;
        if (argument.Length == 0)
        {
            port = SavedPort() ?? DefaultPort;
        }
        else if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            Say("Usage: /agent listen [port], where the port is a number from 1 to 65535");
            return;
        }

        IMcpListener listener;
        try
        {
            listener = _startListener(_endpoint, port);
        }
        catch (SocketException error)
        {
            Say($"Agent: could not listen on port {port}: {error.Message}");
            return;
        }

        if (argument.Length > 0)
            SavePort(port);
        bool wasActive = IsActive;
        _listener = listener;
        Activated(wasActive);
        Say($"Agent: listening on {listener.Url}, reachable from this computer only.");
        Say(ConnectLine(listener.Url));
    }

    private void StopListening(bool announce)
    {
        if (_listener is null)
        {
            if (announce)
                Say("Agent: not listening.");
            return;
        }
        IMcpListener listener = _listener;
        _listener = null;
        listener.Dispose();
        Tools.EndAll("the agent stopped listening");
        _sessions.Clear();
        if (announce)
            Say("Agent: stopped listening.");
    }

    /// <summary>
    /// A consumer was attached: state is published again for it, and events
    /// start from now when nothing was attached before.
    /// </summary>
    private void Activated(bool wasActive)
    {
        _state.PublishSnapshot(_clock.Now);
        if (!wasActive)
            RebaseEvents();
    }

    private int? SavedPort()
    {
        try
        {
            string? text = _host.Storage.IsAvailable ? _host.Storage.ReadText(PortSettingKey) : null;
            return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && port is >= 1 and <= 65535
                ? port
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void SavePort(int port)
    {
        if (!_host.Storage.IsAvailable)
            return;
        try
        {
            _host.Storage.WriteText(PortSettingKey, port.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _host.Log.Warn($"Agent could not save the listen port: {error.Message}");
        }
    }

    private static string ConnectLine(string url) =>
        $"Agent: connect an AI client with: claude mcp add --transport http openac {url}";

    private static string Sessions(int count) =>
        count == 1 ? "1 client session" : $"{count} client sessions";

    private void Do(string line)
    {
        if (line.Length == 0)
        {
            Say("Usage: /agent do <command line>");
            return;
        }
        CommandReceipt receipt = Commands.Deliver(line, "chat");
        Say(receipt.Reason is null
            ? $"Agent [{receipt.Id}]: {receipt.Outcome}."
            : $"Agent [{receipt.Id}]: {receipt.Outcome}, {receipt.Reason}.");
    }

    private void Record(string argument)
    {
        if (argument.Length == 0)
        {
            Say("Usage: /agent record <path> or /agent record off");
            return;
        }
        if (argument.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            StopRecording(announce: true);
            return;
        }
        if (_recording is not null)
        {
            Say($"Agent: already recording to {_recordingPath}. "
                + "Use /agent record off first.");
            return;
        }
        if (ResolvePath(argument) is not { } path)
        {
            Say("Agent: give a full path, for example "
                + "/agent record ~/agent-records.jsonl");
            return;
        }

        IRecordSink sink;
        try
        {
            sink = _openRecording(path);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            Say($"Agent: could not open {path}: {error.Message}");
            return;
        }

        bool wasActive = IsActive;
        _recording = sink;
        _recordingPath = path;
        Publisher.Attach(sink);
        Activated(wasActive);
        Say($"Agent: recording to {path}.");
    }

    private void StopRecording(bool announce)
    {
        if (_recording is null)
        {
            if (announce)
                Say("Agent: not recording.");
            return;
        }
        Publisher.Detach(_recording);
        _recording.Dispose();
        _recording = null;
        if (announce)
            Say($"Agent: stopped recording to {_recordingPath}.");
        _recordingPath = null;
    }

    private void RebaseEvents()
    {
        foreach (IEventProjection source in _events)
        {
            try
            {
                source.Rebase();
            }
            catch (Exception)
            {
                EventFailures++;
            }
        }
    }

    private void Say(string text) =>
        _host.Automation.Chat.PostSystemMessage(text);

    private static (string Subcommand, string Remainder) Split(string arguments)
    {
        string trimmed = arguments.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0
            ? (trimmed, string.Empty)
            : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static string? ResolvePath(string argument)
    {
        string path = argument.Trim().Trim('"');
        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
    }
}
