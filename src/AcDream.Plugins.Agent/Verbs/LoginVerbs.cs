using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>characters</c>, <c>login &lt;name or id&gt;</c> and <c>logout</c>: the
/// account's characters at the character list, entering the world as one of them
/// the way choosing it and pressing enter does, and logging the character out to
/// the character list again. They answer while no character is in the world.
/// </summary>
internal sealed class LoginVerbs : IVerbFamily
{
    /// <summary>How long a login has to reach the world, loading included.</summary>
    internal const double WindowSeconds = 90d;

    /// <summary>How long a logout has to bring the character list back.</summary>
    internal const double LogoutWindowSeconds = 60d;

    internal const string Completed = "completed";
    internal const string Refused = "refused";
    internal const string MidAir = "the character cannot log out in mid-air";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Completed, Refused];

    private const string Usage = "usage: login <character name or id>";
    private const string Unavailable = "this client cannot log in a character for a plugin";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal LoginVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["characters", "login", "logout"];

    public VerbResult Handle(CommandLine line) => line.Verb switch
    {
        "characters" => Characters(line),
        "logout" => Logout(line),
        _ => Login(line),
    };

    private VerbResult Characters(CommandLine line)
    {
        ILoginAutomation login = _host.Automation.Login;
        if (!login.IsAvailable)
            return Refuse(line, "this client cannot show its character list to a plugin");

        PluginLoginSnapshot snapshot = login.Snapshot;
        var characters = new JsonArray();
        foreach (PluginLoginCharacter character in login.CaptureRoster())
        {
            characters.Add(new JsonObject
            {
                ["guid"] = Facts.Hex(character.ObjectId),
                ["name"] = character.Name,
                ["canEnter"] = character.ObjectId != 0u && !character.IsPendingDelete,
                ["pendingDelete"] = character.IsPendingDelete,
            });
        }
        _publisher.Publish(RecordKinds.Characters, new JsonObject
        {
            ["id"] = line.Id,
            ["stage"] = SessionProjection.StageWord(snapshot.Stage),
            ["world"] = snapshot.WorldName,
            ["chosen"] = snapshot.ChosenObjectId != 0u ? Facts.Hex(snapshot.ChosenObjectId) : null,
            ["error"] = snapshot.Error,
            ["characters"] = characters,
        });
        return VerbResult.Handled;
    }

    private VerbResult Login(CommandLine line)
    {
        string named = line.Arguments.Trim();
        if (named.Length == 0)
            return Refuse(line, Usage);
        ILoginAutomation login = _host.Automation.Login;
        if (!login.IsAvailable)
            return Refuse(line, Unavailable);
        if (Away(login.Snapshot.Stage) is { } away)
            return Refuse(line, away);
        if (!TryFind(login.CaptureRoster(), named, out PluginLoginCharacter character, out string? problem))
            return Refuse(line, problem!);
        if (character.IsPendingDelete)
            return Refuse(line, $"{character.Name} is waiting to be deleted, so it cannot enter the world");

        PluginLoginCommandStatus status = login.EnterWorld(character.ObjectId);
        if (status != PluginLoginCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginLoginCommandStatus.Unavailable
                ? Unavailable
                : login.Snapshot.Error ?? "the client did not enter the world");
        }

        _publisher.Publish(RecordKinds.LoginSent, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(character.ObjectId),
            ["name"] = character.Name,
        });
        IAutomationSurface automation = _host.Automation;
        _outcomes.Watch(
            line.Id,
            line.Verb,
            RecordKinds.LoginOutcome,
            WindowSeconds,
            () => automation.IsAvailable && automation.Character.IsInWorld
                ? new Resolution(Completed)
                : login.Snapshot is { Stage: PluginLoginStage.ChoosingCharacter, Error: { } error }
                    ? new Resolution(Refused, error)
                    : null,
            outOfWorld: true);
        return VerbResult.Handled;
    }

    private VerbResult Logout(CommandLine line)
    {
        if (line.Arguments.Trim().Length > 0)
            return Refuse(line, "logout takes nothing after it");
        ILoginAutomation login = _host.Automation.Login;
        IAutomationSurface automation = _host.Automation;
        if (!login.IsAvailable)
            return Refuse(line, "this client cannot log a character out for a plugin");
        if (!(automation.IsAvailable && automation.Character.IsInWorld))
            return Refuse(line, "no character is in the world");
        if (automation.Navigation.Snapshot is { IsAvailable: true, IsAirborne: true })
            return Refuse(line, MidAir);
        uint objectId = automation.Character.ObjectId;
        string name = automation.Character.Name;

        PluginLoginCommandStatus status = login.LogOut();
        if (status != PluginLoginCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginLoginCommandStatus.Unavailable
                ? "this client cannot log a character out for a plugin"
                : "the client did not log the character out");
        }

        _publisher.Publish(RecordKinds.LoginSent, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = objectId != 0u ? Facts.Hex(objectId) : null,
            ["name"] = string.IsNullOrEmpty(name) ? null : name,
        });
        _outcomes.Watch(
            line.Id,
            line.Verb,
            RecordKinds.LoginOutcome,
            LogoutWindowSeconds,
            () => !automation.IsAvailable && login.Snapshot.Stage == PluginLoginStage.ChoosingCharacter
                ? new Resolution(Completed)
                : null,
            outOfWorld: true);
        return VerbResult.Handled;
    }

    /// <summary>Why a login cannot start from where the client stands, or null at the character list.</summary>
    private static string? Away(PluginLoginStage stage) => stage switch
    {
        PluginLoginStage.ChoosingCharacter => null,
        PluginLoginStage.InWorld => "a character is already in the world",
        PluginLoginStage.EnteringWorld => "a character is already entering the world",
        PluginLoginStage.Connecting => "the client is still connecting to the server; try again once the character list is up",
        _ => "the client is not connected to a server",
    };

    /// <summary>A character by its id, its whole name, or the only name that starts with what was given.</summary>
    private static bool TryFind(
        IReadOnlyList<PluginLoginCharacter> roster,
        string named,
        out PluginLoginCharacter found,
        out string? problem)
    {
        found = default;
        problem = null;
        if (Guids.TryParse(named, out uint id))
        {
            foreach (PluginLoginCharacter character in roster)
            {
                if (character.ObjectId == id)
                {
                    found = character;
                    return true;
                }
            }
            problem = $"no character on this account has the id {Facts.Hex(id)}; 'characters' lists them";
            return false;
        }

        PluginLoginCharacter[] whole = roster
            .Where(character => character.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        PluginLoginCharacter[] matches = whole.Length > 0
            ? whole
            : roster.Where(character => character.Name.StartsWith(named, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            found = matches[0];
            return true;
        }
        problem = matches.Length == 0
            ? $"no character on this account is named '{named}'; 'characters' lists them"
            : $"more than one character's name starts with '{named}': {string.Join(", ", matches.Select(character => character.Name))}";
        return false;
    }

    private VerbResult Refuse(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.LoginRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
