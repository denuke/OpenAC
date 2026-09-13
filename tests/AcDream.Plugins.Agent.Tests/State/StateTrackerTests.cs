using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class StateTrackerTests
{
    [Fact]
    public void TheFirstCaptureIsPublishedAndThenOnlyChanges()
    {
        var (tracker, ring, projection) = Build();

        tracker.Tick(0d);
        tracker.Tick(0.1);
        projection.Value = 2;
        tracker.Tick(0.2);
        tracker.Tick(0.3);

        Assert.Equal([1, 2], Values(ring));
    }

    [Fact]
    public void TheMinimumIntervalDefersAChangeWithoutLosingTheLastOne()
    {
        var (tracker, ring, projection) = Build(minimumInterval: 1d);

        tracker.Tick(0d);
        projection.Value = 2;
        tracker.Tick(0.2);
        projection.Value = 3;
        tracker.Tick(0.4);
        Assert.Equal([1], Values(ring));

        tracker.Tick(1.0);

        Assert.Equal([1, 3], Values(ring));
    }

    [Fact]
    public void AHeartbeatRepublishesAnUnchangedStateWithItsAge()
    {
        var (tracker, ring, _) = Build(heartbeat: 5d);

        tracker.Tick(0d);
        tracker.Tick(4.9);
        tracker.Tick(5.0);

        AgentRecord[] records = ring.Read(-1).Records.ToArray();
        Assert.Equal(2, records.Length);
        using JsonDocument last = JsonDocument.Parse(records[1].Json);
        Assert.Equal(5.0, last.RootElement.GetProperty("ageSeconds").GetDouble());
    }

    [Fact]
    public void ThePollIntervalLimitsCaptures()
    {
        var (tracker, _, projection) = Build(poll: 1d);

        tracker.Tick(0d);
        tracker.Tick(0.5);
        tracker.Tick(0.99);
        tracker.Tick(1.0);

        Assert.Equal(2, projection.Captures);
    }

    [Fact]
    public void ASnapshotIsOneSizedBatchAndIsNotRepeatedOnTheNextTick()
    {
        var clock = new AgentClock();
        var ring = new RecordRing(16);
        var publisher = new Publisher(clock, ring);
        var tracker = new StateTracker(publisher);
        tracker.Add(new CountingProjection("a"));
        tracker.Add(new CountingProjection("b"));
        publisher.Publish("earlier");

        tracker.PublishSnapshot(0d);
        tracker.Tick(0.1);

        AgentRecord[] records = ring.Read(0).Records.ToArray();
        Assert.Equal(["a", "b"], records.Select(record => record.Kind));
        for (int ordinal = 0; ordinal < records.Length; ordinal++)
        {
            using JsonDocument document = JsonDocument.Parse(records[ordinal].Json);
            JsonElement batch = document.RootElement.GetProperty("batch");
            Assert.Equal(1, batch.GetProperty("id").GetInt64());
            Assert.Equal(ordinal, batch.GetProperty("ordinal").GetInt32());
            Assert.Equal(2, batch.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public void ACaptureThatThrowsIsCountedAndSkipped()
    {
        var (tracker, ring, projection) = Build();
        projection.Throws = true;

        tracker.Tick(0d);

        Assert.Equal(1, tracker.CaptureFailures);
        Assert.Equal(0, ring.Count);
    }

    private static (StateTracker Tracker, RecordRing Ring, CountingProjection Projection) Build(
        double poll = 0d,
        double minimumInterval = 0d,
        double? heartbeat = null)
    {
        var ring = new RecordRing(64);
        var tracker = new StateTracker(new Publisher(new AgentClock(), ring));
        var projection = new CountingProjection("state")
        {
            PollSeconds = poll,
            MinimumIntervalSeconds = minimumInterval,
            HeartbeatSeconds = heartbeat,
        };
        tracker.Add(projection);
        return (tracker, ring, projection);
    }

    private static int[] Values(RecordRing ring) =>
        ring.Read(-1).Records
            .Select(record =>
            {
                using JsonDocument document = JsonDocument.Parse(record.Json);
                return document.RootElement.GetProperty("value").GetInt32();
            })
            .ToArray();

    private sealed class CountingProjection(string kind) : IStateProjection
    {
        public string Kind { get; } = kind;
        public double PollSeconds { get; init; }
        public double MinimumIntervalSeconds { get; init; }
        public double? HeartbeatSeconds { get; init; }
        internal int Value { get; set; } = 1;
        internal int Captures { get; private set; }
        internal bool Throws { get; set; }

        public JsonObject Capture()
        {
            Captures++;
            if (Throws)
                throw new InvalidOperationException("capture failed");
            return new JsonObject { ["value"] = Value };
        }
    }
}
