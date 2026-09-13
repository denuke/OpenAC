namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginLoginCharacter(
    uint ObjectId,
    string Name,
    int ActiveIndex,
    bool IsPendingDelete);

/// <summary>Where the client stands between reaching a server and playing a character.</summary>
public enum PluginLoginStage
{
    /// <summary>No server session, or the host cannot tell.</summary>
    None = 0,

    /// <summary>Connecting to the server, before its character list arrives.</summary>
    Connecting,

    /// <summary>The character list is up, and a character can be chosen.</summary>
    ChoosingCharacter,

    EnteringWorld,

    InWorld,
}

/// <summary>
/// The client's login state. <paramref name="ChosenObjectId"/> is the character
/// highlighted on the character list, or zero. <paramref name="Error"/> is the
/// client's message for the last refusal at the character list, if there is one.
/// </summary>
public readonly record struct PluginLoginSnapshot(
    PluginLoginStage Stage,
    string AccountName,
    string WorldName,
    uint ChosenObjectId,
    string? Error);

public enum PluginLoginCommandStatus
{
    Unavailable = 0,
    Accepted,
    Rejected,
}

public interface ILoginAutomation
{
    bool IsAvailable => false;
    uint NextLoginObjectId => 0u;

    IReadOnlyList<PluginLoginCharacter> CaptureRoster() =>
        Array.Empty<PluginLoginCharacter>();

    bool SetNextLogin(uint characterObjectId) => false;
    bool ClearNextLogin() => false;

    /// <summary>Where the client stands in logging a character in.</summary>
    PluginLoginSnapshot Snapshot =>
        new(PluginLoginStage.None, string.Empty, string.Empty, 0u, null);

    /// <summary>
    /// Enters the world as a character on the character list, as choosing it and
    /// pressing enter does. Accepted means the client sent the request; the
    /// character is in the world once the automation surface says so.
    /// </summary>
    PluginLoginCommandStatus EnterWorld(uint characterObjectId) =>
        PluginLoginCommandStatus.Unavailable;

    /// <summary>
    /// Logs the character in the world out to the character list, as the client's
    /// own log out does, which is refused while the character is in mid-air.
    /// Accepted means the client sent the request; the character list is back once
    /// <see cref="Snapshot"/> says so.
    /// </summary>
    PluginLoginCommandStatus LogOut() =>
        PluginLoginCommandStatus.Unavailable;
}
