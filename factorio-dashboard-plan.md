# Factorio Dashboard — project plan

> **Historical document.** This plan describes the first version, which collected purely over
> RCON `/silent-command` without a mod. That is exactly where it failed: the whole evaluation
> ran inside the game tick and noticeably slowed the server down — the `ModQuery` route kept
> in reserve in §5.2 is now the only one.
>
> The current design is described in [`README.md`](README.md) and
> [`mod/fdash-exporter/README.md`](mod/fdash-exporter/README.md). What this document says about
> data models, metrics and the frontend still holds; what it says about RCON snippets, job
> scheduling in the collector and chunk pagination is obsolete.

A second-screen dashboard for Factorio (Space Age) with a C# collector and a web frontend.
**No mod required** — data collection runs purely over `/silent-command` via RCON, so fellow
players do not have to install anything.

---

## 1. Architecture

```
┌────────────────────┐        RCON/TCP        ┌──────────────────────────┐
│ Factorio headless  │◄──────────────────────►│  Collector (C#, .NET 9)  │
│  (unmodified)      │  /silent-command ...   │  - RCON client           │
│                    │                        │  - Lua snippet registry  │
│  script-output/ ───┼───── optional ────────►│  - scan state (chunks)   │
└────────────────────┘   (large payloads)     │  - scheduler (jobs)      │
                                              │  - time-series writer    │
                                              └────────┬─────────────────┘
                                                       │
                                              ┌────────▼─────────────────┐
                                              │  SQLite (roll-up tiers)  │
                                              │  (alternative: DuckDB)   │
                                              └────────┬─────────────────┘
                                                       │
                                              ┌────────▼─────────────────┐
                                              │ ASP.NET Core Web API     │
                                              │  + SignalR (push)        │
                                              │  + static SPA            │
                                              └──────────────────────────┘
```

### Why no mod

Factorio syncs the mod list on connect. Every loaded mod — even a pure script mod with no
prototypes — has to exist on **all** clients. There is no "server-only" flag, because a mod
holds `storage` state and participates in the deterministic lockstep.

Since the dashboard only runs on the server and fellow players do not use it, requiring a mod
would be pure friction. Instead the collector sends the Lua code itself, as a string, on every
poll.

### What that changes

| | mod variant | `/silent-command` (chosen) |
|---|---|---|
| Client installation | required | **none** |
| Entity registry | incremental via events | full scan per poll |
| Spreading expensive scans over ticks | in the mod | **in the collector, across polls** |
| State between calls | `storage` | **C# side, in the collector** |
| Statistics queries (power, production) | O(1) | O(1), identical |

The critical point is the resource scan (§3.4) — which is therefore split into map sections on
the collector side. Assembler, train and logistics scans are fast enough even as one-shot
scans.

### Achievements

`/silent-command` marks the save as "commands used" and disables achievements — exactly like a
mod does. If achievements matter to you, that is a deal-breaker for **both** variants. Settle
it up front.

### Security

Anyone with RCON access can execute arbitrary Lua. Therefore:

- `--rcon-bind 127.0.0.1:27015` when collector and server run on the same machine
- Otherwise WireGuard or an SSH tunnel — RCON is unencrypted and has no business being exposed

---

## 2. Lua snippet layer

Instead of a mod, the collector keeps the Lua snippets as embedded resources.

### 2.1 Structure

```
Fdash.Collector/
  Lua/
    _prelude.lua        -- shared helpers, prepended to every snippet
    meta.lua
    prototypes.lua      -- one-shot export to script-output/
    assemblers.lua
    trains.lua
    power.lua
    resources_chunk.lua -- parameterised: chunk range
    logistics.lua
    platforms.lua
    production.lua
    circuits.lua
    set_research.lua    -- the only writing call
```

### 2.2 Call pattern

```
/silent-command rcon.print(helpers.table_to_json(<snippet>))
```

Snippets are minified before sending (comments and redundant whitespace stripped), because
command length counts against the RCON payload.

**Parameterisation:** placeholders in the snippet are substituted on the C# side, e.g.
`__SURFACE__` → `"nauvis"`, `__CHUNK_FROM__` / `__CHUNK_TO__` for the resource scan. Values
have to be escaped properly — surface names do come from a trusted source (the server itself),
but a snippet injection bug here would amount to remote code execution.

### 2.3 Prelude

Prepended to every snippet, defines the recurring helpers:

```lua
local F = game.forces.player
local function surf(n) return game.surfaces[n] end
local function statflow(stats, name, cat)
  return stats.get_flow_count{
    name = name, category = cat, precision_index =
    defines.flow_precision_index.one_minute
  }
end
```

### 2.4 Payload size

RCON responses fragment from roughly 4 kB up; Factorio handles that, but very large payloads
(> 1 MB) are risky. Countermeasures:

1. **Aggregates instead of raw data** — never return individual entities, always group in Lua
2. **`helpers.write_file`** for large snapshots into `script-output/`, the collector reads the file
3. Fall back to RCON when there is no shared file access (§5.4)

---

## 3. Data model per module

### 3.1 Assemblers / machines

Covers all crafting-machine-like types: `assembling-machine`, `furnace`, `rocket-silo`, plus
modded types via `prototypes` iteration.

```json
{
  "surface": "nauvis",
  "by_recipe": {
    "electronic-circuit": {
      "total": 240,
      "status": { "working": 231, "no_ingredients": 7, "output_full": 2 },
      "avg_speed": 3.75,
      "modded": false
    }
  },
  "no_recipe": 4
}
```

`entity.status` returns the `defines.entity_status` enum. Mapping the numeric values to
readable strings happens in the Lua snippet, so the frontend does not have to maintain an enum
table. Unknown (modded) status values pass through as `unknown_<n>`.

**Generic mod support:** no whitelist. All recipes and items come from `prototypes.recipe` /
`prototypes.item`.

**Localised names:** `entity.localised_name` cannot be resolved over RCON. Solution: a one-off
export of the prototype names when the collector starts (§5.4).

**Cost:** full scan per poll via `find_entities_filtered{type=...}`. On large bases, do the
aggregation directly in Lua and never return entity lists.

### 3.2 Trains

```json
{
  "problems": [
    { "id": 42, "surface": "nauvis", "state": "no_path", "schedule_station": "Iron Ore Load", "position": {"x": 0, "y": 0} },
    { "id": 51, "state": "destination_full", "schedule_station": "Smelter Unload" },
    { "id": 63, "state": "manual_control", "schedule_station": null }
  ],
  "totals": { "total": 87, "ok": 84, "problem": 3 }
}
```

Source: `game.train_manager.get_trains{}` → `train.state` (`defines.train_state`). Problem
states: `no_path`, `path_lost`, `no_schedule`, `destination_full`, `manual_control`,
`manual_control_stop`. Plus trains in automatic mode without a schedule.

Only problem trains are transmitted, the rest as counters — that keeps the payload small.

### 3.3 Power

```json
{
  "surface": "nauvis",
  "networks": [{
    "id": 1,
    "production": 1.24e9,
    "consumption": 1.19e9,
    "satisfaction": 0.96,
    "accumulators": { "charge": 0.72, "count": 400, "capacity_j": 2.0e9 },
    "by_producer": { "electric-solar-panel": 3.1e8, "nuclear-reactor": 9.3e8 },
    "by_consumer_group": { "assembling-machine": 4.2e8, "mining-drill": 1.1e8 }
  }]
}
```

Source: `force.get_electric_network_statistics(surface)`, or
`entity.electric_network_statistics` of any pole per network, with
`get_flow_count{name=..., category="input"/"output", precision_index=defines.flow_precision_index.one_minute}`.

Network identification: group poles by `electric_network_id`. Multiple isolated networks per
planet are all listed, sorted by size. Statistics queries are cheap — nothing changes here
compared to the mod variant.

### 3.4 Resources

```json
{
  "surface": "nauvis",
  "resources": {
    "iron-ore": {
      "infinite": false,
      "total_amount": 4.2e8,
      "covered_amount": 1.1e8,
      "drills": { "total": 320, "working": 298 },
      "rate_current": 1840.5,
      "rate_max": 1980.0,
      "depletion_seconds": 59782
    },
    "crude-oil": {
      "infinite": true,
      "rate_current": 4200.0,
      "rate_max": 4650.0,
      "yield_pct": 0.31
    }
  }
}
```

**This is the most expensive job.** `find_entities_filtered{type="resource"}` across a large
map can return tens of thousands of entities and visibly stall the server in a single tick —
in multiplayer, everyone notices.

**Chunked scan strategy (collector side):**

1. The collector fetches the surface's chunk bounds once
2. Each poll scans only one section (starting value: ~200 chunks), parameterised via
   `__CHUNK_FROM__` / `__CHUNK_TO__`
3. Partial results accumulate in C#
4. After the last section: publish the total, restart the cycle

At a 5 s poll and, say, 20 sections, that yields a complete resource picture every 100 seconds.
The section size belongs in the config and should be calibrated against the actual tick time on
your server.

Drills are scanned separately and more cheaply (far fewer entities), hence their own job with a
shorter interval.

**Computing `rate_max`** — sum over all drills of the theoretical rate at status `working`:

```
rate = mining_speed_prototype × speed_modifier(modules+beacons)
       × productivity_multiplier / mining_time_of_resource
```

For infinite resources, additionally the yield factor (`amount / normal_resource_amount`).

**`rate_current`:** via `force.get_item_production_statistics(surface).get_flow_count` for the
mined item — the actually measured rate. Fallback: `rate_max × (working_drills / total_drills)`
when the item comes from several sources.

**`depletion_seconds`:** `covered_amount / rate_current`. "Covered" means only ore under active
drill coverage. That is the more honest number, because untapped patches do not extend the
current output. Both values are delivered; the frontend shows `covered` primarily and `total`
as secondary information.

### 3.5 Logistics / robots

```json
{
  "surface": "nauvis",
  "networks": [{
    "id": 3,
    "roboports": 84,
    "logistic_robots": { "total": 1200, "idle": 340, "working": 810, "charging": 28, "waiting_for_charge": 22 },
    "construction_robots": { "total": 400, "idle": 395, "working": 5, "charging": 0, "waiting_for_charge": 0 },
    "charging_slots": { "used": 28, "total": 336 }
  }]
}
```

Source per network: `network.available_logistic_robots`, `network.all_logistic_robots`, plus
iteration over `network.cells` for `charging_robot_count` and `to_charge_robot_count`. The
identity is `working = all - available - charging - waiting`.

`waiting_for_charge` is the most important alarm value — a high share means the roboports are
undersized.

### 3.6 Platforms (Space Age)

```json
{
  "platforms": [{
    "name": "Vulcanus Hauler",
    "state": "on_the_path",
    "location": "nauvis → vulcanus",
    "speed": 142.3,
    "thrusters": { "count": 8 },
    "fuel": {
      "thruster-fuel":     { "current": 12400, "max": 24000, "pct": 0.52 },
      "thruster-oxidizer": { "current":  8100, "max": 24000, "pct": 0.34 }
    },
    "hub_contents": { "iron-plate": 4000 },
    "warnings": ["oxidizer_low"]
  }]
}
```

`max` = sum of the fluid capacities of all thrusters plus connected tanks in the network.
Warning at `pct < 0.25`, or when the oxidizer/fuel ratio drifts far apart (thrusters need
both).

### 3.7 Orbital requests

```json
{
  "surface": "vulcanus",
  "requests": [
    { "item": "blue-chip", "requested": 2000, "delivered": 0, "waiting_platforms": 1 }
  ]
}
```

Source: planetary logistics groups / `surface.platform` links, and the hub requests of
platforms whose destination is this planet. The Space Age API is still somewhat in motion here
— detailed exploration in phase 3.

### 3.8 Production ratio and stall warning

```json
{
  "surface": "nauvis",
  "items": [
    { "item": "steel-plate", "produced_per_min": 1200, "consumed_per_min": 1450, "ratio": 0.83, "trend": "falling" }
  ],
  "stalled": [
    { "item": "low-density-structure", "reason": "no_ingredients", "machines_affected": 12, "since_seconds": 340 }
  ]
}
```

Source: `force.get_item_production_statistics(surface)` with `flow_precision_index.one_minute`.

**Stall logic:** an item is only reported when

- `produced_per_min == 0` (or below a threshold), **and**
- at least one machine with that recipe exists, **and**
- its status is `no_ingredients` / `no_power` / `low_power` / `no_fluid` — **not** `output_full`

That correctly drops "production stopped because storage is full".

`since_seconds` necessarily lives in the collector — without a mod's `storage` there is no
state between polls on the Lua side. That fits better anyway, because the value then survives
server restarts.

**Correlation:** stall detection needs data from two jobs (`production` + `assemblers`). It
therefore runs in the collector as a derived job, not in Lua.

### 3.9 Tagged circuits

Opt-in by convention: a constant combinator is named or described with the prefix `FDASH:`.
Example: `FDASH:Oil reserve` on a combinator, whose signals then appear in the dashboard as a
named tile.

```json
{
  "tags": [
    { "label": "Oil reserve", "surface": "nauvis", "signals": { "crude-oil": 240000, "petroleum-gas": 18000 } }
  ]
}
```

Collected via `find_entities_filtered{type="constant-combinator"}` plus a check on
`entity.combinator_description` or the backer name.

---

## 4. Command protocol

### 4.1 Standard call

```
/silent-command rcon.print(helpers.table_to_json( <minified snippet> ))
```

### 4.2 Discovery at startup

```
/silent-command rcon.print(helpers.table_to_json({
  version  = "2.0.x",
  save_name = ...,
  surfaces = {...},
  tick     = game.tick,
  mods     = {...}
}))
```

### 4.3 Save identification

Without a mod there is no persistent `storage` and therefore no self-assigned `save_id`.
Substitute: a stable fingerprint from values that are unique per save and constant across
restarts — map seed, force creation tick, surface list:

```lua
local seed = game.surfaces.nauvis.map_gen_settings.seed
```

The collector hashes those into a `save_id`. When the server switches saves, the fingerprint
changes and a new time series is created automatically.

**Edge case:** two saves from the same seed (e.g. a copy for testing) collide. Should that
become relevant, `save_name` from the server settings can go into the hash — at which point a
renamed save collides instead. The seed is enough to start with; the configuration also allows
a manually set `save_id` as an override.

### 4.4 Batching

Combine several jobs per round trip into one snippet:

```
/silent-command rcon.print(helpers.table_to_json({
  power = (function() ... end)(),
  trains = (function() ... end)(),
  logistics = (function() ... end)()
}))
```

Fewer round trips, but: everything runs in **one** tick. Expensive jobs (resources) therefore
never belong in a batch, always in their own call.

### 4.5 The writing call

Only one, for auto-research (§6):

```
/silent-command
  local t = game.forces.player.technologies["logistics-3"]
  if t and not t.researched and t.enabled then
    game.forces.player.add_research(t)
    rcon.print("ok")
  else rcon.print("rejected") end
```

Validation happens in the snippet itself, not only in the collector.

---

## 5. Collector and persistence (C#)

### 5.1 Project structure

```
FactorioDashboard.sln
  src/
    Fdash.Core/        -- models, DTOs, interfaces
    Fdash.Rcon/        -- RCON client (Source RCON protocol)
    Fdash.Lua/         -- snippets (embedded), minifier, parameterisation
    Fdash.Collector/   -- scheduler, job runner, scan state, mapping
    Fdash.Storage/     -- time-series repository
    Fdash.Api/         -- ASP.NET Core, SignalR, static hosting
  web/                 -- frontend sources
```

One self-contained deployment: `dotnet publish -r linux-x64` or `win-x64`, runs on both
platforms. Configuration via `appsettings.json` plus environment variables.

### 5.2 Abstraction for a later mod option

```csharp
public interface IGameQuery
{
    Task<JsonDocument> ExecuteAsync(string job, IReadOnlyDictionary<string,string>? args, CancellationToken ct);
}
```

Implementations:

- `ScriptQuery` — `/silent-command` with Lua snippets **(default)**
- `ModQuery` — `remote.call`, in case a mod turns out to be necessary later

The data models are identical either way. This abstraction costs almost nothing and keeps the
door open without having to build a mod now.

### 5.3 Scheduler

```csharp
public sealed record JobSpec(string Name, TimeSpan Interval, bool Persist, bool Batchable);

new JobSpec("power",       TimeSpan.FromSeconds(5),  Persist: true,  Batchable: true),
new JobSpec("assemblers",  TimeSpan.FromSeconds(5),  Persist: true,  Batchable: true),
new JobSpec("trains",      TimeSpan.FromSeconds(5),  Persist: false, Batchable: true),
new JobSpec("logistics",   TimeSpan.FromSeconds(5),  Persist: true,  Batchable: true),
new JobSpec("circuits",    TimeSpan.FromSeconds(5),  Persist: true,  Batchable: true),
new JobSpec("production",  TimeSpan.FromSeconds(10), Persist: true,  Batchable: true),
new JobSpec("platforms",   TimeSpan.FromSeconds(10), Persist: true,  Batchable: false),
new JobSpec("drills",      TimeSpan.FromSeconds(30), Persist: true,  Batchable: false),
new JobSpec("resources",   TimeSpan.FromSeconds(5),  Persist: true,  Batchable: false), // chunked, full cycle ~100s
```

Resilience: exponential backoff on RCON errors, automatic reconnect, save-change detection via
the fingerprint (§4.3) in every discovery response.

**Adaptive throttling:** the collector measures the execution time of each call. If it rises
above a threshold, the chunk size of the resource scan is reduced automatically. That prevents
a growing save from silently causing server lag at some point.

### 5.4 Prototype export

At startup the collector sends a one-off snippet that writes all prototype names and
localisations to `script-output/fdash_prototypes.json`.

- **Shared file access available** (same machine / shared volume): the collector reads the file
  directly → `ScriptOutputPath` in the config
- **No file access:** fall back to RCON in several pages (paginated, since the list gets large
  with many mods)

The export runs again on every collector start — it costs nothing and picks up mod changes on
the server automatically.

### 5.5 Time-series storage

**Recommendation: SQLite with manual roll-up rather than RRD.**

RRD has the right semantics (fixed size, automatic aggregation), but: no good .NET library, no
ad-hoc queries, and it is poorly suited to dynamic series names (with modded items the metrics
are not known in advance).

```sql
CREATE TABLE samples (
  save_id   TEXT    NOT NULL,
  metric    TEXT    NOT NULL,   -- 'power.production'
  labels    TEXT    NOT NULL,   -- 'surface=nauvis,network=1'
  ts        INTEGER NOT NULL,   -- unix seconds
  value     REAL    NOT NULL,
  PRIMARY KEY (save_id, metric, labels, ts)
) WITHOUT ROWID;
```

Roll-up job (hourly):

| Resolution | Retention | Purpose |
|---|---|---|
| 5 s | 6 h | live graphs |
| 1 min | 7 days | daily view |
| 15 min | 90 days | long term |
| 1 h | unlimited | history |

Each tier its own table, aggregate = avg/min/max. Alternative as the data grows: DuckDB
(columnar, good compression, usable .NET bindings) — which is why the repository stays behind
an interface, so a later switch is cheap.

---

## 6. Auto-research

**Metric "fastest research":**

```
time = Σ(pack_count_i × units) / effective_lab_rate
```

Precondition: all required pack types are available. "Available" means the pack exists in the
logistic/lab network **and** is actively being produced (production rate > 0). Without the
second condition the algorithm picks a tech that blocks immediately.

Effective lab rate = `number of active labs × lab_speed × (1 + research_speed_bonus)`.

**Algorithm:**

1. Candidates = all techs with satisfied prerequisites, not `researched`, not blacklisted
2. Filter: all `research_unit_ingredients` are packs being produced
3. Score = estimated time in seconds
4. Lowest score wins; on a tie, the lower tech level

The selection runs **in the collector**, not in Lua — the production data and the blacklist
config already live there.

**Safety rules:**

- Only active when the research queue is empty
- Configurable blacklist (prefix matching, e.g. all `*-productivity-*` infinite techs)
- Optional: maximum level for infinite techs
- Toggle in the frontend, state persisted in the collector — a save reload does not silently
  re-enable the automation
- Server-side validation in the snippet itself (§4.5)
- Audit log of every change: for an automation that writes into the save, it should be
  traceable what was set, when, and why

---

## 7. Frontend

**Stack:** React + TypeScript + Vite, Tailwind, uPlot for graphs (considerably faster than
Recharts with many data points). Build output as `wwwroot` in the ASP.NET app — one process,
one port, no CORS.

**Transport:** a SignalR hub pushes snapshots, no polling in the browser. Historical data over
REST (`GET /api/series?metric=...&from=...&to=...&resolution=...`).

### Layout — second screen, readable without interaction

```
┌─ Overview ──────────────────────────────────────────────────┐
│ [Nauvis] [Vulcanus] [Fulgora] [Gleba] [Aquilo] [Platforms]  │  ← planet tabs
├──────────────┬──────────────┬──────────────┬────────────────┤
│ POWER        │ RESEARCH     │ ALERTS       │ ROBOTS         │
│ 1.24 / 1.30GW│ Logistics 3  │ ⚠ 3 trains   │ 810/1200 active│
│ ████████░ 96%│ ~4m 12s      │ ⚠ Steel 0.83 │ ⚠ 22 waiting   │
│ Accu ▓▓▓ 72% │ [auto: ON]   │ ⚠ LDS stalled│    to charge   │
├──────────────┴──────────────┴──────────────┴────────────────┤
│ MACHINES (by recipe, sorted by problems)                    │
│ electronic-circuit   240  ▓▓▓▓▓▓▓▓▓░ 231 ok  7 no_ingr      │
│ steel-plate          180  ▓▓▓▓▓▓░░░░ 120 ok 60 output_full  │
├──────────────────────────────┬──────────────────────────────┤
│ ORES            [scan 14/20] │ PRODUCTION RATIO             │
│ iron-ore  1840/1980  ~16h    │ steel-plate    0.83 ↓        │
│ copper    1200/1200  ~22h    │ green-circuit  1.02 →        │
└──────────────────────────────┴──────────────────────────────┘
```

The scan progress (`14/20`) on the resources panel makes it visible that the ore numbers come
from a running cycle and are not second-accurate. Plus a timestamp: "last complete: 47s ago".

**Tabs:** Overview · Machines · Trains · Power · Resources · Logistics · Platforms ·
Production · Circuits · History · Settings

**Traffic-light colour coding** throughout: green ok, yellow warning, red critical. The point
of a second-screen dashboard is that a glance suffices — so always sort problems to the top and
make them visually dominant instead of letting them disappear into tables.

---

## 8. Setup and operation

### 8.1 Server side

No mod, no installation — just enable RCON:

```bash
./bin/x64/factorio \
  --start-server saves/mysave.zip \
  --server-settings data/server-settings.json \
  --rcon-bind 127.0.0.1:27015 \
  --rcon-password "secret"
```

Use `--rcon-bind 127.0.0.1` when collector and server run on the same machine. Otherwise
`--rcon-port` plus a WireGuard or SSH tunnel.

### 8.2 Collector side

`appsettings.json`:

```json
{
  "Factorio": {
    "Host": "127.0.0.1",
    "RconPort": 27015,
    "RconPassword": "secret",
    "ScriptOutputPath": "/opt/factorio/script-output",
    "SaveIdOverride": null
  },
  "Scan": {
    "ResourceChunksPerPoll": 200,
    "MaxCommandDurationMs": 50
  }
}
```

Start:

```bash
./Fdash.Api            # or: dotnet Fdash.Api.dll
```

Dashboard at `http://localhost:5000`.

### 8.3 Startup sequence

1. Establish the RCON connection
2. Discovery call → version, surfaces, save fingerprint
3. Trigger and read the prototype export
4. Start the scheduler

### 8.4 Testing the connection

Before the first line of code:

```bash
mcrcon -H 127.0.0.1 -P 27015 -p secret "/silent-command rcon.print(game.tick)"
```

If a number comes back, the data path works and phase 0 is essentially validated.

---

## 9. Implementation phases

| Phase | Content | Result |
|---|---|---|
| **0** | RCON client, snippet infrastructure, discovery, save fingerprint, prototype export | connection stands, data flows |
| **1** | Power, assemblers, trains + SQLite storage + minimal dashboard | first usable version |
| **2** | Resources (chunked scan), drills, logistics, production ratio, stall detection | core feature set |
| **3** | Space Age: platforms, orbital requests, multi-surface tabs | Space Age complete |
| **4** | Auto-research (read-only preview first, then write access) | automation |
| **5** | Time-series graphs, roll-up, history tab | history |
| **6** | Circuits, icons, polish, configurable alert thresholds | done |

**Phase 2 is the riskiest** — that is where it shows whether the chunked resource scan runs
without noticeable server lag. So measure early against the real save, not against a small test
map. If it stalls there, that is the moment to retrofit the `ModQuery` variant (§5.2) for that
one job.

**Phase 4 deliberately comes after the core:** auto-research is the only feature that writes.
Build it only once the data path is trustworthy — and even then as a preview first ("would set
X now"), so the selection logic can be checked against your own judgement before it runs
autonomously.

---

## 10. Open points

1. **Achievements** — settle with fellow players up front: `/silent-command` disables them
   permanently for the save. Highest priority, because it affects the entire project.
2. **Measure tick cost** — how expensive is an assembler full scan on your actual save? That
   determines whether 5 s intervals are realistic.
3. **Space Age API for orbital requests** — verify against the current docs; the platform
   logistics API changed between 2.0 releases.
4. **Alert thresholds** — hard-coded or configurable per save? Recommendation: JSON config with
   sensible defaults.
5. **Authentication** — LAN only, or reachable through a reverse proxy? For the latter, at
   least basic auth in front of the SignalR hub, because the auto-research endpoint writes.
6. **Icons** — extracting modded item icons from the mod zips would be feasible, but it is
   work. Phase 6, or drop it?

---

## 11. Decisions (already settled)

| Topic | Decision |
|---|---|
| Transport | RCON |
| Data collection | **`/silent-command`, no mod** (fellow players install nothing) |
| Operating mode | host / dedicated server only |
| Space Age | yes, including generic modded-item support |
| Backend | C#, Linux + Windows |
| Poll interval | 5 s by default, longer for expensive jobs |
| Resource scan | chunked across several polls, accumulated in the collector |
| Machine diagnostics | `entity.status` is enough, aggregated per recipe |
| Grouping | per planet/surface |
| Train problems | state-based only, no "waiting unusually long" |
| Resources | per planet; for infinite ones only current/max extraction rate |
| `rate_max` | all miners working (theoretical maximum) |
| Auto-research | fastest with currently available packs, only on an empty queue, writes actively |
| History | time-series DB with roll-up |
| Multi-save | yes, via fingerprint (map seed hash) |
| Layout | dashboard overview + detail tabs |
