using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp;

/// <summary>The tools an MCP client can list and call.</summary>
internal interface IMcpTools
{
    JsonArray List();

    bool Has(string name);

    /// <summary>
    /// Runs one tool and returns its MCP result object. May wait on the game;
    /// never runs game code on the calling thread.
    /// </summary>
    Task<JsonObject> CallAsync(
        string name,
        JsonObject arguments,
        McpSession session,
        CancellationToken cancellationToken);
}
