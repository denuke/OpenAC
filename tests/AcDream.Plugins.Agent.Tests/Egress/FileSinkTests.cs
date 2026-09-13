using System.Text;
using System.Text.Json;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.Tests.Egress;

public sealed class FileSinkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void EachAcceptedRecordBecomesOneLineInOrder()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-agent-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "records.jsonl");
        try
        {
            using (var sink = new FileSink(path))
            {
                Assert.True(sink.TryOffer(Record(0)));
                Assert.True(sink.TryOffer(Record(1)));
            }

            Assert.Equal(
                [Record(0).Json, Record(1).Json],
                File.ReadAllLines(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AFullQueueDropsAndTheNextLineMarksTheGap()
    {
        var output = new GateWriter();
        using var sink = new FileSink("records.jsonl", 2, _ => output);

        Assert.True(sink.TryOffer(Record(0)));
        Assert.True(output.Entered.Wait(Patience));
        Assert.True(sink.TryOffer(Record(1)));
        Assert.True(sink.TryOffer(Record(2)));
        Assert.False(sink.TryOffer(Record(3)));
        Assert.Equal(1, sink.Dropped);

        output.Release.Set();
        Assert.True(sink.WaitForDrain(Patience));
        Assert.True(sink.TryOffer(Record(4)));
        Assert.True(sink.WaitForDrain(Patience));

        string[] lines = output.Snapshot();
        Assert.Equal(5, lines.Length);
        Assert.Equal(Record(2).Json, lines[2]);
        using JsonDocument gap = JsonDocument.Parse(lines[3]);
        Assert.Equal("event-gap", gap.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, gap.RootElement.GetProperty("dropped").GetInt64());
        Assert.Equal(4, gap.RootElement.GetProperty("resumesAtSeq").GetInt64());
        Assert.Equal(JsonValueKind.Null, gap.RootElement.GetProperty("seq").ValueKind);
        Assert.Equal(Record(4).Json, lines[4]);
    }

    [Fact]
    public void AfterDisposeOffersAreRefused()
    {
        var output = new GateWriter();
        output.Release.Set();
        var sink = new FileSink("records.jsonl", 4, _ => output);

        sink.Dispose();
        sink.Dispose();

        Assert.False(sink.TryOffer(Record(0)));
    }

    private static AgentRecord Record(long seq) =>
        new(seq, 0d, "a", $$"""{"seq":{{seq}}}""");

    private sealed class GateWriter : TextWriter
    {
        private readonly List<string> _lines = [];

        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Entered.Set();
            Release.Wait(Patience);
            lock (_lines)
                _lines.Add(value ?? string.Empty);
        }

        internal string[] Snapshot()
        {
            lock (_lines)
                return _lines.ToArray();
        }
    }
}
