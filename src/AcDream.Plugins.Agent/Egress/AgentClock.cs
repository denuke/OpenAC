namespace AcDream.Plugins.Agent.Egress;

/// <summary>
/// Seconds since the plugin started, advanced only by host ticks. Records are
/// stamped from this clock, never from the wall clock, so their order and
/// spacing are the session's own.
/// </summary>
internal sealed class AgentClock
{
    internal double Now { get; private set; }

    internal void Advance(double elapsedSeconds)
    {
        if (double.IsFinite(elapsedSeconds) && elapsedSeconds > 0d)
            Now += elapsedSeconds;
    }
}
