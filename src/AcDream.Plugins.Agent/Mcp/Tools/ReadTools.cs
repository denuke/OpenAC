using System.Globalization;
using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Mcp.Tools;

/// <summary>The command line a read tool runs, or why its arguments were refused.</summary>
internal readonly record struct ReadLine(string? Text, string? Error)
{
    internal static ReadLine Of(string text) => new(text, null);

    internal static ReadLine Refuse(string error) => new(null, error);
}

/// <summary>
/// A tool that runs one read verb and returns the record it produced. A read
/// never makes the character act.
/// </summary>
internal sealed class ReadTool : IMcpTool
{
    private readonly AgentContext _context;
    private readonly string _title;
    private readonly string _description;
    private readonly Func<JsonObject> _properties;
    private readonly IReadOnlyList<string> _required;
    private readonly Func<JsonObject, ReadLine> _line;

    internal ReadTool(
        AgentContext context,
        string name,
        string title,
        string description,
        Func<JsonObject> properties,
        IReadOnlyList<string> required,
        Func<JsonObject, ReadLine> line)
    {
        _context = context;
        Name = name;
        _title = title;
        _description = description;
        _properties = properties;
        _required = required;
        _line = line;
    }

    public string Name { get; }

    public JsonObject Definition() =>
        ToolDefinitions.Make(Name, _title, _description, _properties(), _required, readOnly: true);

    public IMcpToolRun Start(JsonObject arguments, McpSession session)
    {
        ReadLine line = _line(arguments);
        if (line.Error is { } error)
            return new FinishedRun(ToolResults.Error(error));
        return new FinishedRun(ToolResults.Of(ToolLines.Deliver(_context, line.Text!).ReadResult()));
    }
}

internal static class ReadTools
{
    internal const int MaximumSearchLength = 64;

    private const string Nothing = " Sends nothing.";

    internal static IEnumerable<IMcpTool> Create(AgentContext context) =>
    [
        new ReadTool(context, "nearby", "What is around me?",
            "Objects in the world around the character, nearest first, with distance in meters, "
            + "compass bearing, how far to turn to face each, and what kind of thing it is. Counts every "
            + "kind in range and anything unclassified, so a short list never reads as an empty world."
            + Nothing,
            () => new JsonObject
            {
                ["kind"] = Property("string", "One kind word such as monster, vendor, npc, corpse, portal, door or player."),
                ["range"] = Property("number", "Maximum distance in meters."),
            },
            [],
            Nearby),
        new ReadTool(context, "inspect", "What is this thing?",
            "Everything the client holds about one object, by id: its kind, whether it is carried, "
            + "where it is and how far, whether it has been appraised, and its properties. An id the "
            + "client does not hold is refused, never guessed."
            + Nothing,
            () => new JsonObject { ["guid"] = Property("string", "An object id such as 0x70000001.") },
            ["guid"],
            Inspect),
        new ReadTool(context, "spells", "Which spells do I know?",
            "The spells the character knows: id, name, school, tier, difficulty and mana cost, whether "
            + "each is cast on self, on a target or on nothing, whether it helps or harms, and whether the "
            + "character carries its components. A trained caster knows hundreds, so pass search to "
            + "narrow by name. Cast one through act, for example 'cast Strength Self VI'."
            + Nothing,
            () => new JsonObject { ["search"] = Property("string", "Part of a spell name, such as strength or bolt.") },
            [],
            arguments => Searched("spells", arguments)),
        new ReadTool(context, "skills", "What are my skills?",
            "The character's skills: whether each is untrained, trained or specialized, with current "
            + "and base values."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("skills")),
        new ReadTool(context, "buffs", "What is enchanting me?",
            "The enchantments on the character: spell, family, tier and seconds remaining."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("buffs")),
        new ReadTool(context, "inventory", "What am I carrying?",
            "Everything the character carries: id, name, kind, stack size, value, burden, which pack "
            + "holds it and whether it is equipped, with the free main-pack slots. Pass search to narrow "
            + "by name. Use the ids through act, for example 'use 0x50000A01' or 'sell 0x50000A01 5'."
            + Nothing,
            () => new JsonObject { ["search"] = Property("string", "Part of an item name, such as scarab or healing kit.") },
            [],
            arguments => Searched("inventory", arguments)),
        new ReadTool(context, "equipment", "What am I wearing?",
            "What the character wears and wields, and the carried items that could be equipped, with "
            + "ids for 'equip' and 'unequip' through act."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("equipment")),
        new ReadTool(context, "vendor", "What does this vendor sell?",
            "The open vendor's listings: id, name, unit price and stock. When no vendor is open the "
            + "listings are unknown, not an empty shop; 'use <vendor id>' through act opens one. Buy "
            + "through act, for example 'buy prismatic taper 10'."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("vendor")),
        new ReadTool(context, "container", "What is in the open container?",
            "The items in the open corpse or container, with ids to take through act as "
            + "'loot <item id>'. 'open <corpse id>' through act opens one."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("loot list")),
        new ReadTool(context, "corpses", "Which corpses are near?",
            "Corpses within range, nearest first, with whether each has been opened already."
            + Nothing,
            () => new JsonObject { ["range"] = Property("number", "Maximum distance in meters; 30 when omitted.") },
            [],
            Corpses),
        new ReadTool(context, "characters", "Who can I play?",
            "The characters on the account while the client is at the character list, with each one's id and "
            + "whether it can enter the world, and where the client stands: not-connected, connecting, "
            + "choosing-character, entering-world or in-world. Act 'login <name>' to enter the world as one, "
            + "and 'logout' to come back to the list."
            + Nothing,
            () => new JsonObject(),
            [],
            _ => ReadLine.Of("characters")),
    ];

    private static JsonObject Property(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static ReadLine Nearby(JsonObject arguments)
    {
        JsonNode? kindNode = arguments["kind"];
        string? kind = JsonRpc.Text(kindNode)?.Trim();
        if (kindNode is not null
            && (kind is null || kind.Length == 0 || kind.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))))
        {
            return ReadLine.Refuse("kind must be one kind word, such as monster or vendor");
        }
        if (!Range(arguments, out double? range))
            return ReadLine.Refuse("range must be a positive number of meters");
        string line = "nearby";
        if (kind is not null)
            line += " " + kind;
        if (range is { } meters)
            line += " " + meters.ToString(CultureInfo.InvariantCulture);
        return ReadLine.Of(line);
    }

    private static ReadLine Inspect(JsonObject arguments)
    {
        string? guid = ToolArguments.Text(arguments, "guid")?.Trim()
            ?? ToolArguments.Whole(arguments, "guid")?.ToString(CultureInfo.InvariantCulture);
        return guid is not null && Guids.TryParse(guid, out _)
            ? ReadLine.Of("inspect " + guid)
            : ReadLine.Refuse("guid must be an object id such as 0x70000001");
    }

    private static ReadLine Corpses(JsonObject arguments) =>
        !Range(arguments, out double? range)
            ? ReadLine.Refuse("range must be a positive number of meters")
            : ReadLine.Of(range is { } meters
                ? "loot corpses " + meters.ToString(CultureInfo.InvariantCulture)
                : "loot corpses");

    private static ReadLine Searched(string verb, JsonObject arguments)
    {
        if (arguments["search"] is not { } node)
            return ReadLine.Of(verb);
        string? search = JsonRpc.Text(node)?.Trim();
        if (search is null || search.Length > MaximumSearchLength || search.Any(char.IsControl))
        {
            return ReadLine.Refuse(
                $"search must be part of a name, at most {MaximumSearchLength} characters on one line");
        }
        return ReadLine.Of(search.Length == 0 ? verb : verb + " " + search);
    }

    private static bool Range(JsonObject arguments, out double? range) =>
        ToolArguments.TryNumber(arguments, "range", out range) && (range is null || range > 0d);
}
