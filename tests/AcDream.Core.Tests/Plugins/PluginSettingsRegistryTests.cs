using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginSettingsRegistryTests
{
    [Fact]
    public void RegisteredSettingsAreDescribedReadAndChangedUntilTheRegistrationEnds()
    {
        var registry = new PluginSettingsRegistry();
        var provider = new Provider();
        IDisposable registration = registry.Register("acdream.mosstank/settings", "MossTank", provider);

        Assert.Equal(new PluginSettingsInfo("acdream.mosstank/settings", "MossTank"), Assert.Single(registry.Available));
        Assert.True(registry.TryDescribe("ACDREAM.MOSSTANK/SETTINGS", out string description));
        Assert.Equal("how to change", description);
        Assert.True(registry.TryRead("acdream.mosstank/settings", " options ", out string? json));
        Assert.Equal("options", provider.Sections.Single());
        Assert.Equal("{\"options\":{}}", json);
        Assert.True(registry.TryRead("acdream.mosstank/settings", "  ", out _));
        Assert.Null(provider.Sections[1]);
        Assert.True(registry.TryChange("acdream.mosstank/settings", "{\"options\":{}}", out PluginSettingsChangeResult result));
        Assert.True(result.Applied);
        Assert.Equal("{\"options\":{}}", Assert.Single(provider.Changes));
        Assert.Throws<InvalidOperationException>(() => registry.Register("acdream.mosstank/settings", "Other", new Provider()));

        registration.Dispose();
        registration.Dispose();

        Assert.Empty(registry.Available);
        Assert.False(registry.TryDescribe("acdream.mosstank/settings", out _));
        Assert.False(registry.TryRead("acdream.mosstank/settings", null, out _));
        Assert.False(registry.TryChange("acdream.mosstank/settings", "{}", out _));
    }

    [Fact]
    public void AHostWithoutSharedSettingsTakesARegistrationAndSharesNothing()
    {
        IPluginSettingsRegistry none = NoOpPluginSettingsRegistry.Instance;

        using IDisposable registration = none.Register("settings", "MossTank", new Provider());

        Assert.Empty(none.Available);
        Assert.False(none.TryRead("settings", null, out _));
        Assert.False(none.TryChange("settings", "{}", out _));
    }

    private sealed class Provider : IPluginSettingsProvider
    {
        public List<string?> Sections { get; } = [];

        public List<string> Changes { get; } = [];

        public string Describe() => "how to change";

        public string? Read(string? section)
        {
            Sections.Add(section);
            return section is null ? "{}" : $"{{\"{section}\":{{}}}}";
        }

        public PluginSettingsChangeResult Change(string changeJson)
        {
            Changes.Add(changeJson);
            return new PluginSettingsChangeResult(true, "Changed.");
        }
    }
}
