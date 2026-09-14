using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>Process-local registry of the settings plugins share with each other.</summary>
public sealed class PluginSettingsRegistry : IPluginSettingsRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PluginSettingsInfo> Available
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Select(static entry => entry.Info)
                    .OrderBy(static info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static info => info.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public IDisposable Register(
        string settingsId,
        string displayName,
        IPluginSettingsProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(provider);
        string id = settingsId.Trim();
        var entry = new Entry(new PluginSettingsInfo(id, displayName.Trim()), provider);
        lock (_gate)
        {
            if (!_entries.TryAdd(id, entry))
                throw new InvalidOperationException($"Plugin settings '{id}' are already registered.");
        }
        return new Registration(this, id, entry);
    }

    public bool TryDescribe(string settingsId, out string description)
    {
        if (!TryGet(settingsId, out IPluginSettingsProvider provider))
        {
            description = string.Empty;
            return false;
        }
        description = provider.Describe();
        return true;
    }

    public bool TryRead(string settingsId, string? section, out string? json)
    {
        if (!TryGet(settingsId, out IPluginSettingsProvider provider))
        {
            json = null;
            return false;
        }
        json = provider.Read(string.IsNullOrWhiteSpace(section) ? null : section.Trim());
        return true;
    }

    public bool TryChange(
        string settingsId,
        string changeJson,
        out PluginSettingsChangeResult result)
    {
        ArgumentNullException.ThrowIfNull(changeJson);
        if (!TryGet(settingsId, out IPluginSettingsProvider provider))
        {
            result = default;
            return false;
        }
        result = provider.Change(changeJson);
        return true;
    }

    private bool TryGet(string settingsId, out IPluginSettingsProvider provider)
    {
        provider = null!;
        if (string.IsNullOrWhiteSpace(settingsId))
            return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(settingsId.Trim(), out Entry? entry))
                return false;
            provider = entry.Provider;
            return true;
        }
    }

    private void Remove(string id, Entry expected)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out Entry? current) && ReferenceEquals(current, expected))
                _entries.Remove(id);
        }
    }

    private sealed record Entry(PluginSettingsInfo Info, IPluginSettingsProvider Provider);

    private sealed class Registration(
        PluginSettingsRegistry owner,
        string id,
        Entry entry) : IDisposable
    {
        private PluginSettingsRegistry? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(id, entry);
    }
}
