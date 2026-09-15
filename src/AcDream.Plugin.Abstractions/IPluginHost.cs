// src/AcDream.Plugin.Abstractions/IPluginHost.cs
namespace AcDream.Plugin.Abstractions;

public interface IPluginHost
{
    bool HasUi { get; }

    IPluginLogger Log { get; }
    IGameState State { get; }
    IEvents Events { get; }
    ISelectionService Selection { get; }
    IUiRegistry Ui { get; }
    IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;
    /// <summary>
    /// Durable storage scoped by the host to this plugin's manifest id.
    /// No-window/test hosts may explicitly expose the inert implementation.
    /// </summary>
    IPluginStorage Storage => NoOpPluginStorage.Instance;
    IPluginLootClassifierRegistry LootClassifiers =>
        NoOpPluginLootClassifierRegistry.Instance;

    /// <summary>The settings plugins share with each other, such as a combat macro's with an agent.</summary>
    IPluginSettingsRegistry SharedSettings =>
        NoOpPluginSettingsRegistry.Instance;

    /// <summary>Notices plugins post for one another, such as a combat macro's warnings for an agent.</summary>
    IPluginNoticeBoard Notices => NoOpPluginNoticeBoard.Instance;

    IAutomationSurface Automation { get; }

    IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;

    IReadOnlyDictionary<string, string> SessionSettings =>
        EmptySessionSettings;

    private static readonly IReadOnlyDictionary<string, string> EmptySessionSettings =
        new Dictionary<string, string>();
}
