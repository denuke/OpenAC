using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Mcp.Tools;

internal static class ToolLines
{
    internal const string Source = "mcp";

    /// <summary>Delivers one line and gathers the records it produced at once.</summary>
    internal static LineRecords Deliver(AgentContext context, string line)
    {
        long cursor = context.Ring.LastSeq;
        CommandReceipt receipt = context.Commands.Deliver(line, Source);
        var records = new LineRecords(receipt.Id, cursor);
        records.Scan(context.Ring);
        return records;
    }

    /// <summary>Answers now when the line has settled or no wait was asked, otherwise waits.</summary>
    internal static IMcpToolRun Settle(AgentContext context, LineRecords records, double waitSeconds)
    {
        if (records.Settled || waitSeconds <= 0d)
            return new FinishedRun(ToolResults.Of(records.Result()));
        double deadline = context.Clock.Now + waitSeconds;
        return new PollingRun(() =>
        {
            records.Scan(context.Ring);
            return records.Settled || context.Clock.Now >= deadline
                ? ToolResults.Of(records.Result())
                : null;
        });
    }
}
