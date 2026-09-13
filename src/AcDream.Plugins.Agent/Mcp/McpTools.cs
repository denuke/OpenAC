using AcDream.Plugins.Agent.Mcp.Tools;

namespace AcDream.Plugins.Agent.Mcp;

internal static class McpTools
{
    internal static McpToolHost Create(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new McpToolHost(
        [
            new ObserveTool(context),
            new ActTool(context),
            new OutcomeTool(context),
            new EventsTool(context),
            .. ReadTools.Create(context),
        ]);
    }
}
