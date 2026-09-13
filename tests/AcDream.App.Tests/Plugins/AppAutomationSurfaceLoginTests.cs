using AcDream.App.Plugins;
using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Plugins;

public sealed class AppAutomationSurfaceLoginTests
{
    private const uint Character = 0x50000002u;
    private static readonly RuntimeGenerationToken Generation = new(7);

    [Theory]
    [InlineData(RuntimeCharacterSelectionLifecycle.Inactive, PluginLoginStage.None)]
    [InlineData(RuntimeCharacterSelectionLifecycle.Connecting, PluginLoginStage.Connecting)]
    [InlineData(RuntimeCharacterSelectionLifecycle.AwaitingSelection, PluginLoginStage.ChoosingCharacter)]
    [InlineData(RuntimeCharacterSelectionLifecycle.EnteringWorld, PluginLoginStage.EnteringWorld)]
    [InlineData(RuntimeCharacterSelectionLifecycle.InWorld, PluginLoginStage.InWorld)]
    public void EachCharacterListStageReachesPlugins(
        RuntimeCharacterSelectionLifecycle lifecycle,
        PluginLoginStage stage)
    {
        Assert.Equal(stage, AppAutomationSurface.ProjectLoginSnapshot(Selection(lifecycle)).Stage);
    }

    [Fact]
    public void TheSnapshotCarriesTheWorldTheChosenCharacterAndTheClientsMessage()
    {
        var error = new RuntimeCharacterSelectionError(
            0x0Du,
            CharacterError.Code.EnterGameCharacterInWorld,
            "One of this account's characters is still in the world. Please try again shortly.");

        PluginLoginSnapshot snapshot = AppAutomationSurface.ProjectLoginSnapshot(
            Selection(RuntimeCharacterSelectionLifecycle.AwaitingSelection, error));

        Assert.Equal("account", snapshot.AccountName);
        Assert.Equal("Testworld", snapshot.WorldName);
        Assert.Equal(Character, snapshot.ChosenObjectId);
        Assert.Equal(error.Message, snapshot.Error);
    }

    [Fact]
    public void EnteringTheWorldHighlightsTheCharacterThenEnters()
    {
        var commands = new RecordingSelectionCommands();

        PluginLoginCommandStatus status = AppAutomationSurface.EnterWorldWith(commands, Generation, Character);

        Assert.Equal(PluginLoginCommandStatus.Accepted, status);
        Assert.Equal([("highlight", Character, Generation), ("enter", 0u, Generation)], commands.Calls);
    }

    [Theory]
    [InlineData(RuntimeCommandStatus.Rejected, PluginLoginCommandStatus.Rejected)]
    [InlineData(RuntimeCommandStatus.Inactive, PluginLoginCommandStatus.Unavailable)]
    [InlineData(RuntimeCommandStatus.StaleGeneration, PluginLoginCommandStatus.Unavailable)]
    public void ACharacterTheListWillNotHighlightIsNeverEntered(
        RuntimeCommandStatus highlighted,
        PluginLoginCommandStatus expected)
    {
        var commands = new RecordingSelectionCommands { HighlightStatus = highlighted };

        Assert.Equal(expected, AppAutomationSurface.EnterWorldWith(commands, Generation, Character));
        Assert.DoesNotContain(commands.Calls, call => call.Name == "enter");
    }

    [Theory]
    [InlineData(RuntimeCommandStatus.Rejected, PluginLoginCommandStatus.Rejected)]
    [InlineData(RuntimeCommandStatus.Inactive, PluginLoginCommandStatus.Unavailable)]
    [InlineData(RuntimeCommandStatus.Unsupported, PluginLoginCommandStatus.Unavailable)]
    public void AnEnterTheSessionRefusesIsReported(
        RuntimeCommandStatus entered,
        PluginLoginCommandStatus expected)
    {
        var commands = new RecordingSelectionCommands { EnterStatus = entered };

        Assert.Equal(expected, AppAutomationSurface.EnterWorldWith(commands, Generation, Character));
    }

    [Fact]
    public void NoCharacterIsRejectedWithoutAsking()
    {
        var commands = new RecordingSelectionCommands();

        Assert.Equal(PluginLoginCommandStatus.Rejected, AppAutomationSurface.EnterWorldWith(commands, Generation, 0u));
        Assert.Empty(commands.Calls);
    }

    private static RuntimeCharacterSelectionSnapshot Selection(
        RuntimeCharacterSelectionLifecycle lifecycle,
        RuntimeCharacterSelectionError? error = null) =>
        new(
            Generation,
            lifecycle,
            Revision: 1,
            AccountName: "account",
            SlotCount: 11,
            RosterCount: 2,
            WorldName: "Testworld",
            HighlightedCharacterId: Character,
            HighlightedDisplayIndex: 1,
            PendingDeleteCharacterId: 0u,
            LastRestoreRequestedCharacterId: 0u,
            RuntimeCharacterSelectionOperation.None,
            error,
            RuntimeCharacterSelectionButtons.None);

    private sealed class RecordingSelectionCommands : IRuntimeCharacterSelectionCommands
    {
        internal List<(string Name, uint CharacterId, RuntimeGenerationToken Generation)> Calls { get; } = [];

        internal RuntimeCommandStatus HighlightStatus { get; init; } = RuntimeCommandStatus.Accepted;

        internal RuntimeCommandStatus EnterStatus { get; init; } = RuntimeCommandStatus.Accepted;

        public RuntimeCommandResult Highlight(RuntimeGenerationToken expectedGeneration, uint characterId)
        {
            Calls.Add(("highlight", characterId, expectedGeneration));
            return new RuntimeCommandResult(HighlightStatus, expectedGeneration, characterId);
        }

        public RuntimeCommandResult Enter(RuntimeGenerationToken expectedGeneration)
        {
            Calls.Add(("enter", 0u, expectedGeneration));
            return new RuntimeCommandResult(EnterStatus, expectedGeneration);
        }

        public RuntimeCommandResult RequestDelete(RuntimeGenerationToken expectedGeneration) => Unexpected();

        public RuntimeCommandResult ConfirmDelete(RuntimeGenerationToken expectedGeneration) => Unexpected();

        public RuntimeCommandResult Restore(RuntimeGenerationToken expectedGeneration) => Unexpected();

        public RuntimeCommandResult Cancel(RuntimeGenerationToken expectedGeneration) => Unexpected();

        private static RuntimeCommandResult Unexpected() =>
            throw new InvalidOperationException("entering the world never deletes, restores or cancels");
    }
}
