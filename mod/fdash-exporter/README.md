# Factorio Dashboard Exporter

Exports live factory statistics for an external dashboard — **without stalling the game**.

## Why this exists

The usual way to get data out of Factorio is RCON `/silent-command`. That works, but every
poll runs the whole query inside a single game tick. A command like
`surface.find_entities_filtered{type = "assembling-machine"}` walks every entity on the
surface. On a large modded base that is a hundred thousand entities, several times a minute,
all inside one tick. The result is exactly what you would expect: stutter.

This mod inverts the arrangement. It keeps its own view of the factory, updates it a little
bit every tick under a **hard budget**, and hands out a finished snapshot. Reading the data
costs nothing, because by then there is nothing left to compute.

## What that means in practice

- **Bounded cost per tick.** `fdash-entity-budget` (default 400) caps how many entities all
  collectors together may touch in one tick. Grow the factory and passes take longer — the
  UPS cost stays where you put it.
- **No repeated surface scans.** Entity lists are built once and then maintained from build
  events. Destroyed entities are dropped when a pass walks past them, so there is no
  expensive destroy-event handler firing during combat or deconstruction.
- **Rolling ore scan.** Ore patches are scanned chunk by chunk, a few chunks per tick. A full
  pass takes seconds to minutes depending on map size, which is plenty for remaining amounts
  and depletion estimates.
- **Cached drill coverage.** The amount of ore inside a drill's mining radius is measured per
  drill and cached; it is refreshed on a slow timer instead of every poll.
- **Consistent output.** Each collector publishes only when a full pass completes, so a
  snapshot is always internally consistent — just up to one pass old.

## Output

### Files (default)

`script-output/fdash/`:

| File | Contents |
| --- | --- |
| `index.json` | `{protocol, seq, tick, file}` — written **after** the snapshot |
| `snapshot-0.json` … `snapshot-2.json` | rotating snapshots |
| `prototypes.json` | one-shot export of recipes, items, resources, fluids |

Read `index.json` first, then the file it names. Because writes rotate through three slots,
a reader can never catch a half-written file.

Snapshot shape:

```json
{
  "protocol": 1,
  "seq": 128,
  "tick": 1234567,
  "jobs": {
    "meta":         { "tick": 1234500, "data": { } },
    "power@nauvis": { "tick": 1234550, "data": { } },
    "trains":       { "tick": 1234540, "data": { } }
  }
}
```

Per-surface jobs are keyed `job@surface`; global ones (`meta`, `trains`, `platforms`) use the
bare name. `seq` increases whenever any collector publishes — poll it to see if anything is new.

### Remote interface (for RCON)

```
/silent-command rcon.print(remote.call("fdash", "snapshot"))
```

| Call | Returns |
| --- | --- |
| `seq()` | current sequence number — cheap, poll this first |
| `snapshot()` | the full snapshot document |
| `get(key)` | one job fragment, e.g. `"power@nauvis"` |
| `keys()` | available job keys |
| `status()` | scan progress, budgets, registry sizes, collector errors |
| `set_research(name)` | queue a technology (validated inside the mod) |
| `export_prototypes()` | rerun the prototype export |

These calls do not compute anything — they hand back strings that were built earlier.

Also available in-game: `/fdash-status`.

## Collectors

| Job | Default interval | Contents |
| --- | --- | --- |
| `meta` | 10 s | version, surfaces, seed, mods, scan progress |
| `power` | 5 s | per electric network: production, consumption, satisfaction, generator capacity, accumulators |
| `assemblers` | 5 s | machines grouped by produced item: counts, statuses, missing ingredients |
| `production` | 10 s | produced/consumed per minute for every item and fluid |
| `trains` | 5 s | problem trains with cargo, plus totals |
| `logistics` | 5 s | robots per logistic network |
| `circuits` | 5 s | constant combinators whose description starts with `FDASH:` |
| `research_state` | 15 s | researchable technologies, lab count and speed |
| `drills` | 30 s | mining drills per resource: counts, theoretical max rate |
| `resources` | 60 s | ore patches: remaining amount, coverage, depletion estimate |
| `platforms` | 10 s | Space Age platforms: thruster fuel, warnings |
| `orbital` | 10 s | Space Age platform hub requests |

Intervals are the minimum gap between the *end* of one pass and the start of the next. On a
large base a pass may take longer than its interval; then it simply runs continuously.

## Settings

All runtime-global, changeable while the game runs.

| Setting | Default | Notes |
| --- | --- | --- |
| Enable exporter | on | master switch |
| Entity budget per tick | 400 | the main UPS knob |
| Chunk budget per tick | 4 | for the initial scan and the ore scan |
| File write interval | 300 ticks | 5 seconds |
| Write snapshot files | on | turn off if you read over RCON |
| Scan ore patches | on | the most expensive collector |
| Ore-under-drill refresh | 10 min | how often drill coverage is re-measured |

**Multiplayer note:** `helpers.write_file` writes on every peer. On a dedicated server that is
just the server. On a listen server, clients can turn *Write snapshot files* off.

## Tuning

Start with the defaults. If the dashboard updates too slowly for your taste, raise the entity
budget — the relationship is close to linear, and the cost is paid evenly rather than in
spikes. If you are chasing every last UPS on a megabase, lower it; the data simply gets older.

**Measured cost.** On a Pyanodons base (2600 assemblers, 660 drills, 20000 poles, 17400 ore
chunks, Factorio 2.0.77) at the default budget of 400, benchmarked over 1800 ticks with the
mod enabled and disabled:

- without the mod: 9.6 / 10.0 ms per tick
- with the mod: 10.3 / 11.1 ms per tick

So roughly **0.8–1.1 ms per tick**, about 5 % of the 16.7 ms a 60 UPS tick has to spend. It is
paid evenly: no spike in either run came from the exporter. That is the number the budget
scales — halve it and you roughly halve the cost, at the price of a longer pass.

`remote.call("fdash", "status")` reports registry sizes, which tells you how long a pass takes:
roughly `entities / budget` ticks.

## Compatibility

- Factorio 2.0. Space Age is optional — platform collectors disable themselves without it.
- No prototype changes, no GUI, no entities. Safe to add to and remove from an existing save.
- Reads the `player` force only.

## License

MIT — see `LICENSE`.
