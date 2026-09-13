using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Mcp.Tools;

internal sealed class OutcomeTool(AgentContext context) : IMcpTool
{
    public string Name => "outcome";

    public JsonObject Definition() => ToolDefinitions.Make(
        Name,
        "Did it actually happen?",
        "How the command line with this handle ended: its outcome word, such as completed, "
        + "accepted, refused, ended or unconfirmed, with the shared class and every record the "
        + "line produced. pending means no answer yet; unknown means no record carries the handle. "
        + "unconfirmed means no answer came in time, and the action may still have happened. "
        + "Sends nothing.",
        new JsonObject
        {
            ["handle"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The handle act returned.",
            },
            ["waitSeconds"] = ToolDefinitions.WaitProperty(),
        },
        ["handle"],
        readOnly: true);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        if (ToolArguments.Whole(arguments, "handle") is not { } handle || handle < 1)
            return new FinishedRun(ToolResults.Error("handle must be the handle act returned"));

        var records = new LineRecords(handle);
        records.Scan(context.Ring);
        if (records.Records.Count == 0)
            return new FinishedRun(ToolResults.Of(records.Result()));
        return ToolLines.Settle(context, records, ToolArguments.WaitSeconds(arguments));
    }
}
