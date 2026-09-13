using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent;

/// <summary>
/// Lets AI clients observe and act through the game client. Inert until a
/// player turns it on with <c>/agent listen</c>.
/// </summary>
public sealed class AgentPlugin : IAcDreamPlugin
{
    private IPluginHost? _host;
    private AgentService? _service;
    private Action<double>? _tick;
    private IDisposable? _commandRegistration;

    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _service = new AgentService(host);
        host.Log.Info("Agent initialized");
    }

    public void Enable()
    {
        if (_host is null || _service is null)
            return;

        _commandRegistration = _host.Commands.Register(
            AgentService.Verb,
            _service.HandleCommand);
        _tick = _service.OnTick;
        _host.Events.Tick += _tick;
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
        _host?.Log.Info("Agent disabled");
    }
}
