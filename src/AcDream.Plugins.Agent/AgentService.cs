using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent;

/// <summary>
/// Owns everything the plugin does. Game state is only read, and actions are
/// only taken, from <see cref="OnTick"/> and <see cref="HandleCommand"/>, both
/// of which the host calls on its update thread.
/// </summary>
internal sealed class AgentService : IDisposable
{
    internal const string Verb = "agent";

    private static readonly string[] HelpLines =
    [
        "/agent status - whether AI clients can connect, and where",
        "/agent help - this list",
    ];

    private readonly IPluginHost _host;
    private bool _disposed;

    internal AgentService(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    internal void HandleCommand(PluginCommand command)
    {
        if (_disposed)
            return;

        string subcommand = FirstWord(command.Arguments);
        switch (subcommand)
        {
            case "":
            case "help":
                foreach (string line in HelpLines)
                    Say(line);
                break;
            case "status":
                Say("Agent: not listening.");
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
    }

    public void Dispose() => _disposed = true;

    private void Say(string text) =>
        _host.Automation.Chat.PostSystemMessage(text);

    private static string FirstWord(string arguments)
    {
        string trimmed = arguments.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        string word = space < 0 ? trimmed : trimmed[..space];
        return word.ToLowerInvariant();
    }
}
