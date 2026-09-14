using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>settings [plugin] [section]</c> reads the settings other plugins share, such
/// as MossTank's, and <c>configure &lt;plugin&gt; &lt;change&gt;</c> changes them with
/// one JSON object the plugin describes. Neither sends anything to the server.
/// </summary>
internal sealed class PluginSettingsVerbs : IVerbFamily
{
    private const string ConfigureUsage =
        "usage: configure <plugin> <change as one JSON object>, such as configure mosstank {\"options\":{\"EnableCombat\":true}}";

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;

    internal PluginSettingsVerbs(IPluginHost host, Publisher publisher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        _host = host;
        _publisher = publisher;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["settings", "configure"];

    public VerbResult Handle(CommandLine line) =>
        line.Verb == "configure" ? Configure(line) : Read(line);

    private VerbResult Read(CommandLine line)
    {
        IPluginSettingsRegistry registry = _host.SharedSettings;
        string[] words = line.Arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            var plugins = new JsonArray();
            foreach (PluginSettingsInfo shared in registry.Available)
                plugins.Add(new JsonObject { ["plugin"] = shared.Id, ["name"] = shared.DisplayName });
            _publisher.Publish(RecordKinds.PluginSettings, new JsonObject
            {
                ["id"] = line.Id,
                ["verb"] = line.Verb,
                ["plugins"] = plugins,
            });
            return VerbResult.Handled;
        }
        if (words.Length > 2)
            return Refuse(line, "usage: settings [plugin] [section]");
        if (Resolve(registry, words[0]) is not { } info)
            return Refuse(line, NoSuchPlugin(registry, words[0]));
        string? section = words.Length == 2 ? words[1] : null;
        if (!registry.TryRead(info.Id, section, out string? json))
            return Refuse(line, NoSuchPlugin(registry, words[0]));
        if (json is null)
            return Refuse(line, $"{info.DisplayName} shares no settings section named '{section}'");
        registry.TryDescribe(info.Id, out string description);
        _publisher.Publish(RecordKinds.PluginSettings, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["plugin"] = info.Id,
            ["name"] = info.DisplayName,
            ["section"] = section,
            ["howToChange"] = string.IsNullOrWhiteSpace(description) ? null : description,
            ["settings"] = Parsed(json),
        });
        return VerbResult.Handled;
    }

    private VerbResult Configure(CommandLine line)
    {
        string arguments = line.Arguments;
        int space = arguments.IndexOfAny([' ', '\t']);
        if (space < 0)
            return Refuse(line, ConfigureUsage);
        string word = arguments[..space];
        IPluginSettingsRegistry registry = _host.SharedSettings;
        if (Resolve(registry, word) is not { } info)
            return Refuse(line, NoSuchPlugin(registry, word));
        JsonObject? change;
        try
        {
            change = JsonNode.Parse(arguments[(space + 1)..]) as JsonObject;
        }
        catch (JsonException)
        {
            change = null;
        }
        if (change is null)
            return Refuse(line, ConfigureUsage);
        if (!registry.TryChange(info.Id, change.ToJsonString(), out PluginSettingsChangeResult result))
            return Refuse(line, NoSuchPlugin(registry, word));
        if (!result.Applied)
        {
            return Refuse(
                line,
                string.IsNullOrWhiteSpace(result.Message) ? $"{info.DisplayName} refused the change" : result.Message,
                info);
        }
        _publisher.Publish(RecordKinds.SettingsChanged, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["plugin"] = info.Id,
            ["name"] = info.DisplayName,
            ["message"] = result.Message,
            ["settings"] = result.SettingsJson is { } json ? Parsed(json) : null,
        });
        return VerbResult.Handled;
    }

    /// <summary>
    /// The shared settings a word names: their id, the name shown for them, or the
    /// plugin they belong to, such as acdream.mosstank or mosstank. Null when the
    /// word names none, or more than one.
    /// </summary>
    internal static PluginSettingsInfo? Resolve(IPluginSettingsRegistry registry, string word)
    {
        PluginSettingsInfo[] available = [.. registry.Available];
        Func<PluginSettingsInfo, bool>[] matches =
        [
            info => info.Id.Equals(word, StringComparison.OrdinalIgnoreCase),
            info => info.DisplayName.Equals(word, StringComparison.OrdinalIgnoreCase),
            info => PluginOf(info.Id).Equals(word, StringComparison.OrdinalIgnoreCase)
                || PluginOf(info.Id).EndsWith("." + word, StringComparison.OrdinalIgnoreCase),
        ];
        foreach (Func<PluginSettingsInfo, bool> match in matches)
        {
            PluginSettingsInfo[] found = available.Where(match).ToArray();
            if (found.Length == 1)
                return found[0];
            if (found.Length > 1)
                return null;
        }
        return null;
    }

    private static string PluginOf(string id)
    {
        int slash = id.IndexOf('/');
        return slash < 0 ? id : id[..slash];
    }

    private static string NoSuchPlugin(IPluginSettingsRegistry registry, string word)
    {
        IReadOnlyList<PluginSettingsInfo> available = registry.Available;
        return available.Count == 0
            ? "no plugin shares its settings"
            : $"'{word}' names no one plugin's settings; shared: "
                + string.Join(", ", available.Select(static info => $"{info.DisplayName} ({info.Id})"));
    }

    private static JsonNode? Parsed(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private VerbResult Refuse(CommandLine line, string reason, PluginSettingsInfo? info = null)
    {
        _publisher.Publish(RecordKinds.SettingsRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["plugin"] = info?.Id,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
