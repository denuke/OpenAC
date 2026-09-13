using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>The kind words <c>nearby</c> filters by: the host's own object classes.</summary>
internal static class EntityKinds
{
    internal const string Unknown = "unknown";

    internal static IReadOnlyList<string> Words { get; } =
        Enum.GetValues<PluginObjectClass>()
            .Where(objectClass => objectClass != PluginObjectClass.Unknown)
            .Select(objectClass => WireNames.Kebab(objectClass.ToString()))
            .Append(Unknown)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string WordFor(PluginObjectClass objectClass) =>
        objectClass == PluginObjectClass.Unknown
            ? Unknown
            : WireNames.Kebab(objectClass.ToString());

    internal static bool IsWord(string word) =>
        Words.Contains(word, StringComparer.OrdinalIgnoreCase);
}
