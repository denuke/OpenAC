using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Intake;

/// <summary>What the dispatcher did with one line.</summary>
internal readonly record struct CommandReceipt(long Id, string Outcome, string? Reason);

/// <summary>
/// Routes each command line to the verb family that reserves its first word.
/// A word no family reserves is submitted as chat, which on a live server is
/// speech or a client command. Every delivered line gets exactly one
/// <c>command-outcome</c> record.
/// </summary>
internal sealed class CommandDispatcher
{
    internal static readonly IReadOnlyList<string> OutcomeWords =
        ["handled", "chat", "refused", "failed"];

    private static readonly HashSet<string> ClientClosing =
        new(StringComparer.OrdinalIgnoreCase) { "quit", "exit" };

    private static readonly HashSet<string> LoggingOut =
        new(StringComparer.OrdinalIgnoreCase) { "logout", "logoff" };

    /// <summary>
    /// Client commands that kill the character, change its player-killer status,
    /// or carry it into a player-killer arena. Ordinary recalls stay available.
    /// </summary>
    private static readonly HashSet<string> Irreversible =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "die", "pklite", "pkl", "pkarena", "pka", "pklarena", "pla",
        };

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly Dictionary<string, IVerbFamily> _families =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;

    internal CommandDispatcher(IPluginHost host, Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        _host = host;
        _publisher = publisher;
    }

    internal IReadOnlyCollection<string> ReservedWords => _families.Keys;

    /// <summary>Verbs that threw inside the plugin.</summary>
    internal long Failures { get; private set; }

    internal void Register(IVerbFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        foreach (string word in family.ReservedWords)
        {
            if (!_families.TryAdd(word, family))
            {
                throw new InvalidOperationException(
                    $"The word '{word}' is already reserved by another verb family.");
            }
        }
    }

    /// <summary>Delivers one line. Called on the update thread.</summary>
    internal CommandReceipt Deliver(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        CommandLine line = CommandLine.Parse(_nextId++, text, source);
        (string outcome, string? reason) = Route(line);
        _publisher.Publish(RecordKinds.CommandOutcome, new JsonObject
        {
            ["id"] = line.Id,
            ["line"] = line.Text,
            ["verb"] = line.Verb.Length == 0 ? null : line.Verb,
            ["source"] = source,
            ["outcome"] = outcome,
            ["class"] = OutcomeTable.ClassOf(RecordKinds.CommandOutcome, outcome),
            ["reason"] = reason,
        });
        return new CommandReceipt(line.Id, outcome, reason);
    }

    private (string Outcome, string? Reason) Route(CommandLine line)
    {
        if (line.Text.Length == 0)
            return ("refused", "the line is empty");

        if (_families.TryGetValue(line.Verb, out IVerbFamily? family))
        {
            try
            {
                VerbResult result = family.Handle(line);
                return (result.Outcome, result.Reason);
            }
            catch (Exception error)
            {
                Failures++;
                return ("failed", $"the verb failed inside the plugin: {error.Message}");
            }
        }

        if (line.Text[0] is '/' or '@')
        {
            string command = line.Verb[1..];
            if (command.Equals(AgentService.Verb, StringComparison.OrdinalIgnoreCase))
                return ("refused", "/agent commands cannot be issued through the agent");
            if (ClientClosing.Contains(command))
                return ("refused", "closing the client is left to the player");
            if (LoggingOut.Contains(command))
                return ("refused", "act 'logout' instead, which says when the character list is back");
            if (Irreversible.Contains(command))
            {
                return ("refused", "commands that kill the character or expose it to "
                    + "player killers are left to the player");
            }
        }

        if (!_host.Automation.IsAvailable)
            return ("refused", "no character is in the world, so the line was not sent");

        return _host.Automation.Chat.Submit(line.Text)
            ? ("chat", "no verb reserves this word, so it went to the client as chat or a client command")
            : ("refused", "the client did not accept the line as chat or a client command");
    }
}
