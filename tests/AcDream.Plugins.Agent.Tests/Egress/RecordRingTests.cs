using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Tests.Egress;

public sealed class RecordRingTests
{
    [Fact]
    public void ReadsReturnRecordsAfterTheCursorInOrder()
    {
        var ring = new RecordRing(8);
        Append(ring, 0, "a");
        Append(ring, 1, "b");
        Append(ring, 2, "c");

        RecordRead read = ring.Read(0);

        Assert.Equal([1L, 2L], read.Records.Select(record => record.Seq));
        Assert.Equal(2, read.NextSeq);
        Assert.Equal(0, read.Missed);
        Assert.False(read.More);
    }

    [Fact]
    public void EvictionIsReportedAsRecordsTheReaderMissed()
    {
        var ring = new RecordRing(3);
        for (int seq = 0; seq < 5; seq++)
            Append(ring, seq, "a");

        RecordRead read = ring.Read(-1);

        Assert.Equal([2L, 3L, 4L], read.Records.Select(record => record.Seq));
        Assert.Equal(2, read.Missed);
    }

    [Fact]
    public void TheCursorAdvancesOverFilteredRecordsAndCountsThem()
    {
        var ring = new RecordRing(8);
        Append(ring, 0, "a");
        Append(ring, 1, "b");
        Append(ring, 2, "a");
        Append(ring, 3, "b");

        RecordRead read = ring.Read(-1, new HashSet<string> { "a" });

        Assert.Equal([0L, 2L], read.Records.Select(record => record.Seq));
        Assert.Equal(2, read.Filtered);
        Assert.Equal(3, read.NextSeq);
    }

    [Fact]
    public void AReadStopsAtItsMaximumAndSaysThereIsMore()
    {
        var ring = new RecordRing(8);
        for (int seq = 0; seq < 4; seq++)
            Append(ring, seq, "a");

        RecordRead read = ring.Read(-1, maximum: 2);

        Assert.Equal([0L, 1L], read.Records.Select(record => record.Seq));
        Assert.Equal(1, read.NextSeq);
        Assert.True(read.More);
    }

    [Fact]
    public void ThePinnedLatestRecordOutlivesEviction()
    {
        var ring = new RecordRing(2, ["session"]);
        Append(ring, 0, "session");
        Append(ring, 1, "a");
        Append(ring, 2, "a");

        Assert.Equal(0, ring.Latest("session")?.Seq);
        Assert.Null(ring.Latest("a"));
    }

    [Fact]
    public void OutOfOrderAppendsAreRefused()
    {
        var ring = new RecordRing(4);
        Append(ring, 3, "a");

        Assert.Throws<ArgumentException>(() => Append(ring, 3, "a"));
    }

    private static void Append(RecordRing ring, long seq, string kind) =>
        ring.Append(new AgentRecord(seq, 0d, kind, "{}"));
}
