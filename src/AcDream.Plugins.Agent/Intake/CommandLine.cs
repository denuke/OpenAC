namespace AcDream.Plugins.Agent.Intake;

/// <summary>One delivered command line, split into its first word and the rest.</summary>
internal readonly record struct CommandLine(
    long Id,
    string Text,
    string Verb,
    string Arguments,
    string Source)
{
    internal static CommandLine Parse(long id, string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        string verb = space < 0 ? trimmed : trimmed[..space];
        string arguments = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        return new CommandLine(id, trimmed, verb.ToLowerInvariant(), arguments, source);
    }
}
