using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AcDream.Plugins.Agent.Contract;

internal static class AgentJson
{
    /// <summary>Names the record vocabulary. Bumped by any breaking reshape.</summary>
    internal const string Schema = "acdream.agent.v1";

    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>
    /// The line a sink writes in place of records it had to drop. It carries
    /// no sequence number because it is not part of the stream; it tells one
    /// consumer where its copy of the stream resumes.
    /// </summary>
    internal static string GapLine(long dropped, long resumesAtSeq) =>
        new JsonObject
        {
            ["schema"] = Schema,
            ["seq"] = null,
            ["at"] = null,
            ["kind"] = "event-gap",
            ["batch"] = null,
            ["dropped"] = dropped,
            ["resumesAtSeq"] = resumesAtSeq,
            ["because"] = "this consumer fell behind, so records were dropped "
                + "for it; the stream itself is complete",
        }.ToJsonString(Options);
}
