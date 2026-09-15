using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank;

/// <summary>
/// MossTank's settings as other plugins, such as an agent, read and change them:
/// whether the macro runs and what it is doing, every option, the monster rules,
/// the items and consumables, the extra and blacklisted buffs, and which profiles
/// are loaded.
/// </summary>
internal sealed partial class MossTankPanel
{
    internal static readonly IReadOnlyList<string> SharedSections =
        ["macro", "options", "monsters", "items", "buffs", "profiles", "loot", "route"];

    private static string ActionWords => string.Join(
        ", ",
        Enum.GetNames<MonsterActionFlags>().Where(static name => name != nameof(MonsterActionFlags.None)));

    private static string DamageWords => string.Join(", ", Enum.GetNames<MonsterDamageType>());

    internal static string SharedSettingsHelp =>
        "MossTank's settings come in sections: macro (whether it runs, what it is doing, and the problems in its setup), options (every "
        + "option by name, the advanced ones included), monsters (the monster rules, in order), items (the items "
        + "and consumables it uses), buffs (extra buff spells and blacklisted buff families), profiles, loot and "
        + "route. Change them with one JSON object holding any of the parts below. A change wrong in any part "
        + "changes nothing, and an applied change is saved to the loaded profile as MossTank's own panel saves it. "
        + "\"macro\": {\"running\": true or false} starts or stops the macro. "
        + "\"options\": {\"<option>\": <value>} sets options by the names shown under options, each as true or "
        + "false, a number or text, as it is shown; MossTank keeps values such as ranges within its limits. "
        + "\"monsters\": {\"set\": [<rule>], \"remove\": [\"<name>\"]}, where a rule is {\"name\": \"<monster name or "
        + "expression>\", \"priority\": -1 to 4, \"actions\": [<action>], \"damage\": \"<type>\", "
        + "\"extraVulnerability\": \"<type>\", \"weapon\": \"<carried item name>\", \"offhand\": \"<carried item "
        + "name>\", \"petDamage\": \"<type>\"}. A rule named like one already there changes only the fields given; a "
        + "new rule starts attacking at priority 0, and priority -1 leaves a monster alone. The DEFAULT rule answers "
        + "for monsters no other rule names and cannot be removed. "
        + $"Actions: {ActionWords}. Damage types: {DamageWords}. "
        + "\"items\": {\"add\": [\"<carried item name>\"], \"addNoBuffs\": [...], \"remove\": [...], "
        + "\"addConsumables\": [...], \"removeConsumables\": [...]} adds items from the character's packs to the "
        + "lists, or takes them off. "
        + "\"buffs\": {\"addSpells\": [\"<spell name>\"], \"removeSpells\": [...], \"addBlacklistedFamilies\": [...], "
        + "\"removeBlacklistedFamilies\": [...]}. "
        + "\"route\": {\"enabled\": true or false, \"mode\": \"Circular\", \"Linear\" or \"Once\", \"walkLegs\": true or false, "
        + "\"waypoints\": [{\"point\": {\"northSouth\": <n>, \"eastWest\": <e>, \"elevation\": <z>}}, {\"pause\": <seconds>}, "
        + "{\"chat\": \"<text>\"}]} replaces the route, with points in map coordinates, the way VTank's route files keep them. "
        + "With walkLegs the client's own route planning walks each leg "
        + "to a point, around walls, creatures and doors, and back from wherever a fight left the character, and points "
        + "along a straight stretch are walked in one walk.";

    /// <summary>The settings as a JSON object, or only one section of them; null when there is no such section.</summary>
    internal string? ReadSharedSettings(string? section)
    {
        if (section is null)
        {
            var all = new JsonObject();
            foreach (string name in SharedSections)
                all[name] = SharedSection(name);
            return all.ToJsonString();
        }
        string? known = SharedSections.FirstOrDefault(
            name => name.Equals(section.Trim(), StringComparison.OrdinalIgnoreCase));
        return known is null ? null : new JsonObject { [known] = SharedSection(known) }.ToJsonString();
    }

    /// <summary>
    /// Applies a change given as a JSON object and saves it to the loaded profile.
    /// A change wrong in any part changes nothing, and the answer gives every reason.
    /// </summary>
    internal PluginSettingsChangeResult ChangeSharedSettings(string changeJson)
    {
        JsonObject? change;
        try
        {
            change = JsonNode.Parse(changeJson) as JsonObject;
        }
        catch (JsonException)
        {
            change = null;
        }
        if (change is null)
            return new PluginSettingsChangeResult(false, "Nothing was changed: a change must be one JSON object.");

        var plan = new SharedChange();
        var problems = new List<string>();
        if (change.Count == 0)
            problems.Add("the change names nothing to change");
        foreach ((string part, JsonNode? value) in change)
        {
            switch (part)
            {
                case "macro":
                    PlanSharedMacro(value, plan, problems);
                    break;
                case "options":
                    PlanSharedOptions(value, plan, problems);
                    break;
                case "monsters":
                    PlanSharedMonsters(value, plan, problems);
                    break;
                case "items":
                    PlanSharedItems(value, plan, problems);
                    break;
                case "buffs":
                    PlanSharedBuffs(value, plan, problems);
                    break;
                case "route":
                    PlanSharedRoute(value, plan, problems);
                    break;
                default:
                    problems.Add($"'{part}' is not a part MossTank changes; the parts are macro, options, monsters, items, buffs and route");
                    break;
            }
        }
        if (problems.Count > 0)
            return new PluginSettingsChangeResult(false, "Nothing was changed: " + string.Join("; ", problems) + ".");

        ApplySharedChange(plan);
        if (_combat.Enabled && _setupChecked)
            PostSetupProblems();
        var changed = new JsonObject();
        foreach ((string part, _) in change)
            changed[part] = SharedSection(part);
        return new PluginSettingsChangeResult(true, plan.Summary(), changed.ToJsonString());
    }

    private JsonNode SharedSection(string section) => section switch
    {
        "macro" => new JsonObject
        {
            ["running"] = _combat.Enabled,
            ["status"] = _status,
            ["combat"] = _combat.Status,
            ["target"] = _combat.TargetText,
            ["navigation"] = _navigation.Status,
            ["metaState"] = _meta.CurrentState,
            ["holdingWalks"] = WalkPauseReason,
            ["problems"] = SharedProblems(),
        },
        "options" => SharedOptions(),
        "monsters" => new JsonArray([.. _combatSettings.Rules.Select(static rule => (JsonNode?)SharedMonster(rule))]),
        "items" => new JsonObject
        {
            ["combat"] = new JsonArray([.. SortedCombatItemNames().Select(name => (JsonNode?)new JsonObject
            {
                ["name"] = name,
                ["noBuffs"] = _noBuffItemNames.Contains(name),
            })]),
            ["consumables"] = new JsonArray([.. _combatSettings.ConsumableNames
                .Order(StringComparer.Ordinal)
                .Select(name => (JsonNode?)new JsonObject
                {
                    ["name"] = name,
                    ["category"] = _combatSettings.ConsumableCategories.TryGetValue(name, out ConsumableCategory category)
                        ? category.ToString()
                        : null,
                })]),
        },
        "buffs" => new JsonObject
        {
            ["extraSpells"] = SharedTexts(_buffSettings.ExtraBuffSpellNames),
            ["blacklistedFamilies"] = SharedTexts(_buffSettings.BlacklistedBuffFamilyNames),
        },
        "profiles" => new JsonObject
        {
            ["settings"] = _profiles.Selected,
            ["loot"] = _lootProfiles.Selected,
            ["route"] = _routeProfiles.Selected,
            ["meta"] = _metaProfiles.Selected,
        },
        "loot" => new JsonObject
        {
            ["enabled"] = _inventorySettings.Loot.Enabled,
            ["rules"] = _inventorySettings.Loot.Rules.Count,
        },
        "route" => new JsonObject
        {
            ["enabled"] = _navigationSettings.Enabled,
            ["mode"] = _navigationSettings.Mode.ToString(),
            ["walkLegs"] = _navigationSettings.WalkLegsWithClient,
            ["status"] = _navigation.Status,
            ["skipped"] = _navigation.LastSkippedLeg.Length == 0 ? null : _navigation.LastSkippedLeg,
            ["current"] = _navigation.CurrentWaypointIndex,
            ["waypoints"] = new JsonArray([.. _navigationSettings.Waypoints.Select(static waypoint => (JsonNode?)SharedWaypoint(waypoint))]),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    private JsonObject SharedOptions()
    {
        var options = new JsonObject();
        foreach (string name in VtankOptionCatalog.Names)
            options[name] = SharedOption(name, GetMetaOption(name));
        return options;
    }

    private static JsonNode? SharedOption(string name, ExpressionValue value)
    {
        VtankSettingValueType type = VtankOptionCatalog.DeclaredType(name);
        if (type == VtankSettingValueType.Bool || value.Kind == ExpressionValueKind.Boolean)
            return JsonValue.Create(value.IsTruthy);
        if (value.Kind != ExpressionValueKind.Number)
            return JsonValue.Create(value.ToDisplayString());
        double number = value.AsNumber();
        if (!double.IsFinite(number))
            return null;
        return type is VtankSettingValueType.Int or VtankSettingValueType.Enum
            ? JsonValue.Create((long)Math.Round(number))
            : JsonValue.Create(number);
    }

    private static JsonObject SharedMonster(MonsterRule rule)
    {
        MonsterRuleActions actions = rule.Actions;
        return new JsonObject
        {
            ["name"] = rule.Expression,
            ["priority"] = actions.Priority,
            ["actions"] = new JsonArray([.. Enum.GetValues<MonsterActionFlags>()
                .Where(flag => flag != MonsterActionFlags.None && (actions.Flags & flag) == flag)
                .Select(static flag => (JsonNode?)flag.ToString())]),
            ["damage"] = actions.DamageType.ToString(),
            ["extraVulnerability"] = actions.ExtraVulnerability.ToString(),
            ["weapon"] = actions.WeaponName,
            ["offhand"] = actions.OffhandName,
            ["petDamage"] = actions.PetDamageType.ToString(),
        };
    }

    private static JsonArray SharedTexts(IEnumerable<string> names) =>
        new([.. names.Order(StringComparer.Ordinal).Select(static name => (JsonNode?)name)]);

    private static void PlanSharedMacro(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is JsonObject { Count: 1 } macro
            && macro["running"] is JsonValue running
            && running.TryGetValue(out bool value))
        {
            plan.Running = value;
            return;
        }
        problems.Add("macro takes {\"running\": true or false}");
    }

    private static void PlanSharedOptions(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject { Count: > 0 } options)
        {
            problems.Add("options must name at least one option and its value");
            return;
        }
        foreach ((string name, JsonNode? value) in options)
        {
            if (!VtankOptionCatalog.IsKnown(name))
            {
                problems.Add($"'{name}' is not an option");
                continue;
            }
            string canonical = VtankOptionCatalog.Canonical(name);
            if (SharedOptionValue(canonical, value) is { } parsed)
                plan.Options.Add((canonical, parsed));
            else
                problems.Add($"{canonical} takes {SharedOptionType(canonical)}");
        }
    }

    private static ExpressionValue? SharedOptionValue(string name, JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        switch (VtankOptionCatalog.DeclaredType(name))
        {
            case VtankSettingValueType.Bool:
                return value.TryGetValue(out bool flag) ? ExpressionValue.Boolean(flag) : null;
            case VtankSettingValueType.Int:
            case VtankSettingValueType.Enum:
                return value.TryGetValue(out double whole)
                    && double.IsFinite(whole)
                    && whole == Math.Floor(whole)
                    && Math.Abs(whole) <= int.MaxValue
                        ? ExpressionValue.Number(whole)
                        : null;
            case VtankSettingValueType.Double:
            case VtankSettingValueType.Single:
                return value.TryGetValue(out double number) && double.IsFinite(number)
                    ? ExpressionValue.Number(number)
                    : null;
            case VtankSettingValueType.String:
                return value.TryGetValue(out string? text) ? ExpressionValue.String(text) : null;
            default:
                if (value.TryGetValue(out bool anyFlag))
                    return ExpressionValue.Boolean(anyFlag);
                if (value.TryGetValue(out double anyNumber) && double.IsFinite(anyNumber))
                    return ExpressionValue.Number(anyNumber);
                return value.TryGetValue(out string? anyText) ? ExpressionValue.String(anyText) : null;
        }
    }

    private static string SharedOptionType(string name) => VtankOptionCatalog.DeclaredType(name) switch
    {
        VtankSettingValueType.Bool => "true or false",
        VtankSettingValueType.Int or VtankSettingValueType.Enum => "a whole number",
        VtankSettingValueType.Double or VtankSettingValueType.Single => "a number",
        VtankSettingValueType.String => "text",
        _ => "true or false, a number or text",
    };

    private void PlanSharedMonsters(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject { Count: > 0 } monsters)
        {
            problems.Add("monsters takes {\"set\": [rules], \"remove\": [names]}");
            return;
        }
        foreach ((string key, JsonNode? value) in monsters)
        {
            switch (key)
            {
                case "set":
                    if (value is not JsonArray rules)
                    {
                        problems.Add("monsters.set must be a list of rules");
                        break;
                    }
                    foreach (JsonNode? rule in rules)
                        PlanSharedMonster(rule, plan, problems);
                    break;
                case "remove":
                    foreach (string name in SharedNames(value, "monsters.remove", problems))
                    {
                        if (MonsterRule.IsDefaultName(name))
                            problems.Add("the DEFAULT rule cannot be removed; give it priority -1 to leave alone the monsters no rule names");
                        else if (MonsterRuleIndex(name) < 0)
                            problems.Add($"no monster rule is named '{name}'");
                        else
                            plan.MonsterRemovals.Add(name);
                    }
                    break;
                default:
                    problems.Add($"monsters.{key} is not known; use set or remove");
                    break;
            }
        }
    }

    private void PlanSharedMonster(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject rule || SharedText(rule["name"]) is not { } named || named.Trim().Length == 0)
        {
            problems.Add("each monster rule needs a name");
            return;
        }
        string name = named.Trim();
        int index = MonsterRuleIndex(name);
        string expression = index >= 0 ? _combatSettings.Rules[index].Expression : name;
        MonsterRuleActions actions = index >= 0 ? _combatSettings.Rules[index].Actions : new MonsterRuleActions();
        foreach ((string field, JsonNode? value) in rule)
        {
            switch (field)
            {
                case "name":
                    break;
                case "priority":
                    if (value is JsonValue given
                        && given.TryGetValue(out double priority)
                        && priority == Math.Floor(priority)
                        && priority is >= -1d and <= 4d)
                    {
                        actions = actions with { Priority = (int)priority };
                    }
                    else
                    {
                        problems.Add($"{name}: priority must be a whole number from -1 to 4");
                    }
                    break;
                case "actions":
                    if (SharedFlags(value, out MonsterActionFlags flags))
                        actions = actions with { Flags = flags };
                    else
                        problems.Add($"{name}: actions must be a list of {ActionWords}");
                    break;
                case "damage":
                case "extraVulnerability":
                case "petDamage":
                    if (SharedDamage(value) is not { } damage)
                    {
                        problems.Add($"{name}: {field} must be one of {DamageWords}");
                        break;
                    }
                    actions = field switch
                    {
                        "damage" => actions with { DamageType = damage },
                        "extraVulnerability" => actions with { ExtraVulnerability = damage },
                        _ => actions with { PetDamageType = damage },
                    };
                    break;
                case "weapon":
                case "offhand":
                    if (SharedText(value) is { } item)
                    {
                        string wielded = item.Trim();
                        actions = SetWeaponSlot(actions, offhand: field == "offhand", wielded.Length == 0 ? null : wielded);
                    }
                    else
                    {
                        problems.Add($"{name}: {field} must be the name of a carried item, or empty to clear it");
                    }
                    break;
                default:
                    problems.Add($"{name}: '{field}' is not a rule field");
                    break;
            }
        }
        MonsterRule compiled = MonsterRule.Compile(expression, actions, out string? parseError);
        if (parseError is not null)
            problems.Add(parseError);
        else
            plan.MonsterSets.Add(compiled);
    }

    private int MonsterRuleIndex(string name)
    {
        for (int index = 0; index < _combatSettings.Rules.Count; index++)
        {
            string expression = _combatSettings.Rules[index].Expression;
            if (expression.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (MonsterRule.IsDefaultName(expression) && MonsterRule.IsDefaultName(name)))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool SharedFlags(JsonNode? node, out MonsterActionFlags flags)
    {
        flags = MonsterActionFlags.None;
        if (node is not JsonArray words)
            return false;
        foreach (JsonNode? word in words)
        {
            if (SharedText(word) is not { } text
                || int.TryParse(text, out _)
                || !Enum.TryParse(text.Trim(), ignoreCase: true, out MonsterActionFlags flag)
                || flag == MonsterActionFlags.None
                || !Enum.IsDefined(flag))
            {
                return false;
            }
            flags |= flag;
        }
        return true;
    }

    private static MonsterDamageType? SharedDamage(JsonNode? node) =>
        SharedText(node) is { } text
        && !int.TryParse(text, out _)
        && Enum.TryParse(text.Replace(" ", string.Empty), ignoreCase: true, out MonsterDamageType damage)
        && Enum.IsDefined(damage)
            ? damage
            : null;

    private void PlanSharedItems(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject { Count: > 0 } items)
        {
            problems.Add("items takes lists under add, addNoBuffs, remove, addConsumables and removeConsumables");
            return;
        }
        IReadOnlyList<PluginInventoryItem>? carried = null;
        foreach ((string key, JsonNode? value) in items)
        {
            switch (key)
            {
                case "add":
                case "addNoBuffs":
                case "addConsumables":
                    carried ??= _host.Automation.Items.CaptureOwnedItems();
                    foreach (string name in SharedNames(value, $"items.{key}", problems))
                    {
                        if (CarriedItem(carried, name) is not { } item)
                            problems.Add($"'{name}' is not carried; items are added from the character's packs");
                        else if (key == "addConsumables")
                            plan.ConsumableAdds.Add(item);
                        else
                            plan.ItemAdds.Add((item, key == "addNoBuffs"));
                    }
                    break;
                case "remove":
                    foreach (string name in SharedNames(value, "items.remove", problems))
                    {
                        if (ListedName(_combatSettings.CombatItemNames, name) is { } listed)
                            plan.ItemRemovals.Add(listed);
                        else
                            problems.Add($"'{name}' is not on the items list");
                    }
                    break;
                case "removeConsumables":
                    foreach (string name in SharedNames(value, "items.removeConsumables", problems))
                    {
                        if (ListedName(_combatSettings.ConsumableNames, name) is { } listed)
                            plan.ConsumableRemovals.Add(listed);
                        else
                            problems.Add($"'{name}' is not on the consumables list");
                    }
                    break;
                default:
                    problems.Add($"items.{key} is not known; use add, addNoBuffs, remove, addConsumables or removeConsumables");
                    break;
            }
        }
    }

    private void PlanSharedBuffs(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject { Count: > 0 } buffs)
        {
            problems.Add("buffs takes lists under addSpells, removeSpells, addBlacklistedFamilies and removeBlacklistedFamilies");
            return;
        }
        foreach ((string key, JsonNode? value) in buffs)
        {
            switch (key)
            {
                case "addSpells":
                    plan.SpellAdds.AddRange(SharedNames(value, "buffs.addSpells", problems));
                    break;
                case "removeSpells":
                    foreach (string name in SharedNames(value, "buffs.removeSpells", problems))
                    {
                        if (ListedName(_buffSettings.ExtraBuffSpellNames, name) is { } listed)
                            plan.SpellRemovals.Add(listed);
                        else
                            problems.Add($"'{name}' is not an extra buff spell");
                    }
                    break;
                case "addBlacklistedFamilies":
                    plan.FamilyAdds.AddRange(SharedNames(value, "buffs.addBlacklistedFamilies", problems));
                    break;
                case "removeBlacklistedFamilies":
                    foreach (string name in SharedNames(value, "buffs.removeBlacklistedFamilies", problems))
                    {
                        if (ListedName(_buffSettings.BlacklistedBuffFamilyNames, name) is { } listed)
                            plan.FamilyRemovals.Add(listed);
                        else
                            problems.Add($"'{name}' is not a blacklisted buff family");
                    }
                    break;
                default:
                    problems.Add($"buffs.{key} is not known; use addSpells, removeSpells, addBlacklistedFamilies or removeBlacklistedFamilies");
                    break;
            }
        }
    }

    private static JsonObject SharedWaypoint(RouteWaypoint waypoint)
    {
        var node = waypoint.Type switch
        {
            RouteWaypointType.Point => new JsonObject { ["point"] = SharedPoint(waypoint.Position) },
            RouteWaypointType.Pause => new JsonObject { ["pause"] = waypoint.DurationMilliseconds / 1000d },
            RouteWaypointType.ChatCommand => new JsonObject { ["chat"] = waypoint.Text },
            _ => new JsonObject { ["type"] = waypoint.Type.ToString(), ["at"] = SharedPoint(waypoint.Position) },
        };
        if (waypoint.Type is not (RouteWaypointType.Point or RouteWaypointType.Pause or RouteWaypointType.ChatCommand))
        {
            if (waypoint.ObjectName.Length > 0)
                node["object"] = waypoint.ObjectName;
            if (waypoint.Text.Length > 0)
                node["text"] = waypoint.Text;
        }
        return node;
    }

    /// <summary>Decimal places a route point's coordinates are stated to, in map units of 240 m: about 2.4 mm.</summary>
    private const int SharedPointDecimals = 5;

    /// <summary>
    /// A position in map coordinates, the way VTank's route files keep it: north-south,
    /// east-west and elevation, each in map units of 240 m.
    /// </summary>
    internal static JsonObject SharedPoint(in PluginNavigationPosition position) => new()
    {
        ["northSouth"] = Math.Round(position.NorthSouth, SharedPointDecimals),
        ["eastWest"] = Math.Round(position.EastWest, SharedPointDecimals),
        ["elevation"] = Math.Round(position.Elevation, SharedPointDecimals),
    };

    /// <summary>
    /// A position from map coordinates: northSouth, eastWest and elevation, each a number.
    /// Other fields, such as the cell and text reported with a position, are ignored, since a
    /// route keeps no cells.
    /// </summary>
    internal static bool TryReadSharedPoint(JsonNode? node, out PluginNavigationPosition position)
    {
        position = default;
        if (node is not JsonObject point
            || !TryReadCoordinate(point, "northSouth", out double northSouth)
            || !TryReadCoordinate(point, "eastWest", out double eastWest)
            || !TryReadCoordinate(point, "elevation", out double elevation))
        {
            return false;
        }
        position = new PluginNavigationPosition(0u, eastWest, northSouth, elevation, 0f, IsOutdoor: true);
        return true;
    }

    private static bool TryReadCoordinate(JsonObject point, string name, out double value)
    {
        value = 0d;
        return point[name] is JsonValue number
            && number.TryGetValue(out value)
            && double.IsFinite(value)
            && Math.Abs(value) <= 1000d;
    }

    private static void PlanSharedRoute(JsonNode? node, SharedChange plan, List<string> problems)
    {
        if (node is not JsonObject { Count: > 0 } route)
        {
            problems.Add("route takes enabled, mode, walkLegs and waypoints");
            return;
        }
        foreach ((string key, JsonNode? value) in route)
        {
            switch (key)
            {
                case "enabled":
                    if (value is JsonValue enabled && enabled.TryGetValue(out bool on))
                        plan.RouteEnabled = on;
                    else
                        problems.Add("route.enabled takes true or false");
                    break;
                case "mode":
                    if (SharedText(value) is { } word
                        && !int.TryParse(word, out _)
                        && Enum.TryParse(word.Trim(), ignoreCase: true, out RouteMode mode)
                        && mode is RouteMode.Circular or RouteMode.Linear or RouteMode.Once)
                    {
                        plan.Mode = mode;
                    }
                    else
                    {
                        problems.Add("route.mode takes Circular, Linear or Once");
                    }
                    break;
                case "walkLegs":
                    if (value is JsonValue legs && legs.TryGetValue(out bool walk))
                        plan.WalkLegs = walk;
                    else
                        problems.Add("route.walkLegs takes true or false");
                    break;
                case "waypoints":
                    if (value is not JsonArray list)
                    {
                        problems.Add("route.waypoints must be a list");
                        break;
                    }
                    var waypoints = new List<RouteWaypoint>();
                    PluginNavigationPosition last = default;
                    int index = 0;
                    foreach (JsonNode? item in list)
                    {
                        index++;
                        if (SharedWaypointFrom(item, last) is not { } waypoint)
                        {
                            problems.Add($"route.waypoints[{index}] must be {{\"point\": {{\"northSouth\": <n>, \"eastWest\": <e>, \"elevation\": <z>}}}}, {{\"pause\": <seconds up to 3600>}} or {{\"chat\": \"<text>\"}}");
                            continue;
                        }
                        if (waypoint.Type == RouteWaypointType.Point)
                            last = waypoint.Position;
                        waypoints.Add(waypoint);
                    }
                    plan.Waypoints = waypoints;
                    break;
                default:
                    problems.Add($"route.{key} is not known; use enabled, mode, walkLegs or waypoints");
                    break;
            }
        }
    }

    private static RouteWaypoint? SharedWaypointFrom(JsonNode? node, PluginNavigationPosition last)
    {
        if (node is not JsonObject { Count: 1 } item)
            return null;
        (string kind, JsonNode? value) = item.First();
        switch (kind)
        {
            case "point":
                return TryReadSharedPoint(value, out PluginNavigationPosition at)
                    ? new RouteWaypoint { Type = RouteWaypointType.Point, Position = at }
                    : null;
            case "pause":
                return value is JsonValue seconds
                    && seconds.TryGetValue(out double duration)
                    && double.IsFinite(duration)
                    && duration is >= 0d and <= 3600d
                        ? new RouteWaypoint
                        {
                            Type = RouteWaypointType.Pause,
                            Position = last,
                            DurationMilliseconds = (int)Math.Round(duration * 1000d),
                        }
                        : null;
            case "chat":
                return SharedText(value) is { Length: > 0 and <= 128 } text
                    ? new RouteWaypoint { Type = RouteWaypointType.ChatCommand, Position = last, Text = text }
                    : null;
            default:
                return null;
        }
    }

    private void ApplySharedChange(SharedChange plan)
    {
        if (plan.Options.Count > 0)
        {
            _applyingProfileOptions = true;
            try
            {
                foreach ((string name, ExpressionValue value) in plan.Options)
                    SetMetaOption(name, value);
            }
            finally
            {
                _applyingProfileOptions = false;
            }
        }
        foreach (MonsterRule rule in plan.MonsterSets)
        {
            int index = MonsterRuleIndex(rule.Expression);
            if (index >= 0)
                _combatSettings.Rules[index] = rule;
            else
                _combatSettings.Rules.Add(rule);
        }
        foreach (string name in plan.MonsterRemovals)
        {
            int index = MonsterRuleIndex(name);
            if (index >= 0)
                _combatSettings.Rules.RemoveAt(index);
        }
        if (plan.MonsterSets.Count + plan.MonsterRemovals.Count > 0)
        {
            EnsureDefaultMonsterRule();
            RefreshMonsterEditor();
        }
        foreach ((PluginInventoryItem item, bool noBuffs) in plan.ItemAdds)
            AddProfileItem(item, noBuffs);
        foreach (string name in plan.ItemRemovals)
        {
            _combatSettings.CombatItemNames.Remove(name);
            _combatSettings.CombatItemOrder.Remove(name);
            _noBuffItemNames.Remove(name);
            _itemHandedness.Remove(name);
            ClearItemEnchantRows(name);
        }
        foreach (PluginInventoryItem item in plan.ConsumableAdds)
        {
            _combatSettings.ConsumableNames.Add(item.Name);
            _combatSettings.ConsumableCategories[item.Name] = ConsumableClassifier.Classify(item);
        }
        foreach (string name in plan.ConsumableRemovals)
        {
            _combatSettings.ConsumableNames.Remove(name);
            _combatSettings.ConsumableCategories.Remove(name);
        }
        if (plan.ItemAdds.Count + plan.ItemRemovals.Count + plan.ConsumableAdds.Count + plan.ConsumableRemovals.Count > 0)
            RefreshItemEditors();
        foreach (string name in plan.SpellAdds)
            _buffSettings.ExtraBuffSpellNames.Add(name);
        foreach (string name in plan.SpellRemovals)
            _buffSettings.ExtraBuffSpellNames.Remove(name);
        foreach (string name in plan.FamilyAdds)
            _buffSettings.BlacklistedBuffFamilyNames.Add(name);
        foreach (string name in plan.FamilyRemovals)
            _buffSettings.BlacklistedBuffFamilyNames.Remove(name);
        if (plan.RouteEnabled is { } routeOn)
            SetMetaOption("EnableNav", ExpressionValue.Boolean(routeOn));
        if (plan.Mode is { } mode)
            _navigationSettings.Mode = mode;
        if (plan.WalkLegs is { } walkLegs)
            _navigationSettings.WalkLegsWithClient = walkLegs;
        if (plan.Waypoints is { } waypoints)
        {
            _navigationSettings.Waypoints.Clear();
            _navigationSettings.Waypoints.AddRange(waypoints);
        }
        if (plan.RouteEnabled is not null || plan.Mode is not null || plan.WalkLegs is not null || plan.Waypoints is not null)
        {
            _navigation.Reset();
            RefreshRouteEditor();
        }
        SaveProfile();
        if (plan.Running is { } running)
            SetMacroRunning(running);
    }

    private static IEnumerable<string> SharedNames(JsonNode? node, string part, List<string> problems)
    {
        if (node is not JsonArray list)
        {
            problems.Add($"{part} must be a list of names");
            return [];
        }
        var names = new List<string>();
        foreach (JsonNode? item in list)
        {
            if (SharedText(item) is { } text && text.Trim().Length > 0)
                names.Add(text.Trim());
            else
                problems.Add($"{part} holds something that is not a name");
        }
        return names;
    }

    private static string? SharedText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static PluginInventoryItem? CarriedItem(IReadOnlyList<PluginInventoryItem> carried, string name)
    {
        foreach (PluginInventoryItem item in carried)
        {
            if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static string? ListedName(IEnumerable<string> names, string name) =>
        names.FirstOrDefault(listed => listed.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>What one change to the shared settings will do, gathered before any of it is done.</summary>
    private sealed class SharedChange
    {
        public bool? Running { get; set; }
        public List<(string Name, ExpressionValue Value)> Options { get; } = [];
        public List<MonsterRule> MonsterSets { get; } = [];
        public List<string> MonsterRemovals { get; } = [];
        public List<(PluginInventoryItem Item, bool NoBuffs)> ItemAdds { get; } = [];
        public List<string> ItemRemovals { get; } = [];
        public List<PluginInventoryItem> ConsumableAdds { get; } = [];
        public List<string> ConsumableRemovals { get; } = [];
        public List<string> SpellAdds { get; } = [];
        public List<string> SpellRemovals { get; } = [];
        public List<string> FamilyAdds { get; } = [];
        public List<string> FamilyRemovals { get; } = [];
        public bool? RouteEnabled { get; set; }
        public RouteMode? Mode { get; set; }
        public bool? WalkLegs { get; set; }
        public List<RouteWaypoint>? Waypoints { get; set; }

        public string Summary()
        {
            var parts = new List<string>();
            if (Running is { } running)
                parts.Add(running ? "started the macro" : "stopped the macro");
            Count(parts, Options.Count, "set", "option", "options");
            Count(parts, MonsterSets.Count, "set", "monster rule", "monster rules");
            Count(parts, MonsterRemovals.Count, "removed", "monster rule", "monster rules");
            Count(parts, ItemAdds.Count, "added", "item", "items");
            Count(parts, ItemRemovals.Count, "removed", "item", "items");
            Count(parts, ConsumableAdds.Count, "added", "consumable", "consumables");
            Count(parts, ConsumableRemovals.Count, "removed", "consumable", "consumables");
            Count(parts, SpellAdds.Count, "added", "buff spell", "buff spells");
            Count(parts, SpellRemovals.Count, "removed", "buff spell", "buff spells");
            Count(parts, FamilyAdds.Count, "blacklisted", "buff family", "buff families");
            Count(parts, FamilyRemovals.Count, "cleared", "blacklisted buff family", "blacklisted buff families");
            if (Waypoints is { } waypoints)
                parts.Add($"replaced the route with {waypoints.Count} waypoint{(waypoints.Count == 1 ? string.Empty : "s")}");
            if (Mode is { } mode)
                parts.Add($"set the route to {mode}");
            if (WalkLegs is { } walkLegs)
                parts.Add(walkLegs ? "had the client walk the route's legs" : "had MossTank steer the route's legs");
            if (RouteEnabled is { } on)
                parts.Add(on ? "turned route navigation on" : "turned route navigation off");
            string said = string.Join(", ", parts);
            return said.Length == 0
                ? "Nothing needed changing."
                : char.ToUpperInvariant(said[0]) + said[1..] + ".";
        }

        private static void Count(List<string> parts, int count, string verb, string one, string many)
        {
            if (count > 0)
                parts.Add($"{verb} {count} {(count == 1 ? one : many)}");
        }
    }
}

/// <summary>MossTank's settings, shared with other plugins through the host.</summary>
internal sealed class MossTankSharedSettings(MossTankPanel panel) : IPluginSettingsProvider
{
    public string Describe() => MossTankPanel.SharedSettingsHelp;

    public string? Read(string? section) => panel.ReadSharedSettings(section);

    public PluginSettingsChangeResult Change(string changeJson) => panel.ChangeSharedSettings(changeJson);
}
