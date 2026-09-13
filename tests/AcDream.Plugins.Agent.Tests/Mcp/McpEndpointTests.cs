using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Mcp;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class McpEndpointTests
{
    [Fact]
    public async Task InitializeIssuesASessionAndNegotiatesTheVersion()
    {
        var (endpoint, sessions, _) = Build();

        McpHttpResponse response = await Post(endpoint, null,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");

        Assert.Equal(200, response.Status);
        string session = Assert.Single(response.Headers, header => header.Key == McpEndpoint.SessionHeader).Value;
        Assert.True(sessions.TryGet(session, out _));
        JsonElement result = Body(response).GetProperty("result");
        Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
        Assert.Equal(McpEndpoint.ServerName, result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task AnUnsupportedVersionIsAnsweredWithTheNewestSupported()
    {
        var (endpoint, _, _) = Build();

        McpHttpResponse response = await Post(endpoint, null,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal(
            McpEndpoint.SupportedVersions[0],
            Body(response).GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task ARequestWithoutASessionIsRejected()
    {
        var (endpoint, _, _) = Build();

        Assert.Equal(400, (await Post(endpoint, null, """{"jsonrpc":"2.0","id":2,"method":"ping"}""")).Status);
    }

    [Fact]
    public async Task AnUnknownSessionIsNotFound()
    {
        var (endpoint, _, _) = Build();

        Assert.Equal(404, (await Post(endpoint, "nope", """{"jsonrpc":"2.0","id":2,"method":"ping"}""")).Status);
    }

    [Fact]
    public async Task ANotificationIsAcceptedWithNoBody()
    {
        var (endpoint, sessions, _) = Build();
        string session = sessions.Create("2025-06-18").Id;

        McpHttpResponse response = await Post(endpoint, session, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(202, response.Status);
        Assert.Null(response.Body);
    }

    [Fact]
    public async Task PingAndToolsListAreAnswered()
    {
        var (endpoint, sessions, _) = Build();
        string session = sessions.Create("2025-06-18").Id;

        JsonElement ping = Body(await Post(endpoint, session, """{"jsonrpc":"2.0","id":3,"method":"ping"}"""));
        JsonElement list = Body(await Post(endpoint, session, """{"jsonrpc":"2.0","id":4,"method":"tools/list"}"""));

        Assert.Equal(JsonValueKind.Object, ping.GetProperty("result").ValueKind);
        Assert.Equal("echo", list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToolsCallRunsTheToolWithItsArguments()
    {
        var (endpoint, sessions, tools) = Build();
        string session = sessions.Create("2025-06-18").Id;

        JsonElement call = Body(await Post(endpoint, session,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":{"say":"hi"}}}"""));

        Assert.Equal("hi", call.GetProperty("result").GetProperty("said").GetString());
        Assert.Equal(session, tools.LastSession!.Id);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"missing"}}""", JsonRpc.InvalidParams)]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{}}""", JsonRpc.InvalidParams)]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"resources/list"}""", JsonRpc.MethodNotFound)]
    [InlineData("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"boom"}}""", JsonRpc.InternalError)]
    public async Task AFailedCallIsAnErrorWithItsCode(string body, int code)
    {
        var (endpoint, sessions, _) = Build();
        string session = sessions.Create("2025-06-18").Id;

        JsonElement response = Body(await Post(endpoint, session, body));

        Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("https://evil.example", 403)]
    [InlineData("http://localhost:3000", 200)]
    [InlineData("http://127.0.0.1", 200)]
    public async Task OnlyLoopbackOriginsAreServed(string origin, int status)
    {
        var (endpoint, _, _) = Build();

        McpHttpResponse response = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", null, "application/json", origin,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
            CancellationToken.None);

        Assert.Equal(status, response.Status);
    }

    [Fact]
    public async Task AnotherPathIsNotFound()
    {
        var (endpoint, _, _) = Build();

        McpHttpResponse response = await endpoint.HandleAsync(
            new McpHttpRequest("POST", "/other", null, null, null, "{}"),
            CancellationToken.None);

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task GetOpensAStreamOnlyForEventStreamClients()
    {
        var (endpoint, sessions, _) = Build();
        McpSession session = sessions.Create("2025-06-18");

        McpHttpResponse wrong = await Send(endpoint, "GET", session.Id, "application/json");
        McpHttpResponse stream = await Send(endpoint, "GET", session.Id, "text/event-stream");

        Assert.Equal(406, wrong.Status);
        Assert.Equal(200, stream.Status);
        Assert.Equal("text/event-stream", stream.ContentType);
        Assert.Same(session, stream.StreamSession);
    }

    [Fact]
    public async Task DeleteEndsTheSession()
    {
        var (endpoint, sessions, _) = Build();
        string session = sessions.Create("2025-06-18").Id;

        Assert.Equal(200, (await Send(endpoint, "DELETE", session, null)).Status);
        Assert.Equal(404, (await Post(endpoint, session, """{"jsonrpc":"2.0","id":1,"method":"ping"}""")).Status);
    }

    private static Task<McpHttpResponse> Post(McpEndpoint endpoint, string? session, string body) =>
        endpoint.HandleAsync(
            new McpHttpRequest("POST", "/mcp", session, "application/json, text/event-stream", null, body),
            CancellationToken.None);

    private static Task<McpHttpResponse> Send(McpEndpoint endpoint, string method, string? session, string? accept) =>
        endpoint.HandleAsync(
            new McpHttpRequest(method, "/mcp", session, accept, null, string.Empty),
            CancellationToken.None);

    private static JsonElement Body(McpHttpResponse response) =>
        JsonDocument.Parse(response.Body!).RootElement;

    private static (McpEndpoint Endpoint, McpSessions Sessions, FakeTools Tools) Build()
    {
        var sessions = new McpSessions();
        var tools = new FakeTools();
        return (new McpEndpoint(sessions, tools), sessions, tools);
    }

    private sealed class FakeTools : IMcpTools
    {
        internal McpSession? LastSession { get; private set; }

        public JsonArray List() => [new JsonObject { ["name"] = "echo" }];

        public bool Has(string name) => name is "echo" or "boom";

        public Task<JsonObject> CallAsync(string name, JsonObject arguments, McpSession session, CancellationToken cancellationToken)
        {
            LastSession = session;
            if (name == "boom")
                throw new InvalidOperationException("tool exploded");
            return Task.FromResult(new JsonObject { ["said"] = arguments["say"]?.DeepClone() });
        }
    }
}
