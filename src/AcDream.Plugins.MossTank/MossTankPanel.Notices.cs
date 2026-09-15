using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>A part of MossTank's setup that stops part of it working, such as no casting device on the Items list.</summary>
internal readonly record struct SetupProblem(
    string Problem,
    PluginNoticeSeverity Severity,
    string Message,
    string? Subject = null);

internal sealed partial class MossTankPanel
{
    /// <summary>How long the running macro may have nothing to do before that is posted.</summary>
    internal const double MacroIdleSeconds = 120d;

    private double _macroIdleSeconds;
    private bool _macroIdlePosted;

    /// <summary>
    /// The setup problems posted while they last, so each is posted once, and whether the
    /// setup has been checked since the macro started.
    /// </summary>
    private readonly HashSet<string> _postedProblems = new(StringComparer.Ordinal);
    private bool _setupChecked;

    private void Announce(
        string kind,
        PluginNoticeSeverity severity,
        string text,
        JsonObject? details = null) =>
        MossTankNotices.Announce(_host, kind, severity, text, details);

    void IBuffRuleHost.Warn(string text) =>
        Announce(MossTankNotices.BuffWarning, PluginNoticeSeverity.Warning, text);

    /// <summary>Checks the setup once each time the macro starts, and notes how long the running macro has had nothing to do.</summary>
    private void NoteMacroState(bool macroRunning, double elapsedSeconds)
    {
        if (!macroRunning)
        {
            _setupChecked = false;
            _macroIdleSeconds = 0d;
            _macroIdlePosted = false;
            return;
        }
        if (!_setupChecked)
        {
            _setupChecked = true;
            _postedProblems.Clear();
            PostSetupProblems();
        }
        NoteIdle(elapsedSeconds);
    }

    /// <summary>
    /// Posts the macro idle once it has run for <see cref="MacroIdleSeconds"/> with no rule
    /// to run but its idle helpers, and no walk under way.
    /// </summary>
    private void NoteIdle(double elapsedSeconds)
    {
        bool idle = _scheduler.IsRunning
            && !_scheduler.IsSuspended
            && !_buffRule.IsBursting
            && _scheduler.LastExecutedRule?.Name is null or "RandomHelper" or "IdlePeace"
            && _host.Automation.Navigation.GoToReport.State
                is not (PluginGoToState.Planning or PluginGoToState.Walking or PluginGoToState.Waiting);
        if (!idle)
        {
            _macroIdleSeconds = 0d;
            _macroIdlePosted = false;
            return;
        }
        if (double.IsFinite(elapsedSeconds) && elapsedSeconds > 0d)
            _macroIdleSeconds += elapsedSeconds;
        if (_macroIdlePosted || _macroIdleSeconds < MacroIdleSeconds)
            return;
        _macroIdlePosted = true;
        MossTankNotices.Post(
            _host,
            MossTankNotices.MacroIdle,
            PluginNoticeSeverity.Warning,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The macro has had nothing to do for {_macroIdleSeconds:0} seconds."),
            new JsonObject
            {
                ["seconds"] = Math.Round(_macroIdleSeconds),
                ["navigation"] = _navigation.Status,
            });
    }

    /// <summary>Posts each setup problem not already posted, and forgets those put right, so one that comes back is posted again.</summary>
    private void PostSetupProblems()
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (SetupProblem problem in SetupProblems())
        {
            string key = problem.Problem + "\n" + problem.Subject;
            current.Add(key);
            if (_postedProblems.Contains(key))
                continue;
            Announce(
                MossTankNotices.Misconfigured,
                problem.Severity,
                problem.Message,
                MossTankNotices.Problem(problem.Problem, problem.Subject));
        }
        _postedProblems.Clear();
        _postedProblems.UnionWith(current);
    }

    /// <summary>The parts of the setup that stop part of MossTank working.</summary>
    internal IReadOnlyList<SetupProblem> SetupProblems()
    {
        var problems = new List<SetupProblem>();
        IAutomationSurface automation = _host.Automation;
        if (automation.IsAvailable && automation.Equipment.IsAvailable)
        {
            IReadOnlyList<PluginEquipmentItem> carried = automation.Equipment.CaptureOwnedEquipment();
            if (CastsSpells() && !_combatModeGate.CarriesProfiledCaster(carried))
            {
                problems.Add(new SetupProblem(
                    MossTankNotices.NoCastingDevice,
                    PluginNoticeSeverity.Error,
                    "No casting device on the Items list is carried, so MossTank cannot cast: "
                    + "add a wand, orb or staff the character carries to the Items list."));
            }
            foreach (MonsterRule rule in _combatSettings.Rules)
            {
                MonsterRuleActions actions = rule.Actions;
                if (rule.IsIgnoredSpec || (actions.WeaponObjectId == 0u && actions.WeaponName.Length == 0))
                    continue;
                if (carried.Any(item => item.ObjectId == actions.WeaponObjectId
                    || (actions.WeaponName.Length > 0 && item.Name.Equals(actions.WeaponName, StringComparison.Ordinal))))
                {
                    continue;
                }
                string weapon = actions.WeaponName.Length > 0
                    ? actions.WeaponName
                    : string.Create(CultureInfo.InvariantCulture, $"0x{actions.WeaponObjectId:X8}");
                problems.Add(new SetupProblem(
                    MossTankNotices.RuleWeaponNotCarried,
                    PluginNoticeSeverity.Warning,
                    $"Monster rule '{rule.Expression}' uses {weapon}, which the character does not carry.",
                    rule.Expression));
            }
        }
        if (_navigationSettings.Enabled)
        {
            if (_navigationSettings.Mode == RouteMode.Target)
            {
                if (_navigationSettings.FollowTargetObjectId == 0u && _navigationSettings.FollowTargetName.Length == 0)
                {
                    problems.Add(new SetupProblem(
                        MossTankNotices.FollowTargetMissing,
                        PluginNoticeSeverity.Warning,
                        "Navigation follows a target, but no target to follow is set."));
                }
            }
            else if (_navigationSettings.Waypoints.Count == 0)
            {
                problems.Add(new SetupProblem(
                    MossTankNotices.RouteEmpty,
                    PluginNoticeSeverity.Warning,
                    "Navigation is on, but the route has no waypoints."));
            }
        }
        LootSettings loot = _inventorySettings.Loot;
        if (loot.Enabled && loot.Rules.Count == 0 && loot.ExternalClassifierId.Length == 0)
        {
            problems.Add(new SetupProblem(
                MossTankNotices.LootRulesEmpty,
                PluginNoticeSeverity.Warning,
                "Looting is on, but the loot profile has no rules, so nothing is looted."));
        }
        return problems;
    }

    /// <summary>Whether the setup casts: buffing is on, or a monster rule casts more than a plain attack.</summary>
    private bool CastsSpells() =>
        _buffSettings.Enabled
        || _combatSettings.Rules.Any(static rule =>
            (rule.Actions.Flags & ~MonsterActionFlags.Attack) != MonsterActionFlags.None);

    private JsonArray SharedProblems() =>
        new([.. SetupProblems().Select(static problem => (JsonNode?)new JsonObject
        {
            ["problem"] = problem.Problem,
            ["severity"] = problem.Severity.ToString().ToLowerInvariant(),
            ["message"] = problem.Message,
            ["subject"] = problem.Subject,
        })]);
}
