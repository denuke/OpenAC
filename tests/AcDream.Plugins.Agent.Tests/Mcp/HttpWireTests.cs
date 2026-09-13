using System.Text;
using AcDream.Plugins.Agent.Mcp.Http;

namespace AcDream.Plugins.Agent.Tests.Mcp;

public sealed class HttpWireTests
{
    [Fact]
    public async Task ARequestIsReadWithItsHeadersPathAndBody()
    {
        using MemoryStream input = Stream(
            "POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1:31337\r\nMcp-Session-Id: abc\r\nContent-Length: 2\r\n\r\n{}");

        HttpWireRequest request = (await HttpWire.ReadRequestAsync(input, CancellationToken.None))!;

        Assert.Equal("POST", request.Method);
        Assert.Equal("/mcp", request.Path);
        Assert.Equal("abc", request.Header("mcp-session-id"));
        Assert.Equal("{}", Encoding.UTF8.GetString(request.Body));
        Assert.False(request.WantsClose);
    }

    [Fact]
    public async Task RequestsOnOneConnectionAreReadInTurnUntilItCloses()
    {
        using MemoryStream input = Stream(
            "POST /mcp HTTP/1.1\r\nContent-Length: 1\r\n\r\na"
            + "DELETE /mcp HTTP/1.1\r\nConnection: keep-alive, close\r\n\r\n");

        HttpWireRequest first = (await HttpWire.ReadRequestAsync(input, CancellationToken.None))!;
        HttpWireRequest second = (await HttpWire.ReadRequestAsync(input, CancellationToken.None))!;

        Assert.Equal("a", Encoding.UTF8.GetString(first.Body));
        Assert.Equal("DELETE", second.Method);
        Assert.True(second.WantsClose);
        Assert.Null(await HttpWire.ReadRequestAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task AChunkedBodyIsJoinedAndItsTrailersSkipped()
    {
        using MemoryStream input = Stream(
            "POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2;name=value\r\nde\r\n0\r\nTrailer: x\r\n\r\n");

        HttpWireRequest request = (await HttpWire.ReadRequestAsync(input, CancellationToken.None))!;

        Assert.Equal("abcde", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task AnAbsoluteTargetIsReducedToItsPath()
    {
        using MemoryStream input = Stream("POST http://127.0.0.1:31337/mcp HTTP/1.1\r\n\r\n");

        Assert.Equal("/mcp", (await HttpWire.ReadRequestAsync(input, CancellationToken.None))!.Path);
    }

    [Theory]
    [InlineData("POST /mcp\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/2.0\r\n\r\n", 505)]
    [InlineData("POST mcp HTTP/1.1\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nBad Header: x\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nContent-Length: ten\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nContent-Length: 1048577\r\n\r\n", 413)]
    [InlineData("POST /mcp HTTP/1.1\r\nTransfer-Encoding: gzip\r\n\r\n", 501)]
    [InlineData("POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n", 400)]
    public async Task AMalformedRequestIsRefusedWithItsStatus(string text, int status)
    {
        using MemoryStream input = Stream(text);

        HttpWireException refused = await Assert.ThrowsAsync<HttpWireException>(
            () => HttpWire.ReadRequestAsync(input, CancellationToken.None));

        Assert.Equal(status, refused.Status);
    }

    [Fact]
    public async Task AnOversizedHeadIsRefused()
    {
        using MemoryStream input = Stream(
            "POST /mcp HTTP/1.1\r\nX-Filler: " + new string('a', HttpWire.MaximumHeadBytes) + "\r\n\r\n");

        HttpWireException refused = await Assert.ThrowsAsync<HttpWireException>(
            () => HttpWire.ReadRequestAsync(input, CancellationToken.None));

        Assert.Equal(431, refused.Status);
    }

    [Fact]
    public async Task ABodyCutShortEndsTheConnection()
    {
        using MemoryStream input = Stream("POST /mcp HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc");

        await Assert.ThrowsAsync<EndOfStreamException>(() => HttpWire.ReadRequestAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task AResponseIsSizedAndCarriesItsHeaders()
    {
        using var output = new MemoryStream();

        await HttpWire.WriteResponseAsync(output, 200, "application/json", Encoding.UTF8.GetBytes("{}"),
            [new("Mcp-Session-Id", "abc")], close: false, CancellationToken.None);

        string text = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("\r\nContent-Length: 2\r\n", text);
        Assert.Contains("\r\nConnection: keep-alive\r\n", text);
        Assert.Contains("\r\nMcp-Session-Id: abc\r\n", text);
        Assert.EndsWith("\r\n\r\n{}", text);
    }

    [Fact]
    public async Task AHeaderThatWouldSplitTheResponseIsNeverWritten()
    {
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() => HttpWire.WriteResponseAsync(output, 200, null, [],
            [new("X-Test", "a\r\nInjected: yes")], close: true, CancellationToken.None));

        Assert.Equal(0, output.Length);
    }

    private static MemoryStream Stream(string text) => new(Encoding.Latin1.GetBytes(text));
}
