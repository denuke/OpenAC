using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary><c>target &lt;guid&gt;</c>, <c>target nearest [kind]</c> and <c>untarget</c>.</summary>
internal sealed class TargetVerbs : IVerbFamily
{
    internal const double WindowSeconds = 2d;
    internal const string Completed = "completed";
    internal const string NoSuchObject = "the client holds no object with that id";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Completed];

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal TargetVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["target", "untarget"];

    public VerbResult Handle(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        ISelectionService selection = _host.Selection;

        if (line.Verb == "untarget")
        {
            if (selection.SelectedObjectId is null)
                return Refuse(line, "nothing is selected");
            if (!selection.Clear())
                return Refuse(line, "the client did not clear the selection");
            Sent(line, null, null);
            _outcomes.Watch(line.Id, line.Verb, RecordKinds.TargetOutcome, WindowSeconds,
                () => selection.SelectedObjectId is null ? new Resolution(Completed) : null);
            return VerbResult.Handled;
        }

        uint id;
        string name;
        if (line.Arguments.StartsWith("nearest", StringComparison.OrdinalIgnoreCase))
        {
            string kind = line.Arguments["nearest".Length..].Trim().ToLowerInvariant();
            if (kind.Length > 0 && !EntityKinds.IsWord(kind))
                return Refuse(line, $"'{kind}' is not a kind word");
            PluginNavigationSnapshot self = automation.Navigation.Snapshot;
            if (!self.IsAvailable)
                return Refuse(line, "the client has no position for its own body");
            PlacedObject? nearest = null;
            foreach (PlacedObject placed in WorldQuery.Around(automation, self, null))
            {
                if (kind.Length == 0 || placed.Word == kind)
                {
                    nearest = placed;
                    break;
                }
            }
            if (nearest is not { } found)
                return Refuse(line, kind.Length == 0 ? "nothing is nearby" : $"no {kind} is nearby");
            id = found.Value.ObjectId;
            name = found.Value.Name;
        }
        else
        {
            if (!Guids.TryParse(line.Arguments, out id))
                return Refuse(line, "target needs an object id, or 'nearest' and a kind word");
            if (!automation.Objects.TryGet(id, out PluginWorldObject value))
                return Refuse(line, NoSuchObject);
            name = value.Name;
        }

        if (selection.SelectedObjectId == id)
        {
            Sent(line, id, name);
            _outcomes.ResolveNow(line.Id, line.Verb, RecordKinds.TargetOutcome,
                new Resolution(Completed, "it was already targeted"));
            return VerbResult.Handled;
        }
        if (!selection.Select(id))
            return Refuse(line, "the client did not accept the selection");
        Sent(line, id, name);
        uint expected = id;
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.TargetOutcome, WindowSeconds,
            () => selection.SelectedObjectId == expected ? new Resolution(Completed) : null);
        return VerbResult.Handled;
    }

    /// <summary>Why targeting an object would be refused, or null when it would be taken.</summary>
    internal static string? TargetProblem(IAutomationSurface automation, uint id) =>
        automation.Objects.TryGet(id, out _) ? null : NoSuchObject;

    private void Sent(CommandLine line, uint? id, string? name) =>
        _publisher.Publish(RecordKinds.TargetSent, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = id is { } value ? Facts.Hex(value) : null,
            ["name"] = name,
        });

    private VerbResult Refuse(CommandLine line, string reason)
    {
        _publisher.Publish(RecordKinds.TargetRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
