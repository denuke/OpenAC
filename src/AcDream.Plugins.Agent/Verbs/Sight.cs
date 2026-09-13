using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// Whether a shot from the character can reach an object, for each kind of
/// projectile: an arc spell, a war bolt and an arrow. Each flies its own path
/// from its own launch point to its own aim point, so the same object can be
/// visible to an arc that sails over a ledge and blocked for a bolt that meets
/// it. The client traces each path through its collision world, closed doors
/// included. A verdict is a prediction and the server still decides at launch;
/// a path the client cannot trace is cannot-say, with the reason, and never
/// counts as clear.
/// </summary>
internal static class Sight
{
    internal const string Visible = "visible";
    internal const string Blocked = "blocked";
    internal const string CannotSay = "cannot-say";

    /// <summary>The sphere a projectile sweeps, the step its path is sampled at, and the most steps one trace takes.</summary>
    internal const float ProjectileRadius = 0.4f;
    internal const float StepDistance = 0.7f;
    internal const int MaximumChecks = 500;

    /// <summary>One nearby answer traces only this many of its nearest objects, and only within this range.</summary>
    internal const int TracedObjects = 12;
    internal const double TracedRangeMeters = 80d;

    /// <summary>Each kind of projectile, its path, and the height on its target it aims at.</summary>
    internal static readonly IReadOnlyList<(string Word, PluginProjectilePathKind Path, PluginAttackHeight Aim)> Kinds =
    [
        ("arc", PluginProjectilePathKind.Arc, PluginAttackHeight.High),
        ("bolt", PluginProjectilePathKind.Straight, PluginAttackHeight.Medium),
        ("arrow", PluginProjectilePathKind.Missile, PluginAttackHeight.Low),
    ];

    /// <summary>A verdict for each kind of projectile, what blocked each blocked shot, and why any could not be traced.</summary>
    internal static JsonObject Trace(IProjectileAutomation projectiles, uint objectId)
    {
        ArgumentNullException.ThrowIfNull(projectiles);
        var sight = new JsonObject();
        var blockedBy = new JsonObject();
        string? because = null;
        foreach ((string word, PluginProjectilePathKind path, PluginAttackHeight aim) in Kinds)
        {
            PluginProjectilePathResult result = projectiles.EvaluatePath(
                objectId,
                path,
                aim,
                ProjectileRadius,
                StepDistance,
                MaximumChecks);
            switch (result.Status)
            {
                case PluginProjectilePathStatus.Clear:
                    sight[word] = Visible;
                    break;
                case PluginProjectilePathStatus.Blocked:
                    sight[word] = Blocked;
                    if (result.BlockingObjectId != 0u)
                        blockedBy[word] = Facts.Hex(result.BlockingObjectId);
                    break;
                default:
                    sight[word] = CannotSay;
                    because ??= Unanswered(result);
                    break;
            }
        }
        sight["blockedBy"] = blockedBy.Count == 0 ? null : blockedBy;
        sight["because"] = because;
        return sight;
    }

    /// <summary>Cannot-say for every kind of projectile, for an object whose paths were not traced.</summary>
    internal static JsonObject Untraced(string because)
    {
        var sight = new JsonObject();
        foreach ((string word, _, _) in Kinds)
            sight[word] = CannotSay;
        sight["blockedBy"] = null;
        sight["because"] = because;
        return sight;
    }

    private static string Unanswered(PluginProjectilePathResult result) => result.Status switch
    {
        PluginProjectilePathStatus.Unavailable => "this client cannot trace projectile paths for a plugin",
        PluginProjectilePathStatus.InvalidTarget => "the client has no physics body to trace from or to",
        PluginProjectilePathStatus.BudgetExceeded =>
            $"the path is longer than the {MaximumChecks * StepDistance:0} m one trace follows",
        _ => result.Notice ?? "the trace failed",
    };
}
