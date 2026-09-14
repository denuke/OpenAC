using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakeSettingsRegistry : IPluginSettingsRegistry
{
    private readonly List<(PluginSettingsInfo Info, IPluginSettingsProvider Provider)> _entries = [];

    public IReadOnlyList<PluginSettingsInfo> Available => _entries.Select(static entry => entry.Info).ToArray();

    public IDisposable Register(string settingsId, string displayName, IPluginSettingsProvider provider)
    {
        var entry = (new PluginSettingsInfo(settingsId, displayName), provider);
        _entries.Add(entry);
        return new Lease(() => _entries.Remove(entry));
    }

    public bool TryDescribe(string settingsId, out string description)
    {
        description = Find(settingsId)?.Describe() ?? string.Empty;
        return Find(settingsId) is not null;
    }

    public bool TryRead(string settingsId, string? section, out string? json)
    {
        IPluginSettingsProvider? provider = Find(settingsId);
        json = provider?.Read(section);
        return provider is not null;
    }

    public bool TryChange(string settingsId, string changeJson, out PluginSettingsChangeResult result)
    {
        IPluginSettingsProvider? provider = Find(settingsId);
        result = provider?.Change(changeJson) ?? default;
        return provider is not null;
    }

    private IPluginSettingsProvider? Find(string settingsId) =>
        _entries.FirstOrDefault(entry => entry.Info.Id.Equals(settingsId, StringComparison.OrdinalIgnoreCase)).Provider;

    private sealed class Lease(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}

/// <summary>A plugin's shared settings: one options section, and a set answer to every change.</summary>
internal sealed class FakeSettingsProvider : IPluginSettingsProvider
{
    internal JsonObject Settings { get; } = new()
    {
        ["options"] = new JsonObject { ["EnableCombat"] = true },
        ["monsters"] = new JsonArray(new JsonObject { ["name"] = "DEFAULT" }),
    };

    internal List<string> Changes { get; } = [];

    internal PluginSettingsChangeResult Answer { get; set; } =
        new(true, "Set 1 option.", """{"options":{"EnableCombat":false}}""");

    public string Describe() => "Change options with {\"options\":{\"<option>\":<value>}}.";

    public string? Read(string? section) =>
        section is null
            ? Settings.ToJsonString()
            : Settings[section] is { } part
                ? new JsonObject { [section] = part.DeepClone() }.ToJsonString()
                : null;

    public PluginSettingsChangeResult Change(string changeJson)
    {
        Changes.Add(changeJson);
        return Answer;
    }
}
