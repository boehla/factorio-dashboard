# Factorio Dashboard — Projektplan

> **Historisches Dokument.** Dieser Plan beschreibt die erste Fassung, die ohne Mod
> ausschließlich über RCON `/silent-command` sammelte. Genau daran ist sie gescheitert: die
> Auswertung lief komplett im Game-Tick und hat den Server spürbar ausgebremst — der in §5.2
> als Reserve vorgesehene `ModQuery`-Weg ist inzwischen der einzige.
>
> Der aktuelle Aufbau steht in [`README.md`](README.md) und
> [`mod/fdash-exporter/README.md`](mod/fdash-exporter/README.md). Was hier über Datenmodelle,
> Metriken und Frontend steht, gilt weiterhin; was über RCON-Snippets, Job-Scheduling im
> Collector und Chunk-Paginierung steht, ist überholt.

Ein Zweitbildschirm-Dashboard für Factorio (Space Age) mit C#-Collector und Web-Frontend.
**Kein Mod erforderlich** — die Datenerfassung läuft ausschließlich über `/silent-command` via RCON,
damit Mitspieler nichts installieren müssen.

---

## 1. Architektur

```
┌────────────────────┐        RCON/TCP        ┌──────────────────────────┐
│ Factorio Headless  │◄──────────────────────►│  Collector (C#, .NET 9)  │
│  (unmodifiziert)   │  /silent-command ...   │  - RCON-Client           │
│                    │                        │  - Lua-Snippet-Registry  │
│  script-output/ ───┼───── optional ────────►│  - Scan-State (Chunks)   │
└────────────────────┘   (großer Payload)     │  - Scheduler (Jobs)      │
                                              │  - Time-Series-Writer    │
                                              └────────┬─────────────────┘
                                                       │
                                              ┌────────▼─────────────────┐
                                              │  SQLite (Roll-up-Tiers)  │
                                              │  (Alternative: DuckDB)   │
                                              └────────┬─────────────────┘
                                                       │
                                              ┌────────▼─────────────────┐
                                              │ ASP.NET Core Web API     │
                                              │  + SignalR (Push)        │
                                              │  + statisches SPA        │
                                              └──────────────────────────┘
```

### Warum kein Mod

Factorio synchronisiert die Mod-Liste beim Verbinden. Jedes geladene Mod — auch ein reines
Script-Mod ohne Prototypen — muss auf **allen** Clients liegen. Es gibt keinen "server-only"-Flag,
weil das Mod `storage`-State hält und im Determinismus-Lockstep mitläuft.

Da das Dashboard nur auf dem Server läuft und Mitspieler es nicht nutzen, wäre eine Mod-Pflicht
reiner Reibungsverlust. Stattdessen schickt der Collector den Lua-Code bei jedem Poll selbst als
String.

### Was dadurch anders wird

| | Mod-Variante | `/silent-command` (gewählt) |
|---|---|---|
| Client-Installation | nötig | **keine** |
| Entity-Registry | inkrementell via Events | Full-Scan pro Poll |
| Tick-Verteilung teurer Scans | im Mod | **im Collector, über mehrere Polls** |
| State zwischen Aufrufen | `storage` | **C#-seitig im Collector** |
| Statistik-Abfragen (Strom, Produktion) | O(1) | O(1), identisch |

Der kritische Punkt ist der Ressourcen-Scan (§3.4) — der wird deshalb Collector-seitig in
Kartenausschnitte zerlegt. Assembler-, Zug- und Logistik-Scans sind auch als Einmal-Scan
schnell genug.

### Achievements

`/silent-command` markiert das Save als "commands used" und deaktiviert Achievements —
genau wie ein Mod. Falls euch Achievements wichtig sind, ist das ein K.-o.-Kriterium für
**beide** Varianten. Vorab klären.

### Sicherheit

Wer RCON-Zugriff hat, kann beliebiges Lua ausführen. Deshalb:

- `--rcon-bind 127.0.0.1:27015`, wenn Collector und Server auf derselben Maschine laufen
- Sonst WireGuard oder SSH-Tunnel — RCON ist unverschlüsselt und gehört nicht offen ins Netz

---

## 2. Lua-Snippet-Layer

Statt eines Mods hält der Collector die Lua-Snippets als eingebettete Ressourcen.

### 2.1 Struktur

```
Fdash.Collector/
  Lua/
    _prelude.lua        -- gemeinsame Helper, wird jedem Snippet vorangestellt
    meta.lua
    prototypes.lua      -- einmaliger Export nach script-output/
    assemblers.lua
    trains.lua
    power.lua
    resources_chunk.lua -- parametrisiert: Chunk-Bereich
    logistics.lua
    platforms.lua
    production.lua
    circuits.lua
    set_research.lua    -- einziger schreibender Call
```

### 2.2 Aufrufmuster

```
/silent-command rcon.print(helpers.table_to_json(<snippet>))
```

Snippets werden vor dem Senden minifiziert (Kommentare und überflüssige Whitespaces raus),
weil die Command-Länge in den RCON-Payload eingeht.

**Parametrisierung:** Platzhalter im Snippet werden C#-seitig ersetzt, z.B.
`__SURFACE__` → `"nauvis"`, `__CHUNK_FROM__` / `__CHUNK_TO__` für den Ressourcen-Scan.
Werte müssen sauber escaped werden — Surface-Namen kommen zwar aus einer vertrauenswürdigen
Quelle (dem Server selbst), aber ein Snippet-Injection-Bug wäre hier gleichbedeutend mit
Remote-Code-Execution.

### 2.3 Prelude

Jedem Snippet vorangestellt, definiert wiederkehrende Helper:

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

### 2.4 Payload-Größe

RCON-Antworten sind ab ~4 kB fragmentiert; Factorio handhabt das, aber sehr große Payloads
(> 1 MB) sind riskant. Gegenmaßnahmen:

1. **Aggregate statt Rohdaten** — nie einzelne Entities zurückgeben, immer schon in Lua gruppieren
2. **`helpers.write_file`** für große Snapshots nach `script-output/`, der Collector liest die Datei
3. Fallback auf RCON, wenn kein gemeinsamer Dateizugriff besteht (§5.4)

---

## 3. Datenmodell pro Modul

### 3.1 Assembler / Maschinen

Erfasst werden alle crafting-machine-artigen Typen: `assembling-machine`, `furnace`,
`rocket-silo`, plus modded Typen via `prototypes`-Iteration.

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

`entity.status` liefert das `defines.entity_status`-Enum. Das Mapping der numerischen Werte auf
lesbare Strings passiert im Lua-Snippet, damit das Frontend keine Enum-Tabelle pflegen muss.
Unbekannte (modded) Status-Werte werden als `unknown_<n>` durchgereicht.

**Generische Mod-Unterstützung:** keine Whitelist. Alle Rezepte/Items kommen aus
`prototypes.recipe` / `prototypes.item`.

**Lokalisierte Namen:** `entity.localised_name` lässt sich RCON-seitig nicht auflösen. Lösung:
einmaliger Export der Prototyp-Namen beim Collector-Start (§5.4).

**Aufwand:** Full-Scan pro Poll über `find_entities_filtered{type=...}`. Bei großen Basen die
Aggregation direkt in Lua durchführen, nie Entity-Listen zurückgeben.

### 3.2 Züge

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

Quelle: `game.train_manager.get_trains{}` → `train.state` (`defines.train_state`).
Problem-States: `no_path`, `path_lost`, `no_schedule`, `destination_full`, `manual_control`,
`manual_control_stop`. Zusätzlich: Züge ohne Schedule im Automatik-Modus.

Nur Problemzüge werden übertragen, der Rest als Zähler — hält den Payload klein.

### 3.3 Strom

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

Quelle: `force.get_electric_network_statistics(surface)` bzw. `entity.electric_network_statistics`
eines beliebigen Pols pro Netz, mit
`get_flow_count{name=..., category="input"/"output", precision_index=defines.flow_precision_index.one_minute}`.

Netz-Identifikation: Pole nach `electric_network_id` gruppieren. Mehrere isolierte Netze pro
Planet werden alle gelistet, sortiert nach Größe. Statistik-Abfragen sind billig — hier ändert
sich gegenüber der Mod-Variante nichts.

### 3.4 Ressourcen

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

**Das ist der teuerste Job.** `find_entities_filtered{type="resource"}` über eine große Karte
kann zehntausende Entities liefern und in einem einzigen Tick den Server spürbar stocken lassen —
im Multiplayer merken das alle.

**Chunked-Scan-Strategie (Collector-seitig):**

1. Collector holt einmalig die Chunk-Bounds der Surface
2. Pro Poll wird nur ein Ausschnitt gescannt (Startwert: ~200 Chunks), parametrisiert über
   `__CHUNK_FROM__` / `__CHUNK_TO__`
3. Teilergebnisse werden in C# akkumuliert
4. Nach dem letzten Ausschnitt: Gesamtergebnis publizieren, Zyklus neu starten

Bei 5s-Poll und z.B. 20 Ausschnitten ergibt das einen vollständigen Ressourcen-Stand alle
100 Sekunden. Die Ausschnittsgröße gehört in die Config und sollte anhand der tatsächlichen
Tick-Zeit auf eurem Server kalibriert werden.

Drills werden separat und günstiger gescannt (deutlich weniger Entities), deshalb eigener Job
mit kürzerem Intervall.

**Berechnung `rate_max`** — Summe über alle Drills der theoretischen Rate bei Status `working`:

```
rate = mining_speed_prototype × speed_modifier(modules+beacons)
       × productivity_multiplier / mining_time_of_resource
```

Für unendliche Ressourcen zusätzlich der Yield-Faktor (`amount / normal_resource_amount`).

**`rate_current`:** über `force.get_item_production_statistics(surface).get_flow_count` für das
geförderte Item — die real gemessene Rate. Fallback: `rate_max × (working_drills / total_drills)`,
wenn das Item aus mehreren Quellen kommt.

**`depletion_seconds`:** `covered_amount / rate_current`. "Covered" = nur Erz unter aktiven
Drill-Abdeckungen. Das ist die ehrlichere Zahl, weil unerschlossene Patches die aktuelle
Fördermenge nicht verlängern. Beide Werte werden ausgeliefert; das Frontend zeigt primär
`covered`, `total` als Sekundärinfo.

### 3.5 Logistik / Roboter

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

Quelle pro Netzwerk: `network.available_logistic_robots`, `network.all_logistic_robots`, plus
Iteration über `network.cells` für `charging_robot_count` und `to_charge_robot_count`.
Es gilt `working = all - available - charging - waiting`.

`waiting_for_charge` ist der wichtigste Alarmwert — ein hoher Anteil bedeutet
Roboport-Unterversorgung.

### 3.6 Plattformen (Space Age)

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

`max` = Summe der Fluid-Kapazitäten aller Thruster + verbundener Tanks im Netz. Warnung bei
`pct < 0.25` oder wenn das Oxidizer/Fuel-Verhältnis stark abweicht (Thruster brauchen beides).

### 3.7 Orbital Requests

```json
{
  "surface": "vulcanus",
  "requests": [
    { "item": "blue-chip", "requested": 2000, "delivered": 0, "waiting_platforms": 1 }
  ]
}
```

Quelle: Planeten-Logistikgruppen / `surface.platform`-Verknüpfungen und die Hub-Requests der
Plattformen mit Ziel = dieser Planet. Die Space-Age-API ist hier noch etwas in Bewegung —
Detailerkundung in Phase 3.

### 3.8 Produktions-Ratio & Fehlproduktions-Warnung

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

Quelle: `force.get_item_production_statistics(surface)` mit `flow_precision_index.one_minute`.

**Stall-Logik:** Ein Item wird nur gemeldet, wenn

- `produced_per_min == 0` (bzw. unter Schwellwert), **und**
- mindestens eine Maschine mit diesem Rezept existiert, **und**
- deren Status `no_ingredients` / `no_power` / `low_power` / `no_fluid` ist — **nicht** `output_full`

Damit fällt "Produktion steht, weil Lager voll" korrekt weg.

`since_seconds` führt zwingend der Collector — ohne Mod-`storage` gibt es Lua-seitig keinen
Zustand zwischen Polls. Das passt ohnehin besser, weil der Wert dann Neustarts des Servers
übersteht.

**Korrelation:** Die Stall-Erkennung braucht Daten aus zwei Jobs (`production` + `assemblers`).
Sie läuft deshalb im Collector als abgeleiteter Job, nicht in Lua.

### 3.9 Getaggte Circuits

Opt-in per Konvention: ein Constant Combinator wird mit dem Präfix `FDASH:` benannt bzw.
beschrieben. Beispiel: `FDASH:Ölvorrat` an einem Kombinator, dessen Signale dann im Dashboard
als benannte Kachel erscheinen.

```json
{
  "tags": [
    { "label": "Ölvorrat", "surface": "nauvis", "signals": { "crude-oil": 240000, "petroleum-gas": 18000 } }
  ]
}
```

Erfassung über `find_entities_filtered{type="constant-combinator"}` + Prüfung auf
`entity.combinator_description` oder Backer-Name.

---

## 4. Command-Protokoll

### 4.1 Standard-Call

```
/silent-command rcon.print(helpers.table_to_json( <minifiziertes Snippet> ))
```

### 4.2 Discovery beim Start

```
/silent-command rcon.print(helpers.table_to_json({
  version  = "2.0.x",
  save_name = ...,
  surfaces = {...},
  tick     = game.tick,
  mods     = {...}
}))
```

### 4.3 Save-Identifikation

Ohne Mod gibt es kein persistentes `storage` und damit keine selbst vergebene `save_id`.
Ersatz: ein stabiler Fingerprint aus Werten, die pro Save eindeutig und über Neustarts konstant
sind — Map-Seed, Force-Erstellungstick, Surface-Liste:

```lua
local seed = game.surfaces.nauvis.map_gen_settings.seed
```

Der Collector bildet daraus einen Hash = `save_id`. Wechselt der Server das Save, ändert sich
der Fingerprint und es entsteht automatisch eine neue Zeitreihe.

**Grenzfall:** Zwei Saves aus demselben Seed (z.B. eine Kopie zum Testen) kollidieren. Falls
das relevant wird, kann der `save_name` aus den Server-Settings in den Hash einfließen — dann
kollidiert dafür ein umbenanntes Save. Für den Anfang reicht der Seed; die Konfiguration
erlaubt zusätzlich eine manuell gesetzte `save_id` als Override.

### 4.4 Batching

Mehrere Jobs pro Roundtrip in ein Snippet zusammenfassen:

```
/silent-command rcon.print(helpers.table_to_json({
  power = (function() ... end)(),
  trains = (function() ... end)(),
  logistics = (function() ... end)()
}))
```

Reduziert Roundtrips, aber: alles läuft in **einem** Tick. Teure Jobs (Ressourcen) gehören
deshalb nie ins Batch, sondern immer in einen eigenen Call.

### 4.5 Schreibender Call

Nur einer, für Auto-Research (§6):

```
/silent-command
  local t = game.forces.player.technologies["logistics-3"]
  if t and not t.researched and t.enabled then
    game.forces.player.add_research(t)
    rcon.print("ok")
  else rcon.print("rejected") end
```

Validierung passiert im Snippet selbst, nicht nur im Collector.

---

## 5. Collector & Persistenz (C#)

### 5.1 Projektstruktur

```
FactorioDashboard.sln
  src/
    Fdash.Core/        -- Modelle, DTOs, Interfaces
    Fdash.Rcon/        -- RCON-Client (Source RCON Protocol)
    Fdash.Lua/         -- Snippets (embedded), Minifier, Parametrisierung
    Fdash.Collector/   -- Scheduler, Job-Runner, Scan-State, Mapping
    Fdash.Storage/     -- Time-Series Repository
    Fdash.Api/         -- ASP.NET Core, SignalR, statisches Hosting
  web/                 -- Frontend-Sourcen
```

Ein self-contained Deployment: `dotnet publish -r linux-x64` bzw. `win-x64`, läuft auf beiden
Plattformen. Konfiguration über `appsettings.json` + Umgebungsvariablen.

### 5.2 Abstraktion für spätere Mod-Option

```csharp
public interface IGameQuery
{
    Task<JsonDocument> ExecuteAsync(string job, IReadOnlyDictionary<string,string>? args, CancellationToken ct);
}
```

Implementierungen:

- `ScriptQuery` — `/silent-command` mit Lua-Snippets **(Standard)**
- `ModQuery` — `remote.call`, falls sich später herausstellt, dass ein Mod nötig ist

Die Datenmodelle sind in beiden Fällen identisch. Diese Abstraktion kostet fast nichts und hält
die Tür offen, ohne dass jetzt ein Mod gebaut werden muss.

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
new JobSpec("resources",   TimeSpan.FromSeconds(5),  Persist: true,  Batchable: false), // chunked, voller Zyklus ~100s
```

Resilienz: exponentielles Backoff bei RCON-Fehlern, automatischer Reconnect, Erkennung von
Save-Wechsel über den Fingerprint (§4.3) in jeder Discovery-Antwort.

**Adaptive Drosselung:** Der Collector misst die Ausführungsdauer jedes Calls. Steigt sie über
einen Schwellwert, wird die Chunk-Größe des Ressourcen-Scans automatisch reduziert. Das
verhindert, dass ein wachsendes Save irgendwann unbemerkt Server-Lag verursacht.

### 5.4 Prototyp-Export

Beim Start schickt der Collector einmalig ein Snippet, das alle Prototyp-Namen und
Lokalisierungen nach `script-output/fdash_prototypes.json` schreibt.

- **Gemeinsamer Dateizugriff vorhanden** (gleiche Maschine / gemeinsames Volume):
  Collector liest die Datei direkt → `ScriptOutputPath` in der Config
- **Kein Dateizugriff:** Fallback über RCON in mehreren Seiten (paginiert, da die Liste bei
  vielen Mods groß wird)

Der Export läuft bei jedem Collector-Start neu — kostet nichts und fängt Mod-Änderungen am
Server automatisch ab.

### 5.5 Zeitreihen-Speicher

**Empfehlung: SQLite mit manuellem Roll-up statt RRD.**

RRD hat zwar die passende Semantik (feste Größe, automatische Aggregation), aber: keine gute
.NET-Bibliothek, keine Ad-hoc-Queries, schlecht für dynamische Serien-Namen (bei modded Items
sind die Metriken nicht im Voraus bekannt).

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

Roll-up-Job (stündlich):

| Auflösung | Retention | Zweck |
|---|---|---|
| 5 s | 6 h | Live-Graphen |
| 1 min | 7 Tage | Tagesverlauf |
| 15 min | 90 Tage | Langzeit |
| 1 h | unbegrenzt | Historie |

Jede Stufe eine eigene Tabelle, Aggregat = avg/min/max. Alternative bei wachsender Datenmenge:
DuckDB (spaltenorientiert, gute Kompression, brauchbare .NET-Bindings) — deshalb das Repository
hinter einem Interface halten, dann ist ein späterer Wechsel billig.

---

## 6. Auto-Research

**Metrik "schnellste Forschung":**

```
zeit = Σ(pack_count_i × units) / effektive_lab_rate
```

Voraussetzung: alle benötigten Pack-Typen sind verfügbar. "Verfügbar" heißt: Pack existiert im
Logistik-/Lab-Netzwerk **und** wird aktiv produziert (Produktionsrate > 0). Ohne die zweite
Bedingung wählt der Algorithmus eine Tech, die sofort blockiert.

Effektive Lab-Rate = `Anzahl aktiver Labs × lab_speed × (1 + research_speed_bonus)`.

**Algorithmus:**

1. Kandidaten = alle Techs mit erfüllten Prerequisites, nicht `researched`, nicht auf Blacklist
2. Filter: alle `research_unit_ingredients` sind produzierte Packs
3. Score = geschätzte Zeit in Sekunden
4. Min-Score gewinnt; bei Gleichstand niedrigerer Tech-Level

Die Auswahl läuft **im Collector**, nicht in Lua — dort liegen die Produktionsdaten und die
Blacklist-Config ohnehin schon.

**Sicherheitsregeln:**

- Nur aktiv, wenn die Research-Queue leer ist
- Blacklist konfigurierbar (Präfix-Matching, z.B. alle `*-productivity-*` Infinite-Techs)
- Optional: Max-Level-Grenze für Infinite-Techs
- Toggle im Frontend, Zustand persistiert im Collector — ein Save-Reload aktiviert den
  Automatismus nicht ungewollt
- Serverseitige Validierung im Snippet selbst (§4.5)
- Audit-Log jeder Änderung: bei einem Automatismus, der ins Save eingreift, sollte
  nachvollziehbar sein, was wann warum gesetzt wurde

---

## 7. Frontend

**Stack:** React + TypeScript + Vite, Tailwind, uPlot für Graphen (bei vielen Datenpunkten
deutlich performanter als Recharts). Build-Output als `wwwroot` in die ASP.NET-App —
ein Prozess, ein Port, kein CORS.

**Transport:** SignalR-Hub pusht Snapshots, kein Polling im Browser. Historische Daten über REST
(`GET /api/series?metric=...&from=...&to=...&resolution=...`).

### Layout — Zweitbildschirm, ohne Interaktion lesbar

```
┌─ Overview ──────────────────────────────────────────────────┐
│ [Nauvis] [Vulcanus] [Fulgora] [Gleba] [Aquilo] [Platforms]  │  ← Planeten-Tabs
├──────────────┬──────────────┬──────────────┬────────────────┤
│ POWER        │ RESEARCH     │ ALERTS       │ ROBOTS         │
│ 1.24 / 1.30GW│ Logistics 3  │ ⚠ 3 Züge     │ 810/1200 aktiv │
│ ████████░ 96%│ ~4m 12s      │ ⚠ Steel 0.83 │ ⚠ 22 warten    │
│ Akku ▓▓▓ 72% │ [auto: ON]   │ ⚠ LDS stalled│    auf Ladung  │
├──────────────┴──────────────┴──────────────┴────────────────┤
│ MASCHINEN (nach Rezept, sortiert nach Problemen)            │
│ electronic-circuit   240  ▓▓▓▓▓▓▓▓▓░ 231 ok  7 no_ingr      │
│ steel-plate          180  ▓▓▓▓▓▓░░░░ 120 ok 60 output_full  │
├──────────────────────────────┬──────────────────────────────┤
│ ERZE            [Scan 14/20] │ PRODUKTIONS-RATIO            │
│ iron-ore  1840/1980  ~16h    │ steel-plate    0.83 ↓        │
│ copper    1200/1200  ~22h    │ green-circuit  1.02 →        │
└──────────────────────────────┴──────────────────────────────┘
```

Der Scan-Fortschritt (`14/20`) beim Ressourcen-Panel macht sichtbar, dass die Erz-Zahlen aus
einem laufenden Zyklus stammen und nicht sekundenaktuell sind. Zusätzlich ein Zeitstempel
"zuletzt vollständig: vor 47s".

**Tabs:** Overview · Maschinen · Züge · Strom · Ressourcen · Logistik · Plattformen ·
Produktion · Circuits · Verlauf · Einstellungen

**Ampel-Farbcodierung** durchgängig: grün ok, gelb Warnung, rot kritisch. Der Sinn eines
Zweitbildschirm-Dashboards ist, dass ein Blick reicht — Probleme deshalb immer nach oben
sortieren und farblich dominant machen, statt sie in Tabellen verschwinden zu lassen.

---

## 8. Setup & Betrieb

### 8.1 Server-Seite

Kein Mod, keine Installation — nur RCON aktivieren:

```bash
./bin/x64/factorio \
  --start-server saves/meinsave.zip \
  --server-settings data/server-settings.json \
  --rcon-bind 127.0.0.1:27015 \
  --rcon-password "geheim"
```

`--rcon-bind 127.0.0.1` wenn Collector und Server auf derselben Maschine laufen. Sonst
`--rcon-port` + WireGuard/SSH-Tunnel.

### 8.2 Collector-Seite

`appsettings.json`:

```json
{
  "Factorio": {
    "Host": "127.0.0.1",
    "RconPort": 27015,
    "RconPassword": "geheim",
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
./Fdash.Api            # oder: dotnet Fdash.Api.dll
```

Dashboard unter `http://localhost:5000`.

### 8.3 Startsequenz

1. RCON-Verbindung aufbauen
2. Discovery-Call → Version, Surfaces, Save-Fingerprint
3. Prototyp-Export anstoßen und einlesen
4. Scheduler starten

### 8.4 Verbindung testen

Vor der ersten Zeile Code:

```bash
mcrcon -H 127.0.0.1 -P 27015 -p geheim "/silent-command rcon.print(game.tick)"
```

Kommt eine Zahl zurück, steht der Datenpfad und Phase 0 ist im Wesentlichen validiert.

---

## 9. Umsetzungsphasen

| Phase | Inhalt | Ergebnis |
|---|---|---|
| **0** | RCON-Client, Snippet-Infrastruktur, Discovery, Save-Fingerprint, Prototyp-Export | Verbindung steht, Daten fließen |
| **1** | Power, Assembler, Züge + SQLite-Storage + minimales Dashboard | Erste nutzbare Version |
| **2** | Ressourcen (chunked scan), Drills, Logistik, Produktions-Ratio, Stall-Detection | Kern-Feature-Set |
| **3** | Space Age: Plattformen, Orbital Requests, Multi-Surface-Tabs | Space-Age-vollständig |
| **4** | Auto-Research (erst read-only Vorschau, dann Schreibzugriff) | Automatik |
| **5** | Zeitreihen-Graphen, Roll-up, Verlaufs-Tab | Historie |
| **6** | Circuits, Icons, Polish, konfigurierbare Alert-Schwellwerte | Fertig |

**Phase 2 ist die riskanteste** — dort zeigt sich, ob der Chunked-Ressourcen-Scan ohne
spürbaren Server-Lag durchläuft. Deshalb früh gegen das echte Save messen und nicht gegen eine
kleine Testkarte. Falls es dort klemmt, ist der Zeitpunkt gekommen, die `ModQuery`-Variante
(§5.2) für genau diesen einen Job nachzurüsten.

**Phase 4 bewusst nach dem Kern:** Auto-Research ist das einzige Feature, das schreibend
eingreift. Erst bauen, wenn der Datenpfad vertrauenswürdig ist — und selbst dann zuerst als
Vorschau ("würde jetzt X setzen"), damit sich die Auswahl-Logik gegen das eigene Urteil prüfen
lässt, bevor sie autonom läuft.

---

## 10. Offene Punkte

1. **Achievements** — Vorab mit den Mitspielern klären: `/silent-command` deaktiviert sie
   dauerhaft für das Save. Höchste Priorität, weil es das ganze Projekt betrifft.
2. **Tick-Kosten messen** — wie teuer ist ein Assembler-Full-Scan auf eurem tatsächlichen Save?
   Bestimmt, ob 5s-Intervalle realistisch sind.
3. **Space-Age-API für Orbital Requests** — gegen die aktuelle Doku verifizieren; die
   Platform-Logistics-API hat sich zwischen 2.0-Releases geändert.
4. **Alert-Schwellwerte** — hart kodiert oder pro Save konfigurierbar? Empfehlung: JSON-Config
   mit sinnvollen Defaults.
5. **Authentifizierung** — nur LAN oder über Reverse-Proxy erreichbar? Bei letzterem mindestens
   Basic Auth vor dem SignalR-Hub, weil der Auto-Research-Endpunkt schreibend ist.
6. **Icons** — modded Item-Icons aus den Mod-Zips extrahieren wäre machbar, ist aber Aufwand.
   Phase 6 oder weglassen?

---

## 11. Entscheidungen (bereits festgelegt)

| Thema | Entscheidung |
|---|---|
| Transport | RCON |
| Datenerfassung | **`/silent-command`, kein Mod** (Mitspieler müssen nichts installieren) |
| Betriebsmodus | Nur Host/Dedicated Server |
| Space Age | Ja, inkl. generischer Mod-Item-Unterstützung |
| Backend | C#, Linux + Windows |
| Poll-Intervall | 5s Standard, längere Intervalle für teure Jobs |
| Ressourcen-Scan | Chunked über mehrere Polls, Akkumulation im Collector |
| Maschinen-Diagnose | `entity.status` reicht, aggregiert pro Rezept |
| Gruppierung | Pro Planet/Surface |
| Zug-Probleme | Nur State-basiert, kein "wartet ungewöhnlich lange" |
| Ressourcen | Pro Planet; bei unendlichen nur akt./max. Förderrate |
| `rate_max` | Alle Miner arbeiten (theoretisches Maximum) |
| Auto-Research | Schnellste mit aktuell verfügbaren Packs, nur bei leerer Queue, setzt aktiv |
| Historie | Zeitreihen-DB mit Roll-up |
| Multi-Save | Ja, über Fingerprint (Map-Seed-Hash) |
| Layout | Dashboard-Übersicht + Detail-Tabs |
