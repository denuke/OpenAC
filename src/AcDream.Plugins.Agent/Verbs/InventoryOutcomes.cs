namespace AcDream.Plugins.Agent.Verbs;

/// <summary>The words every family ending in an <c>inventory-outcome</c> uses.</summary>
internal static class InventoryOutcomes
{
    internal const string Completed = "completed";
    internal const string Refused = "refused";
    internal const double WindowSeconds = 10d;

    internal static readonly IReadOnlyList<string> Words = [Completed, Refused];
}
