namespace AcDream.Plugin.Abstractions;

/// <summary>Settings a plugin shares, under the id the host scoped them to, and the name shown for them.</summary>
public readonly record struct PluginSettingsInfo(string Id, string DisplayName);

/// <summary>
/// The answer to a change of a plugin's shared settings: whether the plugin
/// applied it, what it said, and the settings the change touched as the plugin
/// now holds them, as a JSON object.
/// </summary>
public readonly record struct PluginSettingsChangeResult(
    bool Applied,
    string Message,
    string? SettingsJson = null);

/// <summary>
/// Settings a plugin shares with other plugins, such as an agent. They are read
/// and changed as JSON objects, on the update thread.
/// </summary>
public interface IPluginSettingsProvider
{
    /// <summary>How the settings are laid out and how to change them, for a person or a model to read.</summary>
    string Describe();

    /// <summary>
    /// The settings as a JSON object, or only <paramref name="section"/> of them
    /// when it is given; null when there is no such section.
    /// </summary>
    string? Read(string? section);

    /// <summary>
    /// Applies a change given as a JSON object and saves it the way the plugin's
    /// own editors do. A change the plugin refuses in any part changes nothing.
    /// </summary>
    PluginSettingsChangeResult Change(string changeJson);
}

/// <summary>
/// The settings plugins share with each other. A plugin registers its own, and
/// any plugin reads and changes what is registered. Calls are made on the
/// update thread.
/// </summary>
public interface IPluginSettingsRegistry
{
    IReadOnlyList<PluginSettingsInfo> Available =>
        Array.Empty<PluginSettingsInfo>();

    /// <summary>
    /// Shares a plugin's settings until the result is disposed. A host without
    /// shared settings takes the registration and shares nothing.
    /// </summary>
    IDisposable Register(
        string settingsId,
        string displayName,
        IPluginSettingsProvider provider) =>
        NoSharedSettings.Instance;

    bool TryDescribe(string settingsId, out string description)
    {
        description = string.Empty;
        return false;
    }

    /// <summary>False when nothing is registered under <paramref name="settingsId"/>.</summary>
    bool TryRead(string settingsId, string? section, out string? json)
    {
        json = null;
        return false;
    }

    /// <summary>False when nothing is registered under <paramref name="settingsId"/>.</summary>
    bool TryChange(
        string settingsId,
        string changeJson,
        out PluginSettingsChangeResult result)
    {
        result = default;
        return false;
    }
}

public sealed class NoOpPluginSettingsRegistry : IPluginSettingsRegistry
{
    public static NoOpPluginSettingsRegistry Instance { get; } = new();

    private NoOpPluginSettingsRegistry()
    {
    }
}

file sealed class NoSharedSettings : IDisposable
{
    public static NoSharedSettings Instance { get; } = new();

    public void Dispose()
    {
    }
}
