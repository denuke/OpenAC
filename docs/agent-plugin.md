# The Agent plugin

The Agent plugin lets an AI client play through the graphical client. It serves
the Model Context Protocol (MCP) on this computer's loopback address, turns short
command lines such as `buy prismatic taper 10` into the client's existing
automation calls, and reports what actually happened rather than what was sent.

It is bundled with the client and does nothing until a player types
`/agent listen`. It is built only on `AcDream.Plugin.Abstractions`, so it can
see and do exactly what any plugin can.

## Connect in two commands

In the game's chat:

```
/agent listen
```

In a terminal, for Claude Code:

```
claude mcp add --transport http openac http://127.0.0.1:31337/mcp
```

Then ask for something, such as "open the nearest vendor and buy ten prismatic
tapers". The model finds the vendor with `nearby`, opens it by acting
`use <vendor id>`, reads the listings with `vendor`, buys by acting
`buy prismatic taper 10`, and checks the purchase with `outcome`. Any MCP client
that speaks Streamable HTTP connects the same way.

## In-game commands

| Command | What it does |
|---|---|
| `/agent listen [port]` | Lets AI clients on this computer connect. The port is 31337 unless one is given, and a given port is remembered. |
| `/agent stop` | Closes the listener and ends every client session. |
| `/agent status` | Whether clients can connect and where, and whether records are written to a file. |
| `/agent record <path>` | Writes every record to a file, one JSON object per line. The path must be absolute or start with `~/`. |
| `/agent record off` | Stops writing the file. |
| `/agent do <line>` | Runs one command line from chat, as an AI client would. |
| `/agent help` | Lists these commands. |

## Tools

Only `act` can make the character do anything. Every other tool only reads.

| Tool | Answers with |
|---|---|
| `observe` | The latest session, body, vitals, stats, target and combat-mode records, the actions still pending, and the most recent outcomes. |
| `act` | Runs one command line and returns a handle with a status: `pending`, `done`, `refused` or `sent-as-chat`. |
| `outcome` | How the line with a handle ended: its outcome word, its shared class, and every record it produced. |
| `events` | Records after a cursor. With `waitSeconds` it waits for the next one, and `until` wakes it on a condition. |
| `nearby` | Objects around the character, nearest first, with distance, bearing and kind. |
| `inspect` | Everything the client holds about one object. |
| `spells` | Known spells, narrowed by `search`, including whether their components are carried. |
| `skills` | Skills with their training and values. |
| `buffs` | Enchantments on the character with the time remaining. |
| `inventory` | Carried items, narrowed by `search`, with the free pack slots. |
| `equipment` | What is worn and wielded, and what could be equipped. |
| `vendor` | The open vendor's listings with prices and stock. |
| `container` | The items in the open corpse or container. |
| `corpses` | Corpses in range, and whether each has been opened. |

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
| Read | `vitals`, `stats`, `location`, `snapshot`, `skills`, `buffs`, `spells [search]`, `nearby [kind] [range]`, `inspect <id>`, `inventory [search]`, `equipment`, `vendor`, `loot list`, `loot corpses [range]` |
| Chat | `say <text>`, `tell <name>, <message>`, `emote <text>` |
| Target | `target <id>`, `target nearest [kind]`, `untarget` |
| Motion | `walk [forward\|backward] [amount]`, `run [forward\|backward] [amount]`, `strafe left\|right [amount]`, `turn left\|right [amount]`, `turn to <degrees>`, `face <id>`, `jump [power]`, `stop [walking\|running\|strafing\|turning]`, `stance peace\|melee\|missile\|magic`, `cancel` |
| Magic | `cast <spell name or id> [on <id>]` |
| Objects | `use <id>`, `use <item id> on <id>`, `open <id>` |
| Items | `loot <item id>`, `drop <item id> [amount]`, `give <item id> to <id> [amount]`, `move <item id> to <container id> [amount]`, `equip <item id>`, `unequip <item id>` |
| Vendor | `buy <listing id or name> [quantity]`, `sell <item id> [amount]` |
| Combat | `attack [id] [power from 0 to 1] [high\|medium\|low]` |

An amount is meters, or degrees for a turn, or seconds when written like `20s`.
Walking or running, strafing and turning combine the way movement keys do:
after `run forward 60s`, `turn left 5` and `strafe right 2s` steer the run
without stopping it, and a new move replaces only a move of its own kind. The
client carries out each move itself, lands a turn on its exact angle, and
reports a move as blocked when the character stops making progress. A move
without an amount keeps going until `stop`, for at most thirty seconds, and the
player's own movement keys end every move at once. `go` and `goto` are refused,
because finding a route to a named place is not part of this plugin. Client
commands that end the session, kill the character, or change its player-killer
status are refused too. Any other line is handed to the client as chat or a
client command, so a model can make the character speak.

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
- Nothing listens until `/agent listen`, and `/agent stop` or closing the client
  stops it. There is no password: while it listens, any program running on this
  computer can connect and act as the character.
- Only `act` can make the character do anything, and it runs one line per call.

## Not included

Finding routes, autonomous combat, the headless host, and pushes from the
server are not part of this plugin. `GET /mcp` is refused, so clients do not open
an event stream; `events` with `waitSeconds` covers waiting for something to
happen.
