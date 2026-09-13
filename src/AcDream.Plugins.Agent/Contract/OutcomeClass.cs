namespace AcDream.Plugins.Agent.Contract;

/// <summary>
/// What a terminal outcome word means, shared across verb families so a
/// consumer can switch on one vocabulary while each family keeps its own word.
/// </summary>
internal enum OutcomeClass
{
    /// <summary>The server confirmed the intent.</summary>
    Confirmed,

    /// <summary>The server answered and the client did part of it.</summary>
    Partial,

    /// <summary>The server, or the client before sending, said no.</summary>
    Refused,

    /// <summary>The client ended it before or instead of sending.</summary>
    Withdrawn,

    /// <summary>The window closed with no answer.</summary>
    Unconfirmed,

    /// <summary>An answer arrived that could not be joined to this request.</summary>
    Unattributable,

    /// <summary>The goal cannot be pursued from here.</summary>
    Unreachable,

    /// <summary>The session, the subject or the object went away.</summary>
    Lost,

    /// <summary>Not a terminal: still waiting.</summary>
    InFlight,
}

internal static class OutcomeClasses
{
    internal static string WireName(OutcomeClass outcome) => outcome switch
    {
        OutcomeClass.Confirmed => "confirmed",
        OutcomeClass.Partial => "partial",
        OutcomeClass.Refused => "refused",
        OutcomeClass.Withdrawn => "withdrawn",
        OutcomeClass.Unconfirmed => "unconfirmed",
        OutcomeClass.Unattributable => "unattributable",
        OutcomeClass.Unreachable => "unreachable",
        OutcomeClass.Lost => "lost",
        OutcomeClass.InFlight => "in-flight",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
