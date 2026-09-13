using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent;

/// <summary>The pieces a consumer of the agent acts and reads through.</summary>
internal sealed record AgentContext(
    IPluginHost Host,
    CommandDispatcher Commands,
    RecordRing Ring,
    OutcomeCorrelator Outcomes,
    AgentClock Clock,
    Publisher Publisher);
