# The Agent plugin

The Agent plugin lets an AI client play through the graphical client. It serves
the Model Context Protocol (MCP) on this computer's loopback address, turns short
command lines such as `buy prismatic taper 10` into the client's existing
automation calls, and reports what actually happened rather than what was sent.

It is bundled with the client and listens from the moment the client starts, so
an AI client can log a character in from the character list, and out again. It
is built only on `AcDream.Plugin.Abstractions`, so it can see and do exactly what
any plugin can.

## Connect in one command

Start the client. Then, in a terminal, for Claude Code:

```
claude mcp add --transport http openac http://127.0.0.1:31337/mcp
```

At the character list, ask for a character by name: the model reads the list
with `characters` and enters the world by acting `login <name>`. Then ask for
something, such as "open the nearest vendor and buy ten prismatic
tapers". The model finds the vendor, and the lines that work on it, with `capabilities`, opens it by acting
`use <vendor id>`, reads the listings with `vendor`, buys by acting
`buy prismatic taper 10`, and checks the purchase with `outcome`. Any MCP client
that speaks Streamable HTTP connects the same way.

## In-game commands

| Command | What it does |
|---|---|
| `/agent status` | Where clients connect, and whether records are written to a file. |
| `/agent record <path>` | Writes every record to a file, one JSON object per line. The path must be absolute or start with `~/`. |
| `/agent record off` | Stops writing the file. |
| `/agent do <line>` | Runs one command line from chat, as an AI client would. |
| `/agent help` | Lists these commands. |

AI clients connect on port 31337. When that port cannot be used, as when a
second client on the same computer holds it, the listener takes a free port and
says which in chat and in the client's log.

## Tools

Only `act` can make the character do anything, and only `configure` changes
another plugin's settings. Every other tool only reads.

| Tool | Answers with |
|---|---|
| `observe` | The latest session, body, vitals, stats, target and combat-mode records, the actions still pending, and the most recent outcomes. |
| `capabilities` | Everything around the character in one answer, each object with the lines this client would take for it now and whether each would be taken; with `guid`, one object. |
| `act` | Runs one command line and returns a handle with a status: `pending`, `done`, `refused` or `sent-as-chat`. |
| `outcome` | How the line with a handle ended: its outcome word, its shared class, and every record it produced. |
| `events` | Records after a cursor. With `waitSeconds` it waits for the next one, and `until` wakes it on a condition. |
| `nearby` | Objects around the character, nearest first, with distance, bearing, kind and sight. |
| `explore` | Rooms, passages and open ground the character can walk to, from the client's navigation mesh, and outdoors nearby buildings and the landblocks beside its own, unvisited first and nearest first, each with a line that walks there. |
| `inspect` | Everything the client holds about one object, with its sight. |
| `spells` | Known spells, narrowed by `search`, including whether their components are carried. |
| `skills` | Skills with their training and values. |
| `buffs` | Enchantments on the character with the time remaining. |
| `inventory` | Carried items, narrowed by `search`, with the free pack slots. |
| `equipment` | What is worn and wielded, and what could be equipped. |
| `vendor` | The open vendor's listings with prices and stock. |
| `container` | The items in the open corpse or container. |
| `corpses` | Corpses in range, and whether each has been opened. |
| `characters` | The account's characters at the character list, and where the client stands in logging in. |
| `settings` | The settings other plugins share, such as MossTank's options, monster rules, items, buffs and route, and how to change them. |
| `configure` | Changes a plugin's shared settings with one JSON object, and answers with the parts it changed as the plugin now holds them. |

`capabilities` answers what the character can do in one call. It lists everything around the
character, nearest first, each object with the lines this client would take for it now, such as
`use 0x70000002` or `attack 0x70000001`, graded `available`, `unavailable` with the reason, or
`unknown` with what is missing, by the same checks the verbs make before they send. Lines graded
alike for every object are said once in `legend`, with `<guid>` in place of the id. The lines that
act on the character itself, such as `buy` at the open vendor, `stance combat` or `logout`, come
under `character`. With `guid` it answers for one object, including one the character carries. A
line graded `available` can still be refused by the server, and that answer arrives on the line's
own outcome. The client learns that an item cannot be sold only by appraising it, and the server
does not answer an offer of such an item, so a sale of an item the client has never appraised is
graded `unknown`, and `sell` appraises the item before offering it; an item that cannot be sold is
then refused with the game's reason.

`spells` shows at most 50 rows, and `inventory`, `vendor` and `container` at most 100, unless
`limit` asks for another number up to 500. Each answer says how many rows matched, how many are
shown and how many the limit held back, so a short list never reads as a short inventory.

`act` and `outcome` take `waitSeconds`, up to 30, to answer once the action
settles instead of at once. An accepted action is not a finished one: the server
can still refuse a spell the client sent, so read `outcome` before relying on it.

An `events` condition names a record kind, optional exact field values, and one
optional numeric comparison. This call returns when health falls below 100:

```json
{
  "kinds": ["vital-changed"],
  "until": [{ "kind": "vital-changed", "match": { "vital": "health" }, "compare": ["value", "<", 100] }],
  "waitSeconds": 30
}
```

## Command lines

`act` and `/agent do` take the same lines. Ids are the hexadecimal object ids
the read tools print, such as `0x70000001`.

| Family | Lines |
|---|---|
| Session | `characters`, `login <name or id>`, `logout` |
| Read | `vitals`, `stats`, `location`, `snapshot`, `capabilities [id]`, `skills`, `buffs`, `spells [search] [limit <n>]`, `nearby [kind] [range]`, `explore [limit <n>]`, `inspect <id>`, `inventory [search] [limit <n>]`, `equipment`, `vendor [limit <n>]`, `loot list [limit <n>]`, `loot corpses [range]` |
| Chat | `say <text>`, `tell <name>, <message>`, `emote <text>` |
| Target | `target <id>`, `target nearest [kind]`, `untarget` |
| Motion | `walk [forward\|backward] [amount]`, `run [forward\|backward] [amount]`, `strafe left\|right [amount]`, `turn left\|right [amount]`, `turn to <degrees>`, `face <id>`, `go to <id, name or target> [within <meters>]`, `go to <north-south> <east-west> [elevation] [within <meters>]`, `jump [power]`, `stop [walking\|running\|strafing\|turning]`, `stance combat\|peace`, `cancel` |
| Magic | `cast <spell name or id> [on <id>]` |
| Objects | `use <id>`, `use <item id> on <id>`, `open <id>` |
| Items | `loot <item id>`, `drop <item id> [amount]`, `give <item id> to <id> [amount]`, `move <item id> to <container id> [amount]`, `equip <item id>`, `unequip <item id>` |
| Vendor | `buy <listing id or name> [quantity]`, `sell <item id> [amount]` |
| Combat | `attack [id] [power from 0 to 1] [high\|medium\|low]` |
| Plugin settings | `settings [plugin] [section]`, `configure <plugin> <change>` |

`login` works at the character list, before any character is in the world. It
enters the world as the named character, as choosing it and pressing enter does,
and resolves `completed` once the character is in the world, or `refused` with
the client's message, such as when another of the account's characters is still
in the world. `logout` logs the character out the way the client's own log out
does and resolves `completed` once the character list is back, so a model can
switch characters without closing the client. `observe` shows where the client
stands in `session.stage`: `not-connected`, `connecting`, `choosing-character`,
`entering-world` or `in-world`.

An amount is meters, or degrees for a turn, or seconds when written like `20s`.
Walking or running, strafing and turning combine the way movement keys do:
after `run forward 60s`, `turn left 5` and `strafe right 2s` steer the run
without stopping it, and a new move replaces only a move of its own kind. The
client carries out each move itself, lands a turn on its exact angle, and
reports a move as blocked when the character stops making progress. A move
without an amount keeps going until `stop`, for at most thirty seconds, and the
player's own movement keys end every move at once.

`go to` walks to an object along a route the client plans from its own
collision world: around walls and objects, through doorways, and up and down
ramps and stairs. A walk arrives within `within` meters of the object, 2.5 by
default, only where no wall stands between the character and the object, and
ends facing it. When nothing that near can both be reached and see the object,
such as a vendor behind a counter, the walk ends at the nearest spot that can,
up to 10 m away. When no reachable spot can see it, as through a window whose
collision fills the opening, the walk ends at the nearest reachable spot up to
10 m away. A walk that ends short in either way still completes, and its
`reason` says why. A walk that meets a closed door on its way opens it first,
as a player's click would, and goes on once it is open. When the character
stops making progress, the client plans again around the spot where it stuck; a
walk that stays blocked names what stood beside that spot in `blockedBy`, such
as a door that would not open. `remaining` is the straight-line distance from
the character to where the object stands, and `no-route` means nothing joins
the character to the object. `stop`, `cancel` and the player's movement keys
end a walk. While MossTank needs the character, to fight, loot or buff, or
while the character attacks or another plugin steers it, a walk stops where it
stands and reports `waiting` with what it waits on, and its `go to` stays
pending. Once nothing has needed the character for a moment, the walk plans
again from where the character stands and goes on, however often that happens
along the way. A walk takes the place of MossTank's own route navigation
among its rules while that is off, so whatever MossTank ranks above navigation
interrupts the walk and nothing ranked below it does.
With its route navigation on and **Walk legs with client pathing** checked on
its Route tab, `walkLegs` in its settings, MossTank walks its own route this
way. It asks for one walk at a time, to the next point or to the farthest of
the points ahead that lie along a straight line, counts the points the walk
goes by, and moves on when the walk arrives, so a fight that shoves the
character off the route is walked back from wherever it ended. Where the route
turns, the character stops for a moment before the next walk, as MossTank's own
steering does. A leg the client cannot walk is skipped with a chat message, and
`skipped` in `settings mosstank route` names the last one skipped. While a walk MossTank did not ask for is under way, such as one from `go to`,
the route waits for it to end. With `walkLegs` on, doors are opened by the walks,
and MossTank's OpenDoors stands aside. A walk to an object farther away than one planning grid reaches,
about 270 m, goes in stages, each planned to the edge of a grid toward the
object, up to 1000 m. Inside a sealed dungeon one grid covers the whole
dungeon, however large, and serves every walk there; the largest take a few
seconds to plan the first time. Each route is planned three ways at once, from
the shortest to one keeping well clear of walls, and the tidiest is walked, its
corners taken wide where there is room so the character does not brush them. A
route keeps out of objects the server placed, such as ore deposits, whenever
another way arrives. A route passes creatures and players around them where
there is room, and through them where going around would bring the character
nearer walls than going through. A walk plans a way around one that steps onto
its route without stopping, and a walk stopped by one waits for it to move
aside, up to twice, before planning around it; it never keeps out of the spot a
creature stood in for good. A door the client has not appraised is appraised before a
walk uses it; a locked door, or one that will not open, is walked around, and
with no other way the walk ends `blocked` naming it. Where no walk reaches, a
route leaps: it hops off ledges of up to 12 m, the deepest fall measured to do
no damage, and takes standing long jumps across gaps and up onto ledges as far
and as high as the character's jump skill, run skill and burden allow. A route
does not pass through portals. While a walk is under way the client
draws its route as a magenta line. Ctrl+F4 also shows the grid, Ctrl+F5 plans
a route to the selected object, and Ctrl+F6 walks to it or stops the walk;
without Ctrl on the acdream keymap, where F4 to F6 are free.

`go to` also walks to a place, given in map coordinates the way positions are
reported: `go to 24.30537 -101.10833 0.00002` gives north-south, east-west and
elevation, and `go to 24.305N, 101.108W` keeps the character's own elevation. A
place is walked to the same way in a dungeon, on open land and inside buildings,
and the walk turns to face nothing when it arrives. `explore` offers places to go
when nothing nearer calls, such as a dungeon with no monsters in sight: the places
a walk reaches from where the character stands, over the whole dungeon or the land
around it, told apart on the client's navigation mesh by how open the floor is. A
room is floor that opens out away from its edges and narrows at its doorways, a
passage is floor that stays narrow, given in stretches of about 20 m, and open
ground is a room too large to call one. Outdoors it also gives the buildings in
the character's landblock and those beside it, at their origins with their
doorways, and the landblocks beside the character's own, at their middles with
their direction and whether they lie under water. Places the character has not stood near
since the plugin started come first, then those it has, marked `visited`, each
nearest walk first from where the character stands, so the order follows it
deeper in. Each place is given at its most open point with its kind, notes such
as `dead end`, `junction`, `stairs or ramp`, `above` or `below`, its floor area,
width, rise and exits, the walk's length, the straight distance and bearing, and a
`go` line to send through `act`. The first `explore` maps the ground, which takes
a moment in a large dungeon, and answers `mapping`; an answer found from where the
character stood before answers `refreshing`. Ask again in a few seconds for either.

Client
commands that close the client, kill the character, or change its player-killer
status are refused, and `logout` is how a model leaves the world. Any other line is handed to the client as chat or a
client command, so a model can make the character speak.

`settings` reads the settings other plugins share, and `configure` changes them.
MossTank shares all of its own: whether its macro runs and what it is doing,
every option by name, the advanced ones included, its monster rules in order
with each rule's priority, actions, damage types and weapons, the items and
consumables it uses, its extra and blacklisted buffs, its route with every
waypoint, and which profiles are loaded. `settings mosstank` answers with all of
it and with `howToChange`, and `settings mosstank options` with one section.
`configure mosstank <change>` takes one JSON object, such as
`{"options":{"EnableCombat":true},"macro":{"running":true}}`, and saves the
change to the loaded profile as MossTank's own panel does. A route is replaced
whole, its points in map coordinates, the way VTank's route files keep them, with
pauses in seconds and chat lines between them, such as
`{"route":{"waypoints":[{"point":{"northSouth":42.12345,"eastWest":33.61234,"elevation":0.39169}},{"pause":5},{"chat":"/say hi"}],"mode":"Circular","walkLegs":true,"enabled":true}}`.
A position the agent reports, the character's own or an object's, can be given as
a point as it is, so a model can build a route from where it has stood.
A change wrong in any
part changes nothing and is refused with every reason. An applied change answers
with the parts it touched as MossTank now holds them, so a range MossTank kept
within its limits shows the value it kept.

Objects in `nearby` and `inspect` carry `sight`: a verdict for an arc spell, a war
bolt and an arrow, each `visible`, `blocked`, or `cannot-say` with `because`. The
three fly different paths, so an arc can clear a ledge that stops a bolt, and
`blockedBy` names what stopped a blocked shot: an object, such as a closed door,
or `geometry` for the landscape and the walls, floors and ceilings of buildings
and dungeons. The client
traces each path through its own collision world, as a prediction: the server
still decides at launch. One `nearby` answer traces its nearest twelve objects
within 80 m, and `inspect` traces any one.

## Records

Everything the plugin reports is a record: one JSON object with `schema`
(`acdream.agent.v1`), a dense `seq`, `at` in seconds since the plugin started,
`kind`, and `batch`. The records a command line produces carry its handle as `id`.

A value the client has not been told is never written as zero. It is written as
`{"presence": "unknown", "value": null, "because": "..."}`, and a value the
client holds is written as `{"presence": "observed", "value": ...}`.

Every command line gets exactly one `command-outcome`. An action the client sent
also ends in exactly one terminal record, such as `cast-outcome` or
`inventory-outcome`. Each family keeps its own outcome words, and each word maps
to one shared class: `confirmed`, `partial`, `refused`, `withdrawn`,
`unconfirmed`, `unattributable`, `unreachable` or `lost`. `unconfirmed` means no
answer arrived in time, and the action may still have happened.

`/agent record` writes the same records to a file. When the writer falls behind,
it drops records rather than slow the game, and writes an `event-gap` line saying
how many were dropped and where the stream resumes.

## Safety

- The listener binds 127.0.0.1 only. Requests that name another host, and
  requests from web pages on other origins, are refused.
- It listens from the moment the client starts until the client closes. There is
  no password: any program running on this computer can connect, log a character
  in or out, and act as it.
- Only `act` can make the character do anything, and it runs one line per call.
- `configure` changes other plugins' settings, such as starting MossTank's
  macro, which can set the character fighting.

## Not included

Finding routes, autonomous combat, the headless host, and pushes from the
server are not part of this plugin. `GET /mcp` is refused, so clients do not open
an event stream; `events` with `waitSeconds` covers waiting for something to
happen.
