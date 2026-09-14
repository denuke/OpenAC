using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// <c>use &lt;id&gt;</c>, <c>use &lt;item id&gt; on &lt;id&gt;</c> and <c>open &lt;id&gt;</c>.
/// A carried item is used as an item; anything else is used as an object in
/// the world, the way a player double-clicks it.
/// </summary>
internal sealed class ObjectVerbs : IVerbFamily
{
    internal const double WindowSeconds = 10d;
    internal const string Completed = "completed";
    internal const string Refused = "refused";

    internal static readonly IReadOnlyList<string> OutcomeWords = [Completed, Refused];

    private readonly IPluginHost _host;
    private readonly Publisher _publisher;
    private readonly OutcomeCorrelator _outcomes;

    internal ObjectVerbs(IPluginHost host, Publisher publisher, OutcomeCorrelator outcomes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outcomes);
        _host = host;
        _publisher = publisher;
        _outcomes = outcomes;
    }

    public IReadOnlyCollection<string> ReservedWords { get; } = ["use", "open", "close"];

    public VerbResult Handle(CommandLine line)
    {
        if (!_host.Automation.IsAvailable)
            return Refuse(line, "no character is in the world");
        return line.Verb switch
        {
            "use" => Use(line),
            "open" => Open(line),
            _ => Refuse(line, "closing a container is not available; moving away from it closes it"),
        };
    }

    private VerbResult Use(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        (string subject, uint? target) = SplitOn(line.Arguments);
        if (!Guids.TryParse(subject, out uint id))
            return Refuse(line, "use needs an object id such as 0x70000001, optionally followed by 'on <object id>'");

        bool carried = automation.Items.CaptureOwnedItems().Any(item => item.ObjectId == id);
        long baseline = automation.Items.LastCompletion.Revision;
        uint vendorBefore = automation.Items.ActiveVendorObjectId;
        uint containerBefore = automation.Loot.CurrentContainerId;

        PluginItemCommandResult result;
        string path;
        if (target is { } onto)
        {
            if (!carried)
                return Refuse(line, "only a carried item can be used on something");
            result = automation.Items.Apply(id, onto);
            path = "item-on-target";
        }
        else if (carried)
        {
            result = automation.Items.Use(id);
            path = "item";
        }
        else
        {
            if (!automation.Objects.TryGet(id, out _))
                return Refuse(line, "the client holds no object with that id");
            result = automation.Objects.Use(id);
            path = "world-object";
        }
        if (!result.Accepted)
            return RefuseStatus(line, result);

        Action(line, id, target, path);
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.ObjectOutcome, WindowSeconds, () =>
        {
            if (UseAnswered(automation, baseline, id) is { } answered)
                return answered;
            if (target is null && vendorBefore != id && automation.Items.ActiveVendorObjectId == id)
                return new Resolution(Completed, "the vendor opened");
            if (target is null && containerBefore != id && automation.Loot.CurrentContainerId == id)
                return new Resolution(Completed, "the container opened");
            return null;
        });
        return VerbResult.Handled;
    }

    private VerbResult Open(CommandLine line)
    {
        IAutomationSurface automation = _host.Automation;
        if (!Guids.TryParse(line.Arguments, out uint id))
            return Refuse(line, "open needs a container id such as 0x70000001");
        if (automation.Objects.TryGet(id, out PluginWorldObject value) && OpenProblem(value) is { } problem)
            return Refuse(line, problem);
        long baseline = automation.Items.LastCompletion.Revision;
        PluginItemCommandResult result = automation.Loot.Open(id);
        if (!result.Accepted)
            return RefuseStatus(line, result);

        Action(line, id, null, "container");
        _outcomes.Watch(line.Id, line.Verb, RecordKinds.ObjectOutcome, WindowSeconds, () =>
            automation.Loot.CurrentContainerId == id
                ? new Resolution(Completed, "the container opened")
                : UseAnswered(automation, baseline, id) is { Word: Refused } refused
                    ? refused
                    : null);
        return VerbResult.Handled;
    }

    /// <summary>Why using an object in the world would be refused, or null when it would be taken.</summary>
    internal static string? WorldUseProblem(in PluginNavigationSnapshot self, in PluginWorldObject value) =>
        self.IsAvailable && value.ObjectId == self.LocalObjectId ? "the character cannot use itself" : null;

    /// <summary>Why opening an object would be refused, or null when it would be taken.</summary>
    internal static string? OpenProblem(in PluginWorldObject value) =>
        value.IsOpenable ? null : "it does not open the way a corpse or a chest does; 'use' is for anything else";

    private static Resolution? UseAnswered(IAutomationSurface automation, long baseline, uint id)
    {
        PluginItemUseCompletion completion = automation.Items.LastCompletion;
        if (completion.Revision <= baseline || completion.SourceObjectId != id)
            return null;
        var fields = new JsonObject { ["weenieError"] = completion.WeenieError };
        return completion.WeenieError == 0u
            ? new Resolution(Completed, null, fields)
            : new Resolution(Refused,
                $"the server refused it with error 0x{completion.WeenieError:X4}",
                fields);
    }

    private static (string Subject, uint? Target) SplitOn(string arguments)
    {
        string trimmed = arguments.Trim();
        int index = trimmed.LastIndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (index > 0 && Guids.TryParse(trimmed[(index + 4)..], out uint target))
            return (trimmed[..index].Trim(), target);
        return (trimmed, null);
    }

    private void Action(CommandLine line, uint id, uint? target, string path) =>
        _publisher.Publish(RecordKinds.ObjectAction, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["guid"] = Facts.Hex(id),
            ["target"] = target is { } onto ? Facts.Hex(onto) : null,
            ["path"] = path,
        });

    private VerbResult RefuseStatus(CommandLine line, PluginItemCommandResult result) =>
        Refuse(line,
            result.Notice ?? $"the client did not do it ({WireNames.Kebab(result.Status.ToString())})",
            WireNames.Kebab(result.Status.ToString()));

    private VerbResult Refuse(CommandLine line, string reason, string? word = null)
    {
        _publisher.Publish(RecordKinds.ObjectRefused, new JsonObject
        {
            ["id"] = line.Id,
            ["verb"] = line.Verb,
            ["line"] = line.Text,
            ["word"] = word,
            ["reason"] = reason,
        });
        return VerbResult.Refused(reason);
    }
}
