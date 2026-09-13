using AcDream.App.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Plugins;

public sealed class AppAutomationSurfaceMovementTests
{
    [Fact]
    public void MoveEnumsMatchTheRuntimeByNameAndValue()
    {
        AssertMatches<RuntimeMoveDirection, PluginMoveDirection>();
        AssertMatches<RuntimeMovePace, PluginMovePace>();
        AssertMatches<RuntimeMoveUnit, PluginMoveUnit>();
        AssertMatches<RuntimeMoveChannel, PluginMoveChannel>();
        AssertMatches<RuntimeScriptedMoveState, PluginMoveState>();
    }

    [Fact]
    public void EachDirectionOccupiesTheSameChannelForPluginsAsInTheRuntime()
    {
        foreach (RuntimeMoveDirection direction in Enum.GetValues<RuntimeMoveDirection>())
        {
            Assert.Equal(
                (int)new RuntimeMoveRequest(direction, RuntimeMovePace.Run, 0f).Channel,
                (int)PluginMoveReport.ChannelOf((PluginMoveDirection)(int)direction));
        }
    }

    [Theory]
    [InlineData(PluginMoveDirection.Forward, 10f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.TurnLeft, 720f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.StrafeRight, 0f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.Forward, 300f, PluginMoveUnit.Seconds, true)]
    [InlineData(PluginMoveDirection.StrafeRight, -1f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Backward, 501f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Forward, 301f, PluginMoveUnit.Seconds, false)]
    [InlineData((PluginMoveDirection)99, 1f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Forward, 1f, (PluginMoveUnit)9, false)]
    public void AMoveIsProjectedOnlyWhenTheRuntimeCanCarryItOut(
        PluginMoveDirection direction,
        float amount,
        PluginMoveUnit unit,
        bool valid)
    {
        RuntimeMoveRequest? request = AppAutomationSurface.ProjectMoveRequest(direction, PluginMovePace.Walk, amount, unit);

        Assert.Equal(valid, request is not null);
        if (request is { } projected)
        {
            Assert.Equal(RuntimeMovePace.Walk, projected.Pace);
            Assert.Equal((int)unit, (int)projected.Unit);
            Assert.Equal(amount, projected.Amount);
        }
    }

    [Fact]
    public void TheMoveReportCarriesEachChannelAndTheJump()
    {
        var snapshot = new RuntimeScriptedMoveSnapshot(
            new RuntimeMoveChannelSnapshot(
                7,
                RuntimeScriptedMoveState.Moving,
                new RuntimeMoveRequest(RuntimeMoveDirection.Backward, RuntimeMovePace.Walk, 20f, RuntimeMoveUnit.Seconds),
                12.5f,
                4f),
            new RuntimeMoveChannelSnapshot(
                8,
                RuntimeScriptedMoveState.Blocked,
                new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 4f),
                1.5f,
                2f),
            new RuntimeMoveChannelSnapshot(
                9,
                RuntimeScriptedMoveState.Completed,
                new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 45f),
                45f,
                0.5f),
            3,
            true);

        Assert.Equal(
            new PluginMoveReport(
                new PluginMoveProgress(7, PluginMoveState.Moving, PluginMoveDirection.Backward, PluginMovePace.Walk, 20f, PluginMoveUnit.Seconds, 12.5f, 4f),
                new PluginMoveProgress(8, PluginMoveState.Blocked, PluginMoveDirection.StrafeLeft, PluginMovePace.Run, 4f, PluginMoveUnit.MetersOrDegrees, 1.5f, 2f),
                new PluginMoveProgress(9, PluginMoveState.Completed, PluginMoveDirection.TurnRight, PluginMovePace.Run, 45f, PluginMoveUnit.MetersOrDegrees, 45f, 0.5f),
                3,
                true),
            AppAutomationSurface.ProjectMoveReport(snapshot));
    }

    private static void AssertMatches<TRuntime, TPlugin>()
        where TRuntime : struct, Enum
        where TPlugin : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TRuntime>(), Enum.GetNames<TPlugin>());
        Assert.Equal(
            Enum.GetValues<TRuntime>().Select(value => Convert.ToInt32(value)),
            Enum.GetValues<TPlugin>().Select(value => Convert.ToInt32(value)));
    }
}
