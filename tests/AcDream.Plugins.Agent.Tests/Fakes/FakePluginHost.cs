using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.Agent.Tests.Fakes;

internal sealed class FakePluginHost : IPluginHost
{
    internal FakePluginHost(FakeAutomation? automation = null)
    {
        FakeAutomation = automation ?? new FakeAutomation();
    }

    public bool HasUi => false;
    public IPluginLogger Log => FakeLog;
    public IGameState State => FakeState;
    public IEvents Events => FakeEvents;
    public ISelectionService Selection => FakeSelection;
    public IUiRegistry Ui => NoOpUiRegistry.Instance;
    public IPluginCommandRegistry Commands => FakeCommands;
    public IPluginStorage Storage => FakeStorage;
    public IAutomationSurface Automation => FakeAutomation;

    internal FakeLogger FakeLog { get; } = new();
    internal FakeGameState FakeState { get; } = new();
    internal FakeEvents FakeEvents { get; } = new();
    internal FakeSelection FakeSelection { get; } = new();
    internal FakeCommands FakeCommands { get; } = new();
    internal FakeStorage FakeStorage { get; } = new();
    internal FakeAutomation FakeAutomation { get; }
}

internal sealed class FakeLogger : IPluginLogger
{
    internal List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add(message);
    public void Warn(string message) => Lines.Add(message);
    public void Error(string message, Exception? exception = null) =>
        Lines.Add(message);
}

internal sealed class FakeGameState : IGameState
{
    public IReadOnlyList<WorldEntitySnapshot> Entities => EntityList;

    internal List<WorldEntitySnapshot> EntityList { get; } = [];
}

internal sealed class FakeEvents : IEvents
{
    public event Action<WorldEntitySnapshot>? EntitySpawned;
    public event Action<double>? Tick;

    internal int TickSubscriberCount =>
        Tick?.GetInvocationList().Length ?? 0;

    internal void FireTick(double elapsedSeconds) =>
        Tick?.Invoke(elapsedSeconds);

    internal void Spawn(WorldEntitySnapshot entity) =>
        EntitySpawned?.Invoke(entity);
}

internal sealed class FakeSelection : ISelectionService
{
    public uint? SelectedObjectId { get; private set; }
    public uint? PreviousObjectId { get; private set; }
    public event Action<SelectionChangedEvent>? Changed;

    internal bool Accepts { get; set; } = true;

    public bool Select(uint objectId)
    {
        if (!Accepts)
            return false;
        PreviousObjectId = SelectedObjectId;
        SelectedObjectId = objectId;
        Changed?.Invoke(new(PreviousObjectId, SelectedObjectId));
        return true;
    }

    public bool Clear()
    {
        if (SelectedObjectId is null)
            return false;
        PreviousObjectId = SelectedObjectId;
        SelectedObjectId = null;
        Changed?.Invoke(new(PreviousObjectId, null));
        return true;
    }
}

internal sealed class FakeCommands : IPluginCommandRegistry
{
    private readonly Dictionary<string, Action<PluginCommand>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyCollection<string> Verbs => _handlers.Keys;

    public IDisposable Register(string verb, Action<PluginCommand> handler)
    {
        _handlers.Add(verb, handler);
        return new Registration(() => _handlers.Remove(verb));
    }

    /// <summary>Runs a chat line the way the host's command registry does.</summary>
    internal bool Run(string line)
    {
        string trimmed = line.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t'], 1);
        string verb = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        if (!_handlers.TryGetValue(verb, out Action<PluginCommand>? handler))
            return false;
        string arguments = separator < 0
            ? string.Empty
            : trimmed[(separator + 1)..].Trim();
        handler(new PluginCommand(verb, arguments, trimmed));
        return true;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeStorage : IPluginStorage
{
    internal Dictionary<string, string> Files { get; } = [];

    public bool IsAvailable => true;
    public string? ReadText(string key) =>
        Files.TryGetValue(key, out string? content) ? content : null;
    public IReadOnlyList<string> List(string prefix) =>
        Files.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    public void WriteText(string key, string content) => Files[key] = content;
    public bool Delete(string key) => Files.Remove(key);
}
