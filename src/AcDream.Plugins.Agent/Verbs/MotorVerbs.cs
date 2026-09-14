using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.State;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// Moving and turning the character: <c>walk</c> and <c>run</c> forward or
/// backward, <c>strafe left|right</c> and <c>turn left|right</c>, each for a
/// distance, an angle, a time, or until <c>stop</c>; <c>turn to &lt;heading&gt;</c>,
/// <c>face &lt;guid&gt;</c>, <c>go to &lt;object&gt;</c>, <c>jump</c>,
/// <c>stance combat|peace</c> and <c>cancel</c>. Walking or running, strafing and
/// turning combine the way held movement keys do, and a new move replaces only
/// a move of its own kind. <c>go to</c> walks to an object or a place along a route the
/// client plans. The client carries out each move and ends it; these verbs ask
/// for it and report how it ended.
/// </summary>
internal sealed class MotorVerbs : IVerbFamily
{
    internal const double StanceWindowSeconds = 5d;
    internal const float DefaultJumpPower = 0.5f;
    internal const double JumpWindowSeconds = 3d;
    internal const float MaximumMoveMeters = 500f;
    internal const float MaximumTurnDegrees = 3600f;
    internal const float MaximumMoveSeconds = 300f;

    /// <summary>A heading this close to the one faced needs no turn.</summary>
    internal const double FacedDegrees = 0.05d;

    /// <summary>
    /// The client's own time limits for a move, which the wait for its report
    /// outlasts by a few seconds: thirty seconds without an amount, the time
    /// itself for a time, and otherwise the time the amount takes at these speeds
    /// plus three seconds.
    /// </summary>
    internal const double OpenEndedSeconds = 30d;
    internal const double SlowestMetersPerSecond = 1d;
    internal const double SlowestDegreesPerSecond = 30d;
    internal const double MoveWindowSlackSeconds = 5d;

    /// <summary>A walk to an object ends this near it unless the line says how near.</summary>
    internal const float DefaultArrivalMeters = 2.5f;
    internal const float MaximumArrivalMeters = 50f;

    /// <summary>The farthest object a walk is planned to.</summary>
    internal const double MaximumGoToMeters = 1000d;

    /// <summary>
    /// A walk's report is waited for as long as planning takes, plus the straight
    /// distance at this slow a pace, since a route bends around what is in the way.
    /// </summary>
    internal const double GoToPlanningSeconds = 30d;
    internal const double GoToSlowestMetersPerSecond = 0.5d;

    internal const string Completed = "completed";
    internal const string Cancelled = "cancelled";
    internal const string Blocked = "blocked";
    internal const string NoRoute = "no-route";

    internal const string NoOwnPosition = "the client has no position for its own body";
    internal const string NoObjectPosition = "the client holds no position for that object";
    internal const string InPortalSpace = "the character is in portal space";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Completed, Cancelled, Blocked, NoRoute];

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal MotorVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } =
        ["walk", "run", "strafe", "turn", "face", "go", "goto", "jump", "stop", "stance", "cancel"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        return line.Verb switch
        {
            "walk" or "run" or "strafe" => Travel(line),
            "turn" => Turn(line),
            "face" => Face(line),
            "go" or "goto" => GoTo(line),
            "jump" => Jump(line),
            "stop" => Stop(line),
            "stance" => Stance(line),
            _ => Cancel(line),
        };
    }

    private VerbResult Travel(CommandLine line)
    {
        bool strafe = line.Verb == "strafe";
        string usage = strafe
            ? "usage: strafe left|right [meters, or seconds such as 2s]"
            : $"usage: {line.Verb} [forward|backward] [meters, or seconds such as 20s]";
        PluginMoveDirection? direction = strafe ? null : PluginMoveDirection.Forward;
        bool named = false;
        Amount? amount = null;
        string[] words = Words(line);
        for (int index = 0; index < words.Length; index++)
        {
            if (!named && (strafe ? Sideways(words[index]) : Straight(words[index])) is { } given)
            {
                direction = given;
                named = true;
            }
            else if (amount is null && TryAmount(words, ref index, turn: false, out Amount parsed))
            {
                amount = parsed;
            }
            else
            {
                return Refuse(line, usage);
            }
        }
        if (direction is not { } chosen)
            return Refuse(line, usage);
        if (amount is { } measured && Problem(measured, turn: false) is { } problem)
            return Refuse(line, problem);
        return StartMove(
            line,
            chosen,
            line.Verb == "walk" ? PluginMovePace.Walk : PluginMovePace.Run,
            amount ?? Amount.None);
    }

    private VerbResult Turn(CommandLine line)
    {
        const string usage =
            "usage: turn left|right [degrees, or seconds such as 2s], or turn to <compass heading in degrees>";
        string[] words = Words(line);
        if (words.Length == 2 && words[0].Equals("to", StringComparison.OrdinalIgnoreCase))
        {
            return TryNumber(words[1], out float heading)
                ? TurnToward(line, Normalize(heading), facing: null)
                : Refuse(line, usage);
        }
        if (words.Length == 0 || TurnSide(words[0]) is not { } direction)
            return Refuse(line, usage);
        Amount amount = Amount.None;
        int index = 1;
        if (index < words.Length)
        {
            if (!TryAmount(words, ref index, turn: true, out amount) || index != words.Length - 1)
                return Refuse(line, usage);
            if (Problem(amount, turn: true) is { } problem)
                return Refuse(line, problem);
        }
        return StartMove(line, direction, PluginMovePace.Run, amount);
    }

    private VerbResult Face(CommandLine line)
    {
        if (!Guids.TryParse(line.Arguments, out uint id))
            return Refuse(line, "face needs an object id such as 0x70000001");
        IAutomationSurface automation = _host.Automation;
        if (!automation.Objects.TryGet(id, out PluginWorldObject value))
            return Refuse(line, NoObjectPosition);
        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        if (FaceProblem(self, value) is { } problem)
            return Refuse(line, problem);
        return TurnToward(line, Geometry.BearingDegrees(self.Position, value.Position), Facts.Hex(id));
    }

    /// <summary>Turns the short way onto a compass heading, as a turn that combines with other moves.</summary>
    private VerbResult TurnToward(CommandLine line, double heading, string? facing)
    {
        PluginNavigationSnapshot self = _host.Automation.Navigation.Snapshot;
        if (!self.IsAvailable)
            return Refuse(line, NoOwnPosition);
        var aim = new JsonObject
        {
            ["heading"] = Math.Round(heading, 1),
            ["facing"] = facing,
        };
        double delta = Geometry.RelativeDegrees(heading, self.Position.HeadingDegrees);
        if (Math.Abs(delta) < FacedDegrees)
        {
            Accepted(line, aim);
            _outcomes.ResolveNow(
                line.Id,
                line.Verb,
                RecordKinds.GoalResolved,
                new Resolution(Completed, "already facing it", new JsonObject { ["turned"] = 0d }));
            return VerbResult.Handled;
        }
        return StartMove(
            line,
            delta > 0d ? PluginMoveDirection.TurnRight : PluginMoveDirection.TurnLeft,
            PluginMovePace.Run,
            new Amount((float)Math.Abs(delta), PluginMoveUnit.MetersOrDegrees),
            aim);
    }

    /// <summary>
    /// Asks the client to walk to an object, named by id, by name among the
    /// objects around the character, or as <c>target</c> for the selection, and
    /// resolves from the client's own report of how the walk ended.
    /// </summary>
    private VerbResult GoTo(CommandLine line)
    {
        const string usage = "usage: go to <object id, name, or 'target'> [within <meters>], or go to <north-south> <east-west> [<elevation>] [within <meters>]";
        string[] words = Words(line);
        int first = 0;
        if (line.Verb == "go")
        {
            if (words.Length == 0 || !words[0].Equals("to", StringComparison.OrdinalIgnoreCase))
                return Refuse(line, usage);
            first = 1;
        }
        int end = words.Length;
        float arrival = DefaultArrivalMeters;
        if (end - first >= 3 && words[end - 2].Equals("within", StringComparison.OrdinalIgnoreCase))
        {
            int index = end - 1;
            if (!TryAmount(words, ref index, turn: false, out Amount within)
                || within.Unit != PluginMoveUnit.MetersOrDegrees
                || !(within.Value > 0f && within.Value <= MaximumArrivalMeters))
            {
                return Refuse(line, $"within must be more than 0 and at most {MaximumArrivalMeters:0} meters");
            }
            arrival = within.Value;
            end -= 2;
        }
        if (end <= first)
            return Refuse(line, usage);
        string named = string.Join(' ', words[first..end]);

        IAutomationSurface automation = _host.Automation;
        PluginNavigationSnapshot self = automation.Navigation.Snapshot;
        if (!self.IsAvailable)
            return Refuse(line, NoOwnPosition);
        if (self.IsPortalSpace)
            return Refuse(line, InPortalSpace);
        if (TryReadPlace(words[first..end], self.Position, out PluginNavigationPosition place))
            return GoToPlace(line, automation.Navigation, self.Position, place, arrival);
        if (!TryFindGoal(automation, self, named, out PluginWorldObject goal, out string problem))
            return Refuse(line, problem);
        if (GoToProblem(self, goal) is { } refusal)
            return Refuse(line, refusal);
        double distance = Geometry.DistanceMeters(self.Position, goal.Position);

        INavigationAutomation navigation = automation.Navigation;
        PluginNavigationCommandStatus status = navigation.GoTo(goal.ObjectId, arrival);
        if (status != PluginNavigationCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginNavigationCommandStatus.Unavailable
                ? "this client cannot plan a walk for a plugin"
                : "the client did not accept the walk");
        }

        long sequence = navigation.GoToReport.Sequence;
        Accepted(line, new JsonObject
        {
            ["target"] = Facts.Hex(goal.ObjectId),
            ["name"] = goal.Name,
            ["meters"] = Math.Round(distance, 1),
            ["within"] = Math.Round(arrival, 1),
            ["from"] = Coordinates.Describe(self.Position),
        });
        WatchWalk(line, navigation, sequence, distance);
        return VerbResult.Handled;
    }

    /// <summary>Walks to a place given in map coordinates, the way <c>go to</c> walks to an object.</summary>
    private VerbResult GoToPlace(
        CommandLine line,
        INavigationAutomation navigation,
        PluginNavigationPosition from,
        PluginNavigationPosition place,
        float arrival)
    {
        double distance = Geometry.DistanceMeters(from, place);
        if (distance > MaximumGoToMeters)
            return Refuse(line, $"that place is {distance:0} m away, and a walk is planned to at most {MaximumGoToMeters:0} m");
        PluginNavigationCommandStatus status = navigation.GoTo(place, arrival);
        if (status != PluginNavigationCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginNavigationCommandStatus.Unavailable
                ? "this client cannot plan a walk for a plugin"
                : "the client did not accept the walk");
        }

        long sequence = navigation.GoToReport.Sequence;
        Accepted(line, new JsonObject
        {
            ["place"] = Coordinates.DescribePlace(place),
            ["meters"] = Math.Round(distance, 1),
            ["within"] = Math.Round(arrival, 1),
            ["from"] = Coordinates.Describe(from),
        });
        WatchWalk(line, navigation, sequence, distance);
        return VerbResult.Handled;
    }

    /// <summary>
    /// A place written in map coordinates: north-south then east-west as signed numbers, or
    /// as numbers ending N, S, E or W in either order, then an optional elevation in map
    /// units; with no elevation, the character's own.
    /// </summary>
    internal static bool TryReadPlace(IReadOnlyList<string> words, in PluginNavigationPosition self, out PluginNavigationPosition place)
    {
        place = default;
        if (words.Count is < 2 or > 3)
            return false;
        double? northSouth = null;
        double? eastWest = null;
        var plain = new List<double>(3);
        foreach (string word in words)
        {
            string token = word.TrimEnd(',');
            if (token.Length == 0)
                return false;
            char compass = char.ToUpperInvariant(token[^1]);
            if (compass is 'N' or 'S' or 'E' or 'W')
            {
                if (!double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
                    || !double.IsFinite(amount)
                    || amount < 0d)
                {
                    return false;
                }
                double signed = compass is 'S' or 'W' ? -amount : amount;
                if (compass is 'N' or 'S')
                {
                    if (northSouth is not null)
                        return false;
                    northSouth = signed;
                }
                else
                {
                    if (eastWest is not null)
                        return false;
                    eastWest = signed;
                }
            }
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number))
            {
                plain.Add(number);
            }
            else
            {
                return false;
            }
        }
        double? elevation;
        if (northSouth is null && eastWest is null)
        {
            if (plain.Count < 2)
                return false;
            northSouth = plain[0];
            eastWest = plain[1];
            elevation = plain.Count == 3 ? plain[2] : null;
        }
        else
        {
            if (northSouth is null || eastWest is null || plain.Count > 1)
                return false;
            elevation = plain.Count == 1 ? plain[0] : null;
        }
        place = new PluginNavigationPosition(0u, eastWest.Value, northSouth.Value, elevation ?? self.Elevation, 0f, IsOutdoor: true);
        return true;
    }

    /// <summary>Resolves a walk from the client's own report of how it ended.</summary>
    private void WatchWalk(CommandLine line, INavigationAutomation navigation, long sequence, double distance)
    {
        double window = Math.Min(
            MaximumMoveSeconds * 2d,
            GoToPlanningSeconds + (distance / GoToSlowestMetersPerSecond));
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, window, () =>
        {
            PluginGoToReport report = navigation.GoToReport;
            if (report.Sequence != sequence)
                return new Resolution(Cancelled, "a later walk replaced it");
            if (report.State is PluginGoToState.Planning or PluginGoToState.Walking or PluginGoToState.Waiting)
                return null;
            var fields = new JsonObject
            {
                ["remaining"] = float.IsFinite(report.RemainingMeters)
                    ? Math.Round(report.RemainingMeters, 1)
                    : (double?)null,
                ["replans"] = report.Replans,
                ["blockedBy"] = report.BlockedByObjectId != 0u ? Facts.Hex(report.BlockedByObjectId) : null,
            };
            return report.State switch
            {
                PluginGoToState.Arrived => new Resolution(Completed, ArrivalNote(report.Reason), fields),
                PluginGoToState.NoRoute => new Resolution(NoRoute, report.Reason, fields),
                PluginGoToState.Blocked => new Resolution(Blocked, report.Reason, fields),
                PluginGoToState.Stopped => new Resolution(Cancelled, report.Reason, fields),
                PluginGoToState.Interrupted => new Resolution(Cancelled, "the player moved the character", fields),
                PluginGoToState.Lost => new Resolution(OutcomeCorrelator.Lost, report.Reason, fields),
                _ => new Resolution(OutcomeCorrelator.Unconfirmed, "the client lost track of the walk", fields),
            };
        },
        waiting: () => navigation.GoToReport is { State: PluginGoToState.Waiting } waiting && waiting.Sequence == sequence);
    }

    /// <summary>
    /// What the client said of an arrival beyond having arrived, such as a route
    /// that ends short of the object or without a line of sight to it.
    /// </summary>
    private static string? ArrivalNote(string? reason) =>
        string.IsNullOrEmpty(reason) || reason.Equals("arrived", StringComparison.Ordinal) ? null : reason;

    /// <summary>Why facing an object would be refused, or null when it would be taken.</summary>
    internal static string? FaceProblem(in PluginNavigationSnapshot self, in PluginWorldObject value) =>
        !value.HasPosition ? NoObjectPosition : !self.IsAvailable ? NoOwnPosition : null;

    /// <summary>Why a walk to an object would be refused, or null when it would be taken.</summary>
    internal static string? GoToProblem(in PluginNavigationSnapshot self, in PluginWorldObject goal)
    {
        if (!self.IsAvailable)
            return NoOwnPosition;
        if (self.IsPortalSpace)
            return InPortalSpace;
        if (!goal.HasPosition)
            return NoObjectPosition;
        double distance = Geometry.DistanceMeters(self.Position, goal.Position);
        return distance > MaximumGoToMeters
            ? $"{goal.Name} is {distance:0} m away, and a walk is planned to at most {MaximumGoToMeters:0} m"
            : null;
    }

    private bool TryFindGoal(
        IAutomationSurface automation,
        PluginNavigationSnapshot self,
        string named,
        out PluginWorldObject goal,
        out string problem)
    {
        uint id;
        if (named.Equals("target", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.Selection.SelectedObjectId is not { } selected)
            {
                goal = default;
                problem = "nothing is targeted";
                return false;
            }
            id = selected;
        }
        else if (!Guids.TryParse(named, out id))
        {
            foreach (PlacedObject placed in WorldQuery.Around(automation, self, null))
            {
                if (placed.Value.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
                {
                    goal = placed.Value;
                    problem = string.Empty;
                    return true;
                }
            }
            goal = default;
            problem = $"nothing called '{named}' is nearby";
            return false;
        }

        if (!automation.Objects.TryGet(id, out goal) || !goal.HasPosition)
        {
            problem = NoObjectPosition;
            return false;
        }
        problem = string.Empty;
        return true;
    }

    /// <summary>Asks the client for a move and resolves it from the client's own report of how it ended.</summary>
    private VerbResult StartMove(
        CommandLine line,
        PluginMoveDirection direction,
        PluginMovePace pace,
        Amount amount,
        JsonObject? aim = null)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable)
            return Refuse(line, NoOwnPosition);
        if (self.IsPortalSpace)
            return Refuse(line, InPortalSpace);

        PluginNavigationCommandStatus status = navigation.Move(direction, pace, amount.Value, amount.Unit);
        if (status != PluginNavigationCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginNavigationCommandStatus.Unavailable
                ? "this client cannot move the character for a plugin"
                : "the client did not accept the move");
        }

        PluginMoveChannel channel = PluginMoveReport.ChannelOf(direction);
        long sequence = navigation.MoveReport[channel].Sequence;
        bool turn = channel == PluginMoveChannel.Turn;
        bool openEnded = amount.Value <= 0f;
        bool timed = !openEnded && amount.Unit == PluginMoveUnit.Seconds;
        JsonObject details = aim ?? new JsonObject();
        details["direction"] = DirectionWord(direction);
        details["pace"] = turn ? null : WireNames.Kebab(pace.ToString());
        details[turn ? "degrees" : "meters"] = openEnded || timed ? null : Math.Round(amount.Value, 1);
        details["seconds"] = timed ? Math.Round(amount.Value, 1) : null;
        details["from"] = Coordinates.Describe(self.Position);
        Accepted(line, details);

        double window = (openEnded
                ? OpenEndedSeconds
                : timed
                    ? amount.Value
                    : (amount.Value / (turn ? SlowestDegreesPerSecond : SlowestMetersPerSecond)) + 3d)
            + MoveWindowSlackSeconds;
        string measure = turn ? "turned" : "travelled";
        string kind = ChannelWord(channel);
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, window, () =>
        {
            PluginMoveProgress progress = navigation.MoveReport[channel];
            if (progress.Sequence != sequence)
                return new Resolution(Cancelled, $"a later {kind} replaced it");
            if (progress.State == PluginMoveState.Moving)
                return null;
            var fields = new JsonObject
            {
                [measure] = Math.Round(progress.Covered, 1),
                ["seconds"] = Math.Round(progress.ElapsedSeconds, 1),
            };
            return progress.State switch
            {
                PluginMoveState.Completed => new Resolution(Completed, null, fields),
                PluginMoveState.Stopped => openEnded
                    ? new Resolution(Completed, "a 'stop' ended it", fields)
                    : new Resolution(Cancelled, "a 'stop' ended it short", fields),
                PluginMoveState.TimeLimit => openEnded
                    ? new Resolution(Completed, "the client stopped it at its time limit; give a time such as 60s, or 'stop' sooner", fields)
                    : new Resolution(OutcomeCorrelator.Unconfirmed, "the client did not cover the amount in time", fields),
                PluginMoveState.Blocked => new Resolution(Blocked, "the character stopped making progress", fields),
                PluginMoveState.Interrupted => new Resolution(Cancelled, "the player moved the character", fields),
                PluginMoveState.Lost => new Resolution(OutcomeCorrelator.Lost, "the character entered portal space or left the world", fields),
                _ => new Resolution(OutcomeCorrelator.Unconfirmed, "the client lost track of the move", fields),
            };
        });
        return VerbResult.Handled;
    }

    private VerbResult Jump(CommandLine line)
    {
        float power = DefaultJumpPower;
        if (line.Arguments.Length > 0
            && (!TryNumber(line.Arguments, out power) || !(power > 0f && power <= 1f)))
        {
            return Refuse(line, "usage: jump [power above 0 and at most 1]");
        }
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationCommandStatus status = navigation.Jump(power);
        if (status != PluginNavigationCommandStatus.Accepted)
        {
            return Refuse(line, status == PluginNavigationCommandStatus.Unavailable
                ? "this client cannot jump for a plugin"
                : "the client did not accept the jump; one may already be charging");
        }

        long jump = navigation.MoveReport.JumpSequence;
        Accepted(line, new JsonObject { ["power"] = Math.Round(power, 2) });
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, power + JumpWindowSeconds, () =>
        {
            PluginMoveReport report = navigation.MoveReport;
            if (report.JumpSequence != jump)
                return new Resolution(Cancelled, "a later jump replaced it");
            return !report.JumpCharging && navigation.Snapshot.IsAirborne
                ? new Resolution(Completed)
                : null;
        });
        return VerbResult.Handled;
    }

    private VerbResult Stop(CommandLine line)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        string[] words = Words(line);
        PluginNavigationCommandStatus status;
        string stopped;
        if (words.Length == 0)
        {
            status = navigation.StopMoving();
            stopped = "all";
        }
        else if (words.Length == 1 && StoppedKind(words[0]) is { } channel)
        {
            status = navigation.StopMoving(channel);
            stopped = ChannelWord(channel);
        }
        else
        {
            return Refuse(line, "usage: stop, or stop walking|running|strafing|turning");
        }
        navigation.StopGoTo();
        if (status != PluginNavigationCommandStatus.Accepted)
            return Refuse(line, "the client did not accept the stop");
        Accepted(line, new JsonObject { ["stopped"] = stopped });
        _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.GoalResolved, new Resolution(Completed));
        return VerbResult.Handled;
    }

    /// <summary>
    /// <c>stance combat</c> enters the stance the wielded weapon or held caster
    /// calls for, as the client's own combat key does, and <c>stance peace</c>
    /// leaves it. A stance is not named, because the weapon decides it.
    /// </summary>
    private VerbResult Stance(CommandLine line)
    {
        ICombatAutomation combat = _host.Automation.Combat;
        switch (line.Arguments.Trim().ToLowerInvariant())
        {
            case "combat":
                return EnterStance(line, combat, combat.EnterDefaultMode(), "combat", InCombat);
            case "peace":
                return EnterStance(line, combat, combat.EnterMode(PluginCombatMode.Peace), "peace",
                    static mode => mode == PluginCombatMode.Peace);
            case "melee" or "missile" or "magic":
                return Refuse(line,
                    "a stance is not named; 'stance combat' enters the one the wielded weapon or held caster calls for");
            default:
                return Refuse(line, "usage: stance combat|peace");
        }
    }

    private VerbResult EnterStance(
        CommandLine line,
        ICombatAutomation combat,
        PluginCombatCommandResult result,
        string stance,
        Func<PluginCombatMode, bool> reached)
    {
        if (!result.Accepted)
        {
            return Refuse(line, result.Notice
                ?? $"the client did not change stance ({WireNames.Kebab(result.Status.ToString())})");
        }
        Accepted(line, new JsonObject { ["stance"] = stance });
        if (result.Status == PluginCombatCommandStatus.AlreadyReady)
        {
            _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.GoalResolved,
                new Resolution(Completed, "the character was already in that stance", StanceFields(combat.Snapshot.Mode)));
            return VerbResult.Handled;
        }
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.GoalResolved, StanceWindowSeconds, () =>
        {
            PluginCombatMode mode = combat.Snapshot.Mode;
            return reached(mode) ? new Resolution(Completed, null, StanceFields(mode)) : null;
        });
        return VerbResult.Handled;
    }

    private static bool InCombat(PluginCombatMode mode) =>
        mode is PluginCombatMode.Melee or PluginCombatMode.Missile or PluginCombatMode.Magic;

    private static JsonObject StanceFields(PluginCombatMode mode) =>
        new() { ["mode"] = WireNames.Kebab(mode.ToString()) };

    private VerbResult Cancel(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        automation.Navigation.StopGoTo();
        automation.Navigation.StopMoving();
        automation.Navigation.ClearMovementIntent();
        automation.Combat.AbortPhysicalAttack();
        int ended = _outcomes.EndAll(
            RecordKinds.GoalResolved,
            Cancelled,
            "a later 'cancel' stopped it");
        Accepted(line, new JsonObject { ["cancelled"] = ended });
        _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.GoalResolved, new Resolution(Completed));
        return VerbResult.Handled;
    }

    /// <summary>
    /// Reads an amount starting at <paramref name="index"/>: a number of meters,
    /// or degrees for a turn, or seconds, with its unit attached as in
    /// <c>20s</c> or as the next word. Leaves <paramref name="index"/> on the last
    /// word it read.
    /// </summary>
    private static bool TryAmount(string[] words, ref int index, bool turn, out Amount amount)
    {
        amount = default;
        string word = words[index];
        if (TryNumber(word, out float value))
        {
            PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees;
            if (index + 1 < words.Length && UnitWord(words[index + 1], turn) is { } named)
            {
                unit = named;
                index++;
            }
            amount = new Amount(value, unit);
            return true;
        }
        int split = word.Length;
        while (split > 0 && char.IsLetter(word[split - 1]))
            split--;
        if (split > 0
            && split < word.Length
            && TryNumber(word[..split], out value)
            && UnitWord(word[split..], turn) is { } attached)
        {
            amount = new Amount(value, attached);
            return true;
        }
        return false;
    }

    private static PluginMoveUnit? UnitWord(string word, bool turn) => word.ToLowerInvariant() switch
    {
        "s" or "sec" or "secs" or "second" or "seconds" => PluginMoveUnit.Seconds,
        "m" or "meter" or "meters" or "metre" or "metres" when !turn => PluginMoveUnit.MetersOrDegrees,
        "deg" or "degree" or "degrees" when turn => PluginMoveUnit.MetersOrDegrees,
        _ => null,
    };

    private static string? Problem(Amount amount, bool turn)
    {
        if (amount.Unit == PluginMoveUnit.Seconds)
        {
            return amount.Value > 0f && amount.Value <= MaximumMoveSeconds
                ? null
                : $"a time must be more than 0 and at most {MaximumMoveSeconds:0} seconds";
        }
        if (turn)
        {
            return amount.Value > 0f && amount.Value <= MaximumTurnDegrees
                ? null
                : $"turn left or right by more than 0 and at most {MaximumTurnDegrees:0} degrees";
        }
        return amount.Value > 0f && amount.Value <= MaximumMoveMeters
            ? null
            : $"a distance must be more than 0 and at most {MaximumMoveMeters:0} meters";
    }

    private static PluginMoveDirection? Straight(string word) => word.ToLowerInvariant() switch
    {
        "forward" or "forwards" => PluginMoveDirection.Forward,
        "backward" or "backwards" or "back" => PluginMoveDirection.Backward,
        _ => null,
    };

    private static PluginMoveDirection? Sideways(string word) => word.ToLowerInvariant() switch
    {
        "left" => PluginMoveDirection.StrafeLeft,
        "right" => PluginMoveDirection.StrafeRight,
        _ => null,
    };

    private static PluginMoveDirection? TurnSide(string word) => word.ToLowerInvariant() switch
    {
        "left" => PluginMoveDirection.TurnLeft,
        "right" => PluginMoveDirection.TurnRight,
        _ => null,
    };

    private static PluginMoveChannel? StoppedKind(string word) => word.ToLowerInvariant() switch
    {
        "walk" or "walking" or "run" or "running" => PluginMoveChannel.Travel,
        "strafe" or "strafing" => PluginMoveChannel.Strafe,
        "turn" or "turning" => PluginMoveChannel.Turn,
        _ => null,
    };

    private static string ChannelWord(PluginMoveChannel channel) => channel switch
    {
        PluginMoveChannel.Travel => "walk or run",
        PluginMoveChannel.Strafe => "strafe",
        _ => "turn",
    };

    private static string DirectionWord(PluginMoveDirection direction) => direction switch
    {
        PluginMoveDirection.Forward => "forward",
        PluginMoveDirection.Backward => "backward",
        PluginMoveDirection.StrafeLeft or PluginMoveDirection.TurnLeft => "left",
        _ => "right",
    };

    private static string[] Words(CommandLine line) =>
        line.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool TryNumber(string word, out float value) =>
        float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && float.IsFinite(value);

    private static double Normalize(double heading) => ((heading % 360d) + 360d) % 360d;

    private void Accepted(CommandLine line, JsonObject details)
    {
        var fields = new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
        };
        foreach (KeyValuePair<string, JsonNode?> detail in details.ToList())
        {
            details.Remove(detail.Key);
            fields[detail.Key] = detail.Value;
        }
        _publisher.Publish(RecordKinds.GoalAccepted, fields);
    }

    private VerbResult Refuse(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.GoalRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }

    /// <summary>A move's amount: meters or degrees, or seconds; zero keeps going until stopped.</summary>
    private readonly record struct Amount(float Value, PluginMoveUnit Unit)
    {
        internal static Amount None => new(0f, PluginMoveUnit.MetersOrDegrees);
    }
}
