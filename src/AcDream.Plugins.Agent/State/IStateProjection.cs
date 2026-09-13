using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.State;

/// <summary>One piece of current state the plugin publishes when it changes.</summary>
internal interface IStateProjection
{
    string Kind { get; }

    /// <summary>Seconds between captures. Zero captures on every tick.</summary>
    double PollSeconds { get; }

    /// <summary>Least seconds between two published changes.</summary>
    double MinimumIntervalSeconds { get; }

    /// <summary>
    /// Seconds after which an unchanged state is published again, or
    /// <see langword="null"/> to publish only changes.
    /// </summary>
    double? HeartbeatSeconds { get; }

    /// <summary>
    /// The state's fields, freshly built. Called on the update thread. Holds
    /// nothing that changes merely because time passed.
    /// </summary>
    JsonObject Capture();
}
