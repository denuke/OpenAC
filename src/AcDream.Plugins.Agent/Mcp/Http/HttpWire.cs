using System.Globalization;
using System.Text;

namespace AcDream.Plugins.Agent.Mcp.Http;

/// <summary>One HTTP/1.1 request read off a connection.</summary>
internal sealed class HttpWireRequest
{
    internal HttpWireRequest(
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        Method = method;
        Path = path;
        Headers = headers;
        Body = body;
    }

    internal string Method { get; }

    internal string Path { get; }

    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal byte[] Body { get; }

    /// <summary>Whether the client asked for the connection to close after this exchange.</summary>
    internal bool WantsClose =>
        Header("Connection") is { } connection
        && connection.Split(',').Any(token => token.Trim().Equals("close", StringComparison.OrdinalIgnoreCase));

    internal string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>A request refused before it reaches the endpoint, with the status to answer.</summary>
internal sealed class HttpWireException(int status, string message) : Exception(message)
{
    internal int Status { get; } = status;
}

/// <summary>
/// The part of HTTP/1.1 an MCP client on this computer needs: requests one at
/// a time per connection, bodies sized by Content-Length or sent chunked, and
/// responses sized by Content-Length.
/// </summary>
internal static class HttpWire
{
    internal const int MaximumHeadBytes = 16 * 1024;
    internal const int MaximumBodyBytes = 1024 * 1024;
    private const int MaximumTrailerLines = 64;

    /// <summary>Reads one request, or returns <see langword="null"/> when the connection closed before one began.</summary>
    internal static async Task<HttpWireRequest?> ReadRequestAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[]? head = await ReadHeadAsync(input, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;

        string[] lines = Encoding.Latin1.GetString(head).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || requestLine[1].Length == 0)
            throw new HttpWireException(400, "the request line is malformed");
        if (!requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new HttpWireException(505, "only HTTP/1.1 is served");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0)
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0 || line.AsSpan(0, colon).ContainsAny(' ', '\t'))
                throw new HttpWireException(400, "a header is malformed");
            string name = line[..colon];
            string value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? earlier) ? earlier + ", " + value : value;
        }

        string path = PathOf(requestLine[1]);
        byte[] body = await ReadBodyAsync(input, headers, cancellationToken).ConfigureAwait(false);
        return new HttpWireRequest(requestLine[0], path, headers, body);
    }

    internal static async Task WriteResponseAsync(
        Stream output,
        int status,
        string? contentType,
        byte[] body,
        IEnumerable<KeyValuePair<string, string>> headers,
        bool close,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(Reason(status)).Append("\r\n");
        if (contentType is not null)
            Header(head, "Content-Type", contentType);
        Header(head, "Content-Length", body.Length.ToString(CultureInfo.InvariantCulture));
        Header(head, "Cache-Control", "no-store");
        Header(head, "Connection", close ? "close" : "keep-alive");
        foreach (KeyValuePair<string, string> header in headers)
            Header(head, header.Key, header.Value);
        head.Append("\r\n");
        await output.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        413 => "Content Too Large",
        421 => "Misdirected Request",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        505 => "HTTP Version Not Supported",
        _ => "Unknown",
    };

    private static void Header(StringBuilder head, string name, string value)
    {
        if (name.Length == 0 || name.AsSpan().ContainsAny("\r\n: ") || value.AsSpan().ContainsAny('\r', '\n'))
            throw new ArgumentException($"The header '{name}' cannot be written.", nameof(name));
        head.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static string PathOf(string target)
    {
        if (target.StartsWith('/'))
        {
            int query = target.IndexOfAny(['?', '#']);
            return query < 0 ? target : target[..query];
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp)
            return uri.AbsolutePath;
        throw new HttpWireException(400, "the request target must be a path");
    }

    private static async Task<byte[]?> ReadHeadAsync(Stream input, CancellationToken cancellationToken)
    {
        var head = new MemoryStream();
        byte[] one = new byte[1];
        int matched = 0;
        while (true)
        {
            if (await input.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            {
                if (head.Length == 0)
                    return null;
                throw new EndOfStreamException("the connection closed inside a request head");
            }
            byte value = one[0];
            if (head.Length == 0 && value is (byte)'\r' or (byte)'\n')
                continue;
            head.WriteByte(value);
            if (head.Length > MaximumHeadBytes)
                throw new HttpWireException(431, "the request head is too large");
            byte expected = matched % 2 == 0 ? (byte)'\r' : (byte)'\n';
            matched = value == expected ? matched + 1 : value == (byte)'\r' ? 1 : 0;
            if (matched == 4)
                return head.ToArray();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        Stream input,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (headers.TryGetValue("Transfer-Encoding", out string? encoding))
        {
            if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                throw new HttpWireException(501, "only chunked transfer encoding is understood");
            return await ReadChunkedAsync(input, cancellationToken).ConfigureAwait(false);
        }
        if (!headers.TryGetValue("Content-Length", out string? length))
            return [];
        if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            throw new HttpWireException(400, "Content-Length is not a length");
        if (size > MaximumBodyBytes)
            throw new HttpWireException(413, "the request body is too large");
        byte[] body = new byte[size];
        await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream input, CancellationToken cancellationToken)
    {
        var body = new MemoryStream();
        while (true)
        {
            string sizeLine = await ReadLineAsync(input, cancellationToken).ConfigureAwait(false);
            int extension = sizeLine.IndexOf(';');
            string hex = (extension < 0 ? sizeLine : sizeLine[..extension]).Trim();
            if (hex.Length is 0 or > 7
                || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int size))
            {
                throw new HttpWireException(400, "a chunk size is malformed");
            }
            if (size == 0)
            {
                for (int trailer = 0; ; trailer++)
                {
                    if ((await ReadLineAsync(input, cancellationToken).ConfigureAwait(false)).Length == 0)
                        return body.ToArray();
                    if (trailer == MaximumTrailerLines)
                        throw new HttpWireException(431, "the request has too many trailers");
                }
            }
            if (body.Length + size > MaximumBodyBytes)
                throw new HttpWireException(413, "the request body is too large");
            byte[] chunk = new byte[size];
            await input.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            body.Write(chunk);
            if ((await ReadLineAsync(input, cancellationToken).ConfigureAwait(false)).Length != 0)
                throw new HttpWireException(400, "a chunk is malformed");
        }
    }

    private static async Task<string> ReadLineAsync(Stream input, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        byte[] one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("the connection closed inside a chunked body");
            if (one[0] == (byte)'\n')
                return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumHeadBytes)
                throw new HttpWireException(431, "a line is too long");
            line.Append((char)one[0]);
        }
    }
}
