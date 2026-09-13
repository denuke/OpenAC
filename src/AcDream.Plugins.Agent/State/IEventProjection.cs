using AcDream.Plugins.Agent.Egress;

namespace AcDream.Plugins.Agent.State;

/// <summary>Publishes things that happen, as they happen.</summary>
internal interface IEventProjection
{
    /// <summary>Publishes what happened since the last poll. Called on the update thread.</summary>
    void Poll(Publisher publisher);

    /// <summary>Forgets history, so the next poll reports only what happens from now on.</summary>
    void Rebase();
}
