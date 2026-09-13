using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Tests.Intake;

public sealed class OutcomeCorrelatorTests
{
    [Fact]
    public void AnActionResolvesOnceWhenItsProbeAnswers()
    {
        var (clock, correlator, ring) = Build();
        bool done = false;
        correlator.Watch(4, "turn", RecordKinds.GoalResolved, 5d, () => done ? new Resolution("completed") : null);

        correlator.Tick(inWorld: true);
        done = true;
        correlator.Tick(inWorld: true);
        correlator.Tick(inWorld: true);

        JsonElement outcome = Only(ring);
        Assert.Equal(4, outcome.GetProperty("id").GetInt64());
        Assert.Equal("completed", outcome.GetProperty("outcome").GetString());
        Assert.Equal("confirmed", outcome.GetProperty("class").GetString());
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void NoAnswerWithinTheWindowIsUnconfirmedNeverSuccess()
    {
        var (clock, correlator, ring) = Build();
        correlator.Watch(1, "jump", RecordKinds.GoalResolved, 2d, () => null);

        clock.Advance(1.9);
        correlator.Tick(inWorld: true);
        Assert.Equal(0, ring.Count);
        clock.Advance(0.1);
        correlator.Tick(inWorld: true);

        JsonElement outcome = Only(ring);
        Assert.Equal("unconfirmed", outcome.GetProperty("outcome").GetString());
        Assert.Equal("unconfirmed", outcome.GetProperty("class").GetString());
    }

    [Fact]
    public void LeavingTheWorldLosesEveryPendingAction()
    {
        var (_, correlator, ring) = Build();
        correlator.Watch(1, "turn", RecordKinds.GoalResolved, 5d, () => new Resolution("completed"));

        correlator.Tick(inWorld: false);

        Assert.Equal("lost", Only(ring).GetProperty("outcome").GetString());
    }

    [Fact]
    public void AnActionCarriedOutOfTheWorldIsProbedThereInsteadOfLost()
    {
        var (_, correlator, ring) = Build();
        bool entered = false;
        correlator.Watch(1, "login", RecordKinds.LoginOutcome, 60d, () => entered ? new Resolution("completed") : null, outOfWorld: true);

        correlator.Tick(inWorld: false);
        Assert.Equal(0, ring.Count);
        entered = true;
        correlator.Tick(inWorld: false);

        Assert.Equal("completed", Only(ring).GetProperty("outcome").GetString());
    }

    [Fact]
    public void AProbeThatThrowsEndsTheActionUnconfirmedAndIsCounted()
    {
        var (_, correlator, ring) = Build();
        correlator.Watch(1, "turn", RecordKinds.GoalResolved, 5d,
            () => throw new InvalidOperationException("probe failed"));

        correlator.Tick(inWorld: true);

        Assert.Equal(1, correlator.ProbeFailures);
        Assert.Equal("unconfirmed", Only(ring).GetProperty("outcome").GetString());
    }

    [Fact]
    public void EndAllEndsOnlyThatKind()
    {
        var (_, correlator, ring) = Build();
        correlator.Watch(1, "turn", RecordKinds.GoalResolved, 5d, () => null);
        correlator.Watch(2, "target", RecordKinds.TargetOutcome, 5d, () => null);

        int ended = correlator.EndAll(RecordKinds.GoalResolved, "cancelled", "stopped");

        Assert.Equal(1, ended);
        Assert.Equal(1, correlator.PendingCount);
        Assert.Equal("cancelled", Only(ring).GetProperty("outcome").GetString());
    }

    private static JsonElement Only(RecordRing ring) =>
        JsonDocument.Parse(Assert.Single(ring.Read(-1).Records).Json).RootElement;

    private static (AgentClock Clock, OutcomeCorrelator Correlator, RecordRing Ring) Build()
    {
        var clock = new AgentClock();
        var ring = new RecordRing(16);
        return (clock, new OutcomeCorrelator(new Publisher(clock, ring), clock), ring);
    }
}
