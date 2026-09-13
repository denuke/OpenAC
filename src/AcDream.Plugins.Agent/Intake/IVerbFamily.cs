namespace AcDream.Plugins.Agent.Intake;

/// <summary>How the dispatcher should report a line a family handled.</summary>
internal readonly record struct VerbResult(string Outcome, string? Reason)
{
    internal static VerbResult Handled { get; } = new("handled", null);

    internal static VerbResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new VerbResult("refused", reason);
    }
}

/// <summary>A closed set of first words and what they do.</summary>
internal interface IVerbFamily
{
    /// <summary>Every first word this family answers. No two families share one.</summary>
    IReadOnlyCollection<string> ReservedWords { get; }

    /// <summary>Handles a line whose first word is reserved here. Called on the update thread.</summary>
    VerbResult Handle(CommandLine line);
}
