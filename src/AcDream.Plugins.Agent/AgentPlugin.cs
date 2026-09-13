using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent;

/// <summary>
/// Lets AI clients observe and act through the game client. It serves MCP on
/// this computer's loopback address from the moment it is enabled, so an AI
/// client can log a character in from the character list.
/// </summary>
public sealed class AgentPlugin : IAcDreamPlugin
{
    private readonly Func<IPluginHost, AgentService> _createService;
    private IPluginHost? _host;
    private AgentService? _service;
    private Action<double>? _tick;
    private IDisposable? _commandRegistration;

    public AgentPlugin()
        : this(static host => new AgentService(host))
    {
    }

    internal AgentPlugin(Func<IPluginHost, AgentService> createService)
    {
        ArgumentNullException.ThrowIfNull(createService);
        _createService = createService;
    }

    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        host.Log.Info("Agent initialized");
    }

    public void Enable()
    {
        if (_host is null || _service is not null)
            return;

        AgentService service = _createService(_host);
        _service = service;
        _commandRegistration = _host.Commands.Register(
            AgentService.Verb,
            service.HandleCommand);
        _tick = service.OnTick;
        _host.Events.Tick += _tick;
        service.StartListening();
        _host.Log.Info("Agent enabled");
    }

    public void Disable()
    {
        if (_host is not null && _tick is not null)
            _host.Events.Tick -= _tick;
        _tick = null;
        _commandRegistration?.Dispose();
        _commandRegistration = null;
        _service?.Dispose();
        _service = null;
        _host?.Log.Info("Agent disabled");
    }
}
