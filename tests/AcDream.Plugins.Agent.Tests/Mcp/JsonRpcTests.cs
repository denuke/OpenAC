using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Mcp;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class JsonRpcTests
{
    [Fact]
    public void ARequestKeepsItsIdMethodAndParams()
    {
        Assert.True(JsonRpc.TryParse(
            """{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"observe"}}""",
            out JsonRpc.Message message,
            out _));

        Assert.True(message.IsRequest);
        Assert.Equal("tools/call", message.Method);
        Assert.Equal("abc", message.Id!.GetValue<string>());
        Assert.Equal("observe", message.Params!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ANotificationHasNoId()
    {
        Assert.True(JsonRpc.TryParse(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            out JsonRpc.Message message,
            out _));

        Assert.False(message.IsRequest);
    }

    [Theory]
    [InlineData("not json", JsonRpc.ParseError)]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", JsonRpc.InvalidRequest)]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""", JsonRpc.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", JsonRpc.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ping","params":[1]}""", JsonRpc.InvalidParams)]
    public void AMalformedMessageIsRefusedWithItsCode(string body, int code)
    {
        Assert.False(JsonRpc.TryParse(body, out _, out JsonObject? error));
        Assert.Equal(code, error!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void AResultEchoesANumericIdExactly()
    {
        JsonRpc.TryParse("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", out JsonRpc.Message message, out _);

        Assert.Equal(
            """{"jsonrpc":"2.0","id":7,"result":{}}""",
            JsonRpc.Result(message.Id, new JsonObject()).ToJsonString(AgentJson.Options));
    }
}
