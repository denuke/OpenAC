using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AcDream.Plugins.Agent.Mcp.Http;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>What an AI client on this computer connects to.</summary>
internal interface IMcpListener : IDisposable
{
    string Url { get; }
}

/// <summary>
/// Serves the MCP endpoint over HTTP on 127.0.0.1 only. Each connection is
/// served on the thread pool; a tool call waits for the game's update thread
/// through the tool host and never runs game code on a connection.
/// </summary>
internal sealed class McpServer : IMcpListener
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly McpEndpoint _endpoint;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _connections = new();
    private readonly Task _accepting;
    private long _refused;
    private long _failures;
    private int _disposed;

    private McpServer(McpEndpoint endpoint, TcpListener listener)
    {
        _endpoint = endpoint;
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    internal int Port { get; }

    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/mcp");

    internal int Connections => _connections.Count;

    /// <summary>Requests refused before the endpoint: malformed, too large, or naming another host.</summary>
    internal long Refused => Interlocked.Read(ref _refused);

    /// <summary>Connections or accepts that ended on an unexpected error.</summary>
    internal long Failures => Interlocked.Read(ref _failures);

    /// <summary>Starts listening on 127.0.0.1. Port 0 takes any free port.</summary>
    internal static McpServer Start(McpEndpoint endpoint, int port)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        return new McpServer(endpoint, listener);
    }

    /// <summary>Stops accepting, closes every connection, and waits briefly for them to end.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _listener.Stop();
        foreach (TcpClient client in _connections.Keys)
            client.Close();
        Task[] running = [_accepting, .. _connections.Values];
        bool ended;
        try
        {
            ended = Task.WaitAll(running, StopTimeout);
        }
        catch (AggregateException)
        {
            ended = true;
        }
        if (ended)
            _stopping.Dispose();
    }

    /// <summary>
    /// Whether a Host header names this computer's loopback address, so a web
    /// page that points its own name at 127.0.0.1 cannot reach the endpoint.
    /// </summary>
    internal static bool HostAllowed(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        string value = host.Trim();
        string name;
        string? portText = null;
        if (value.StartsWith('['))
        {
            int end = value.IndexOf(']');
            if (end < 0)
                return false;
            name = value[..(end + 1)];
            string rest = value[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                    return false;
                portText = rest[1..];
            }
        }
        else
        {
            int colon = value.IndexOf(':');
            name = colon < 0 ? value : value[..colon];
            portText = colon < 0 ? null : value[(colon + 1)..];
        }
        bool loopback = name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name == "127.0.0.1"
            || name == "[::1]";
        return loopback && (portText is null || portText == port.ToString(CultureInfo.InvariantCulture));
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                Interlocked.Increment(ref _failures);
                return;
            }

            Task serving = ServeAsync(client);
            _connections[client] = serving;
            _ = serving.ContinueWith(
                static (_, state) =>
                {
                    var (connections, finished) = ((ConcurrentDictionary<TcpClient, Task>, TcpClient))state!;
                    connections.TryRemove(finished, out Task? _);
                },
                (_connections, client),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream network = client.GetStream();
                var input = new BufferedStream(network);
                while (!_stopping.IsCancellationRequested)
                {
                    HttpWireRequest? request;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                    {
                        idle.CancelAfter(IdleTimeout);
                        try
                        {
                            request = await HttpWire.ReadRequestAsync(input, idle.Token).ConfigureAwait(false);
                        }
                        catch (HttpWireException refused)
                        {
                            await RefuseAsync(network, refused.Status, refused.Message).ConfigureAwait(false);
                            return;
                        }
                    }
                    if (request is null)
                        return;
                    if (!HostAllowed(request.Header("Host"), Port))
                    {
                        await RefuseAsync(network, 421, "only 127.0.0.1 and localhost are served").ConfigureAwait(false);
                        return;
                    }

                    McpHttpResponse response = await _endpoint.HandleAsync(
                        new McpHttpRequest(
                            request.Method,
                            request.Path,
                            request.Header(McpEndpoint.SessionHeader),
                            request.Header("Accept"),
                            request.Header("Origin"),
                            Encoding.UTF8.GetString(request.Body)),
                        _stopping.Token).ConfigureAwait(false);
                    bool close = request.WantsClose || _stopping.IsCancellationRequested;
                    await HttpWire.WriteResponseAsync(
                        network,
                        response.Status,
                        response.ContentType,
                        response.Body is null ? [] : Encoding.UTF8.GetBytes(response.Body),
                        response.Headers,
                        close,
                        _stopping.Token).ConfigureAwait(false);
                    if (close)
                        return;
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private async Task RefuseAsync(NetworkStream network, int status, string reason)
    {
        Interlocked.Increment(ref _refused);
        await HttpWire.WriteResponseAsync(
            network,
            status,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(reason),
            [],
            close: true,
            _stopping.Token).ConfigureAwait(false);
    }
}
