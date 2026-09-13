using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary><c>say</c>, <c>tell</c> and <c>emote</c>: speech the player's character says.</summary>
internal sealed class ChatVerbs : IVerbFamily
{
    private readonly IPluginHost _host;

    internal ChatVerbs(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["say", "tell", "emote"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return VerbResult.Refused("no character is in the world");
        if (line.Arguments.Length == 0)
            return VerbResult.Refused($"'{line.Verb}' needs something to say");
        if (line.Arguments[0] is '/' or '@')
        {
            return VerbResult.Refused(
                $"'{line.Verb}' sends speech, not commands; give the command line on its own");
        }

        string submitted;
        switch (line.Verb)
        {
            case "say":
                submitted = line.Arguments;
                break;
            case "tell":
                int comma = line.Arguments.IndexOf(',');
                if (comma <= 0 || comma == line.Arguments.Length - 1)
                    return VerbResult.Refused("usage: tell <name>, <message>");
                submitted = $"/tell {line.Arguments}";
                break;
            default:
                submitted = $"/emote {line.Arguments}";
                break;
        }

        return _host.Automation.Chat.Submit(submitted)
            ? VerbResult.Handled
            : VerbResult.Refused("the client did not accept the line");
    }
}
