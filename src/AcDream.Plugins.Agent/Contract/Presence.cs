namespace AcDream.Plugins.Agent.Contract;

/// <summary>Whether the client holds a fact, and if not, why not.</summary>
internal enum Presence
{
    /// <summary>The client has not received this fact.</summary>
    Unknown,

    /// <summary>The client holds this fact.</summary>
    Observed,

    /// <summary>This host cannot provide this fact at all.</summary>
    Unsupported,
}
