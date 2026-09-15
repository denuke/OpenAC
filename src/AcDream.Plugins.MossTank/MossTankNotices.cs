using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>
/// What MossTank posts to the host's notice board for other plugins, such as an agent running
/// the macro: the warnings it says in chat, problems with its setup, and how its macro and
/// route are getting on.
/// </summary>
internal static class MossTankNotices
{
    internal const string MacroStarted = "macro-started";
    internal const string MacroStopped = "macro-stopped";
    internal const string MacroIdle = "macro-idle";
    internal const string CharacterDied = "character-died";
    internal const string Misconfigured = "misconfigured";
    internal const string CombatWarning = "combat-warning";
    internal const string BuffWarning = "buff-warning";
    internal const string ItemWarning = "item-warning";
    internal const string GhostTarget = "ghost-target";
    internal const string ProfileRecovered = "profile-recovered";
    internal const string RouteWaypoint = "route-waypoint";
    internal const string RouteLap = "route-lap";
    internal const string RouteFinished = "route-finished";
    internal const string RouteSkipped = "route-skipped";
    internal const string RouteStopped = "route-stopped";
    internal const string RouteStuck = "route-stuck";

    internal const string NoCastingDevice = "no-casting-device";
    internal const string RuleWeaponNotCarried = "rule-weapon-not-carried";
    internal const string RouteEmpty = "route-empty";
    internal const string RouteElsewhere = "route-elsewhere";
    internal const string FollowTargetMissing = "follow-target-missing";
    internal const string LootRulesEmpty = "loot-rules-empty";
    internal const string LowWaypointDistance = "low-waypoint-distance";
    internal const string WalkLegsUnavailable = "walk-legs-unavailable";

    /// <summary>Says a line in chat and posts it as a notice.</summary>
    internal static void Announce(
        IPluginHost host,
        string kind,
        PluginNoticeSeverity severity,
        string text,
        JsonObject? details = null)
    {
        host.Automation.Chat.PostSystemMessage("[MossTank] " + text);
        Post(host, kind, severity, text, details);
    }

    /// <summary>Posts a notice without a chat line, for progress that would crowd chat.</summary>
    internal static void Post(
        IPluginHost host,
        string kind,
        PluginNoticeSeverity severity,
        string text,
        JsonObject? details = null) =>
        host.Notices.Post(kind, severity, text, details?.ToJsonString());

    /// <summary>The details of a setup problem: which problem, and what it concerns when that helps.</summary>
    internal static JsonObject Problem(string problem, string? subject = null)
    {
        var details = new JsonObject { ["problem"] = problem };
        if (subject is not null)
            details["subject"] = subject;
        return details;
    }
}
