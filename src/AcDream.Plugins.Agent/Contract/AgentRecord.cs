namespace AcDream.Plugins.Agent.Contract;

/// <summary>One published record: its place in the stream and its JSON line.</summary>
internal sealed record AgentRecord(long Seq, double At, string Kind, string Json);
