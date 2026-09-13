using System.Collections.Concurrent;
using System.Text;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Egress;

/// <summary>
/// Appends the record stream to a file, one JSON object per line, from its own
/// thread. When it falls behind it drops records rather than slow the game,
/// and writes a gap line saying how many and where the stream resumes.
/// </summary>
internal sealed class FileSink : IRecordSink
{
    private readonly BlockingCollection<string> _queue;
    private readonly Thread _writer;
    private readonly ManualResetEventSlim _drained = new(true);
    private long _pendingDrops;
    private long _writeFailures;
    private bool _disposed;

    internal FileSink(string path, int capacity = 4096)
        : this(path, capacity, OpenAppend)
    {
    }

    internal FileSink(string path, int capacity, Func<string, TextWriter> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentNullException.ThrowIfNull(open);
        Path = System.IO.Path.GetFullPath(path);
        TextWriter output = open(Path);
        _queue = new BlockingCollection<string>(
            new ConcurrentQueue<string>(),
            capacity);
        _writer = new Thread(() => Drain(output))
        {
            IsBackground = true,
            Name = "agent-record-file",
        };
        _writer.Start();
    }

    internal string Path { get; }

    internal long Dropped { get; private set; }

    internal long WriteFailures => Interlocked.Read(ref _writeFailures);

    public bool TryOffer(AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_disposed)
            return false;

        int needed = _pendingDrops > 0 ? 2 : 1;
        if (_queue.Count > _queue.BoundedCapacity - needed)
        {
            _pendingDrops++;
            Dropped++;
            return false;
        }

        _drained.Reset();
        if (_pendingDrops > 0)
        {
            _queue.Add(AgentJson.GapLine(_pendingDrops, record.Seq));
            _pendingDrops = 0;
        }
        _queue.Add(record.Json);
        return true;
    }

    /// <summary>Waits until every accepted line has been written.</summary>
    internal bool WaitForDrain(TimeSpan timeout) => _drained.Wait(timeout);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
        if (_writer.Join(TimeSpan.FromSeconds(5)))
        {
            _queue.Dispose();
            _drained.Dispose();
        }
    }

    private void Drain(TextWriter output)
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    output.WriteLine(line);
                    output.Flush();
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref _writeFailures);
                }
                if (_queue.Count == 0)
                    _drained.Set();
            }
        }
        finally
        {
            output.Dispose();
            _drained.Set();
        }
    }

    private static TextWriter OpenAppend(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false))
        {
            NewLine = "\n",
        };
    }
}
