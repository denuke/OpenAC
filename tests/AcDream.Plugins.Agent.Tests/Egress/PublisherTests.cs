using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Tests.Egress;

public sealed class PublisherTests
{
    [Fact]
    public void TheEnvelopeLeadsAndNullsAreWrittenOut()
    {
        var publisher = new Publisher(new AgentClock(), new RecordRing(8));

        AgentRecord record = publisher.Publish(
            "session",
            new JsonObject { ["state"] = "idle", ["reason"] = null });

        Assert.Equal(
            """{"schema":"acdream.agent.v1","seq":0,"at":0,"kind":"session","batch":null,"state":"idle","reason":null}""",
            record.Json);
    }

    [Fact]
    public void SequenceNumbersAreDenseFromZero()
    {
        var ring = new RecordRing(8);
        var publisher = new Publisher(new AgentClock(), ring);

        publisher.Publish("a");
        publisher.Publish("b");
        publisher.Publish("c");

        Assert.Equal([0L, 1L, 2L], ring.Read(-1).Records.Select(r => r.Seq));
        Assert.Equal(3, publisher.Published);
    }

    [Fact]
    public void RecordsAreStampedFromTheTickClock()
    {
        var clock = new AgentClock();
        var publisher = new Publisher(clock, new RecordRing(8));

        clock.Advance(0.0166);
        clock.Advance(0.0166);
        clock.Advance(double.NaN);
        clock.Advance(-1d);

        Assert.Equal(0.033, publisher.Publish("a").At);
    }

    [Fact]
    public void FieldsCannotReuseAnEnvelopeName()
    {
        var publisher = new Publisher(new AgentClock(), new RecordRing(8));

        Assert.Throws<ArgumentException>(
            () => publisher.Publish("a", new JsonObject { ["seq"] = 9 }));
    }

    [Fact]
    public void ASinkThatDeclinesIsCountedAndTheStreamStaysWhole()
    {
        var ring = new RecordRing(8);
        var publisher = new Publisher(new AgentClock(), ring);
        var full = new RecordingSink { Accepts = false };
        var open = new RecordingSink();
        publisher.Attach(full);
        publisher.Attach(open);

        publisher.Publish("a");
        publisher.Publish("b");

        Assert.Equal(2, publisher.Dropped);
        Assert.Equal(2, ring.Read(-1).Records.Count);
        Assert.Equal([0L, 1L], open.Records.Select(r => r.Seq));
    }

    [Fact]
    public void ADetachedSinkReceivesNothingMore()
    {
        var publisher = new Publisher(new AgentClock(), new RecordRing(8));
        var sink = new RecordingSink();
        publisher.Attach(sink);
        publisher.Publish("a");

        Assert.True(publisher.Detach(sink));
        publisher.Publish("b");

        Assert.Equal(["a"], sink.Records.Select(r => r.Kind));
    }

    private sealed class RecordingSink : IRecordSink
    {
        internal bool Accepts { get; init; } = true;
        internal List<AgentRecord> Records { get; } = [];

        public bool TryOffer(AgentRecord record)
        {
            if (!Accepts)
                return false;
            Records.Add(record);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
