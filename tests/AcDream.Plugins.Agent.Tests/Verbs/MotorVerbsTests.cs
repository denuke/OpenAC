using System.Globalization;
using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class MotorVerbsTests
{
    [Fact]
    public void WalkAsksTheClientForAWalkAndCompletesWhenItArrives()
    {
        var (host, verbs, correlator, _, ring) = Build();
        FakeNavigation navigation = host.FakeAutomation.FakeNavigation;

        Assert.Equal("handled", verbs.Handle(Line("walk forward 5")).Outcome);
        Assert.Equal([(PluginMoveDirection.Forward, PluginMovePace.Walk, 5f, PluginMoveUnit.MetersOrDegrees)], navigation.Moves);

        correlator.Tick(inWorld: true);
        Assert.Empty(Kinds(ring, RecordKinds.GoalResolved));

        navigation.End(PluginMoveChannel.Travel, PluginMoveState.Completed, covered: 5f, seconds: 2.1f);
        correlator.Tick(inWorld: true);

        JsonElement resolved = Kinds(ring, RecordKinds.GoalResolved).Single();
        Assert.Equal("completed", resolved.GetProperty("outcome").GetString());
        Assert.Equal(5d, resolved.GetProperty("travelled").GetDouble());
        Assert.Equal(2.1d, resolved.GetProperty("seconds").GetDouble());
    }

    [Theory]
    [InlineData("run", PluginMoveDirection.Forward, PluginMovePace.Run, 0f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("run backward 3", PluginMoveDirection.Backward, PluginMovePace.Run, 3f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("walk backwards", PluginMoveDirection.Backward, PluginMovePace.Walk, 0f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("strafe left", PluginMoveDirection.StrafeLeft, PluginMovePace.Run, 0f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("strafe right 2", PluginMoveDirection.StrafeRight, PluginMovePace.Run, 2f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("turn left", PluginMoveDirection.TurnLeft, PluginMovePace.Run, 0f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("turn right 90", PluginMoveDirection.TurnRight, PluginMovePace.Run, 90f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("run forward 20s", PluginMoveDirection.Forward, PluginMovePace.Run, 20f, PluginMoveUnit.Seconds)]
    [InlineData("run 20 seconds", PluginMoveDirection.Forward, PluginMovePace.Run, 20f, PluginMoveUnit.Seconds)]
    [InlineData("walk 12m forward", PluginMoveDirection.Forward, PluginMovePace.Walk, 12f, PluginMoveUnit.MetersOrDegrees)]
    [InlineData("strafe left 1.5 sec", PluginMoveDirection.StrafeLeft, PluginMovePace.Run, 1.5f, PluginMoveUnit.Seconds)]
    [InlineData("turn left 2s", PluginMoveDirection.TurnLeft, PluginMovePace.Run, 2f, PluginMoveUnit.Seconds)]
    [InlineData("turn right 45 degrees", PluginMoveDirection.TurnRight, PluginMovePace.Run, 45f, PluginMoveUnit.MetersOrDegrees)]
    public void EachMoveAsksForItsDirectionPaceAmountAndUnit(
        string text,
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit)
    {
        var (host, verbs, _, _, _) = Build();

        verbs.Handle(Line(text));

        Assert.Equal((direction, pace, amount, unit), Assert.Single(host.FakeAutomation.FakeNavigation.Moves));
    }

    [Theory]
    [InlineData("walk sideways")]
    [InlineData("strafe")]
    [InlineData("strafe forward 3")]
    [InlineData("run forward 0")]
    [InlineData("run forward -3")]
    [InlineData("walk forward 501")]
    [InlineData("walk forward 5 5")]
    [InlineData("run forward 0s")]
    [InlineData("run forward 301s")]
    [InlineData("run 5 degrees")]
    [InlineData("run 20 parsecs")]
    public void AMalformedMoveIsRefusedAndAsksNothing(string text)
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeNavigation.Moves);
        Assert.Single(Kinds(ring, RecordKinds.GoalRefused));
    }

    [Theory]
    [InlineData("turn 90")]
    [InlineData("turn to north")]
    [InlineData("turn to")]
    [InlineData("turn around")]
    [InlineData("turn left 0")]
    [InlineData("turn left 10m")]
    [InlineData("turn left 3601")]
    [InlineData("turn left 90 90")]
    public void AMalformedTurnIsRefused(string text)
    {
        var (host, verbs, _, _, ring) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeNavigation.Moves);
        Assert.Single(Kinds(ring, RecordKinds.GoalRefused));
    }

    [Theory]
    [InlineData(PluginMoveState.Completed, 5f, "completed", "confirmed")]
    [InlineData(PluginMoveState.Stopped, 0f, "completed", "confirmed")]
    [InlineData(PluginMoveState.Stopped, 5f, "cancelled", "withdrawn")]
    [InlineData(PluginMoveState.TimeLimit, 0f, "completed", "confirmed")]
    [InlineData(PluginMoveState.TimeLimit, 5f, "unconfirmed", "unconfirmed")]
    [InlineData(PluginMoveState.Blocked, 5f, "blocked", "unreachable")]
    [InlineData(PluginMoveState.Interrupted, 5f, "cancelled", "withdrawn")]
    [InlineData(PluginMoveState.Lost, 5f, "lost", "lost")]
    public void EachWayAMoveEndsHasItsOutcome(PluginMoveState ending, float amount, string outcome, string outcomeClass)
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line(amount > 0f ? "run forward " + amount.ToString(CultureInfo.InvariantCulture) : "run forward"));

        host.FakeAutomation.FakeNavigation.End(PluginMoveChannel.Travel, ending);
        correlator.Tick(inWorld: true);

        JsonElement resolved = Kinds(ring, RecordKinds.GoalResolved).Single();
        Assert.Equal(outcome, resolved.GetProperty("outcome").GetString());
        Assert.Equal(outcomeClass, resolved.GetProperty("class").GetString());
    }

    [Fact]
    public void MovesOfOtherKindsLeaveTheRunGoing()
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("run forward 60s"));

        verbs.Handle(CommandLine.Parse(10, "turn left 5", "mcp"));
        verbs.Handle(CommandLine.Parse(11, "strafe right 2s", "mcp"));
        correlator.Tick(inWorld: true);

        Assert.Empty(Kinds(ring, RecordKinds.GoalResolved));
        Assert.Equal(3, host.FakeAutomation.FakeNavigation.Moves.Count);
        Assert.Equal(PluginMoveState.Moving, host.FakeAutomation.FakeNavigation.MoveReport.Travel.State);
    }

    [Fact]
    public void ALaterMoveOfTheSameKindReplacesTheEarlierOne()
    {
        var (_, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("walk forward 10"));

        verbs.Handle(CommandLine.Parse(10, "run backward", "mcp"));
        correlator.Tick(inWorld: true);

        JsonElement resolved = Kinds(ring, RecordKinds.GoalResolved).Single();
        Assert.Equal("cancelled", resolved.GetProperty("outcome").GetString());
        Assert.Equal(9, resolved.GetProperty("id").GetInt64());
        Assert.Contains("walk or run", resolved.GetProperty("reason").GetString());
    }

    [Fact]
    public void AMoveTheClientRefusesIsRefused()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeNavigation.MoveStatus = PluginNavigationCommandStatus.Rejected;

        Assert.Equal("refused", verbs.Handle(Line("run forward 5")).Outcome);
        Assert.Single(Kinds(ring, RecordKinds.GoalRefused));
    }

    [Fact]
    public void AMoveForATimeWaitsForItsTimeBeforeGivingUp()
    {
        var (_, verbs, correlator, clock, ring) = Build();
        verbs.Handle(Line("run forward 60s"));

        clock.Advance(60d);
        correlator.Tick(inWorld: true);
        Assert.Empty(Kinds(ring, RecordKinds.GoalResolved));

        clock.Advance(MotorVerbs.MoveWindowSlackSeconds);
        correlator.Tick(inWorld: true);

        Assert.Equal("unconfirmed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(0f, "turn to 90", PluginMoveDirection.TurnRight, 90f)]
    [InlineData(0f, "turn to 270", PluginMoveDirection.TurnLeft, 90f)]
    [InlineData(350f, "turn to 10", PluginMoveDirection.TurnRight, 20f)]
    [InlineData(10f, "turn to -10", PluginMoveDirection.TurnLeft, 20f)]
    public void TurnToTurnsTheShortWayByTheDifference(
        float heading,
        string text,
        PluginMoveDirection direction,
        float degrees)
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading);

        verbs.Handle(Line(text));

        var move = Assert.Single(host.FakeAutomation.FakeNavigation.Moves);
        Assert.Equal(direction, move.Direction);
        Assert.Equal(degrees, move.Amount, 3);
        Assert.Equal(PluginMoveUnit.MetersOrDegrees, move.Unit);
    }

    [Fact]
    public void TurnToTheHeadingAlreadyFacedCompletesAtOnce()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 90f);

        verbs.Handle(Line("turn to 90"));

        Assert.Empty(host.FakeAutomation.FakeNavigation.Moves);
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void FaceTurnsTowardAnObjectsBearing()
    {
        var (host, verbs, _, _, _) = Build();
        host.FakeAutomation.FakeObjects.Add(new PluginWorldObject(
            0x70000001u, 1u, "Drudge", PluginObjectClass.Monster, 16u, 0u, 0u)
        {
            HasPosition = true,
            IsLandscape = true,
            Position = new PluginNavigationPosition(0u, 0.01, 0d, 0d, 0f, true),
        });

        verbs.Handle(Line("face 0x70000001"));

        var move = Assert.Single(host.FakeAutomation.FakeNavigation.Moves);
        Assert.Equal(PluginMoveDirection.TurnRight, move.Direction);
        Assert.Equal(90f, move.Amount, 3);
    }

    [Fact]
    public void ATurnReportsTheDegreesTurned()
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("turn to 45"));

        host.FakeAutomation.FakeNavigation.End(PluginMoveChannel.Turn, PluginMoveState.Completed, covered: 45f);
        correlator.Tick(inWorld: true);

        JsonElement resolved = Kinds(ring, RecordKinds.GoalResolved).Single();
        Assert.Equal("completed", resolved.GetProperty("outcome").GetString());
        Assert.Equal(45d, resolved.GetProperty("turned").GetDouble());
    }

    [Fact]
    public void JumpAsksForHalfAChargeAndCompletesOnceReleasedAndAirborne()
    {
        var (host, verbs, correlator, _, ring) = Build();
        FakeNavigation navigation = host.FakeAutomation.FakeNavigation;

        verbs.Handle(Line("jump"));
        Assert.Equal([MotorVerbs.DefaultJumpPower], navigation.Jumps);

        navigation.Snapshot = Body(heading: 0f) with { IsAirborne = true };
        correlator.Tick(inWorld: true);
        Assert.Empty(Kinds(ring, RecordKinds.GoalResolved));

        navigation.MoveReport = navigation.MoveReport with { JumpCharging = false };
        correlator.Tick(inWorld: true);

        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AJumpWithAPowerAsksForThatPower()
    {
        var (host, verbs, _, _, _) = Build();

        verbs.Handle(Line("jump 0.8"));

        Assert.Equal([0.8f], host.FakeAutomation.FakeNavigation.Jumps);
    }

    [Theory]
    [InlineData("jump 0")]
    [InlineData("jump 2")]
    [InlineData("jump high")]
    public void AJumpWithAPowerOutOfRangeIsRefused(string text)
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);
        Assert.Empty(host.FakeAutomation.FakeNavigation.Jumps);
    }

    [Fact]
    public void StopAsksTheClientToStopEveryMoveAndCompletes()
    {
        var (host, verbs, _, _, ring) = Build();

        verbs.Handle(Line("stop"));

        Assert.Null(Assert.Single(host.FakeAutomation.FakeNavigation.Stops));
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("stop turning", PluginMoveChannel.Turn)]
    [InlineData("stop strafing", PluginMoveChannel.Strafe)]
    [InlineData("stop running", PluginMoveChannel.Travel)]
    [InlineData("stop walk", PluginMoveChannel.Travel)]
    public void StopWithAKindOfMoveStopsOnlyThatKind(string text, PluginMoveChannel channel)
    {
        var (host, verbs, _, _, _) = Build();

        verbs.Handle(Line(text));

        Assert.Equal((PluginMoveChannel?)channel, Assert.Single(host.FakeAutomation.FakeNavigation.Stops));
    }

    [Fact]
    public void StopWithAnUnknownWordIsRefused()
    {
        var (host, verbs, _, _, _) = Build();

        Assert.Equal("refused", verbs.Handle(Line("stop dancing")).Outcome);
        Assert.Empty(host.FakeAutomation.FakeNavigation.Stops);
    }

    [Fact]
    public void StopEndsAMoveWithoutAnAmountAsCompleted()
    {
        var (_, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("run forward"));

        verbs.Handle(CommandLine.Parse(10, "stop", "mcp"));
        correlator.Tick(inWorld: true);

        Assert.Equal(
            ["completed", "completed"],
            Kinds(ring, RecordKinds.GoalResolved).Select(record => record.GetProperty("outcome").GetString()));
    }

    [Fact]
    public void StoppingTheTurnLeavesTheRunGoing()
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("run forward"));
        verbs.Handle(CommandLine.Parse(10, "turn left", "mcp"));

        verbs.Handle(CommandLine.Parse(11, "stop turning", "mcp"));
        correlator.Tick(inWorld: true);

        Assert.Equal(
            [10L, 11L],
            Kinds(ring, RecordKinds.GoalResolved).Select(record => record.GetProperty("id").GetInt64()).Order());
        Assert.Equal(PluginMoveState.Moving, host.FakeAutomation.FakeNavigation.MoveReport.Travel.State);
    }

    [Fact]
    public void StanceEntersTheModeAndResolvesWhenTheModeIsReported()
    {
        var (host, verbs, correlator, _, ring) = Build();

        verbs.Handle(Line("stance magic"));
        host.FakeAutomation.FakeCombat.Snapshot = host.FakeAutomation.FakeCombat.Snapshot with
        {
            Mode = PluginCombatMode.Magic,
        };
        correlator.Tick(inWorld: true);

        Assert.Equal(["mode:Magic"], host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal("completed", Kinds(ring, RecordKinds.GoalResolved).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void AStanceTheClientRefusesIsRefusedWithItsStatus()
    {
        var (host, verbs, _, _, ring) = Build();
        host.FakeAutomation.FakeCombat.NextStatus = PluginCombatCommandStatus.Busy;

        Assert.Equal("refused", verbs.Handle(Line("stance melee")).Outcome);
        Assert.Contains("busy", Kinds(ring, RecordKinds.GoalRefused).Single().GetProperty("reason").GetString());
    }

    [Fact]
    public void CancelStopsMovementAttacksAndPendingGoals()
    {
        var (host, verbs, correlator, _, ring) = Build();
        verbs.Handle(Line("run forward"));

        verbs.Handle(CommandLine.Parse(10, "cancel", "mcp"));

        JsonElement[] resolved = Kinds(ring, RecordKinds.GoalResolved).ToArray();
        Assert.Equal(["cancelled", "completed"], resolved.Select(r => r.GetProperty("outcome").GetString()));
        Assert.Null(Assert.Single(host.FakeAutomation.FakeNavigation.Stops));
        Assert.Equal(1, host.FakeAutomation.FakeNavigation.Clears);
        Assert.Contains("abort", host.FakeAutomation.FakeCombat.Calls);
        Assert.Equal(0, correlator.PendingCount);
    }

    private static PluginNavigationSnapshot Body(float heading) =>
        new(true, false, 0x50000001u, new PluginNavigationPosition(0u, 0d, 0d, 0d, heading, true), false, false);

    private static CommandLine Line(string text) => CommandLine.Parse(9, text, "mcp");

    private static IEnumerable<JsonElement> Kinds(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);

    private static (FakePluginHost Host, MotorVerbs Verbs, OutcomeCorrelator Correlator, AgentClock Clock, RecordRing Ring) Build()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = Body(heading: 0f);
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new MotorVerbs(host, publisher, correlator), correlator, clock, ring);
    }
}
