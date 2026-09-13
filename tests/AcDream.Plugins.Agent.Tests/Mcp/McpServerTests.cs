using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Mcp;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.Mcp;

/// <summary>The agent serving MCP on a real loopback socket, with the game's update ticks driven by the test.</summary>
public sealed class McpServerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakePluginHost _host = new();
    private readonly AgentService _service;
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Patience };
    private McpServer? _server;

    public McpServerTests()
    {
        _host.FakeAutomation.FakeNavigation.Snapshot = McpToolHarness.Body(heading: 0f);
        _service = new AgentService(_host, _ => new RecordingSink(), (endpoint, _) => _server = McpServer.Start(endpoint, 0));
        _service.StartListening();
    }

    [Fact]
    public async Task AClientInitializesListsToolsAndActsOverLoopback()
    {
        Assert.StartsWith("http://127.0.0.1:", _service.ListenerUrl);
        string session = await InitializeAsync();

        using JsonDocument tools = await JsonAsync(await PostAsync(session, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        Assert.Contains(
            tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "act");

        using JsonDocument said = await JsonAsync(await TickUntilAnswered(PostAsync(session,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"act","arguments":{"line":"say hello"}}}""")));
        JsonElement result = said.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("done", result.GetProperty("status").GetString());
        Assert.Contains("hello", Assert.Single(_host.FakeAutomation.FakeChat.Submitted));
    }

    [Fact]
    public async Task ARequestNamingAnotherHostIsRefusedBeforeTheEndpoint()
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _server!.Port);
        NetworkStream stream = socket.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: rebound.example:{_server.Port}\r\nContent-Length: 2\r\n\r\n{{}}"));
        using var reading = new CancellationTokenSource(Patience);
        string response = await new StreamReader(stream).ReadToEndAsync(reading.Token);

        Assert.StartsWith("HTTP/1.1 421 ", response);
        Assert.Equal(1, _server.Refused);
    }

    [Fact]
    public async Task AClientAskingToCloseGetsItsAnswerAndTheConnectionEnds()
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _server!.Port);
        NetworkStream stream = socket.GetStream();
        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\nConnection: close\r\n"
            + $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
        using var reading = new CancellationTokenSource(Patience);
        string response = await new StreamReader(stream).ReadToEndAsync(reading.Token);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.Contains("\r\nConnection: close\r\n", response);
        Assert.Contains("\"protocolVersion\"", response);
    }

    [Fact]
    public async Task ClosingTheServiceEndsAWaitingCallAndClosesThePort()
    {
        string session = await InitializeAsync();
        Task<HttpResponseMessage> waiting = PostAsync(session,
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"act","arguments":{"line":"turn to 90","waitSeconds":30}}}""");
        await TickUntil(() => _service.Tools.RunningCount == 1);
        int port = _server!.Port;

        _service.Dispose();

        await Assert.ThrowsAsync<HttpRequestException>(() => waiting.WaitAsync(Patience));
        Assert.Equal(0, _service.Tools.RunningCount);
        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    public void Dispose()
    {
        _service.Dispose();
        _client.Dispose();
    }

    private async Task<string> InitializeAsync()
    {
        HttpResponseMessage response = await PostAsync(null,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues(McpEndpoint.SessionHeader).Single();
    }

    private Task<HttpResponseMessage> PostAsync(string? session, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _service.ListenerUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null)
            request.Headers.Add(McpEndpoint.SessionHeader, session);
        return _client.SendAsync(request);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<T> TickUntilAnswered<T>(Task<T> call)
    {
        await TickUntil(() => call.IsCompleted);
        return await call;
    }

    /// <summary>Runs game updates on this thread, as the client's update loop would, until the condition holds.</summary>
    private async Task TickUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold in time");
            _service.OnTick(0.016);
            await Task.Delay(5);
        }
    }
}
