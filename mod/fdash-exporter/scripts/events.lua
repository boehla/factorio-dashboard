-- Event-Anbindung.
--
-- Bewusst nur Bau-Events: sie feuern selten (ein Mensch oder Roboter baut ein
-- paar Entities pro Sekunde) und kosten pro Aufruf einen Tabelleneintrag.
--
-- Destroy-Events sind absichtlich NICHT abonniert. on_entity_died feuert bei
-- einem Beisser-Angriff hunderte Male pro Tick; ungueltige Registry-Eintraege
-- kosten uns dagegen gar nichts, weil sie beim naechsten Sweep ohnehin
-- auffallen und dort entfernt werden (siehe registry.lua).

local registry = require("scripts.registry")
local chunks = require("scripts.chunks")
local scan = require("scripts.scan")
local scheduler = require("scripts.scheduler")

local events = {}

local function build_filters()
  local f = {}
  for i = 1, #registry.TYPES do
    f[i] = { filter = "type", type = registry.TYPES[i], mode = "or" }
  end
  return f
end

local function on_built(event)
  registry.add(event.entity)
end

local function on_cloned(event)
  registry.add(event.destination)
end

function events.register()
  local filters = build_filters()

  local build_events = {
    defines.events.on_built_entity,
    defines.events.on_robot_built_entity,
    defines.events.script_raised_built,
    defines.events.script_raised_revive,
    defines.events.on_space_platform_built_entity,   -- nur mit Space Age
  }
  for _, id in pairs(build_events) do
    if id then script.on_event(id, on_built, filters) end
  end

  -- on_entity_cloned unterstuetzt keine Typ-Filter auf `destination`;
  -- registry.add verwirft alles, was uns nicht interessiert.
  script.on_event(defines.events.on_entity_cloned, on_cloned)

  -- ------------------------------------------------------------- Surfaces
  script.on_event(defines.events.on_surface_created, function(event)
    chunks.rebuild(event.surface_index)
    scan.enqueue(event.surface_index)
  end)

  script.on_event(defines.events.on_surface_deleted, function(event)
    registry.drop_surface(event.surface_index)
    chunks.drop_surface(event.surface_index)
    scheduler.forget_surface(event.surface_index)
  end)

  script.on_event(defines.events.on_surface_cleared, function(event)
    registry.drop_surface(event.surface_index)
    chunks.rebuild(event.surface_index)
    scheduler.forget_surface(event.surface_index)
    scan.enqueue(event.surface_index)
  end)

  -- --------------------------------------------------------- Zerstoerungen
  -- Die einzige Ausnahme von "keine Destroy-Events": ein Zaehler fuer
  -- zerstoerte Gebaeude der Spieler-Force. Der Filter ist entscheidend — ohne
  -- ihn feuert das bei jedem sterbenden Beisser, also hunderte Male pro Tick
  -- im Kampf. Mit Filter kommt der Handler nur dran, wenn wirklich etwas von
  -- der Basis kaputtgeht, und macht dann drei Tabellenzugriffe.
  --
  -- Noetig, weil player.get_alerts an einen verbundenen Spieler gebunden ist:
  -- auf einem Headless-Server ohne Spieler ist die Alert-Liste leer.
  script.on_event(defines.events.on_entity_died, function(event)
    local e = event.entity
    if not (e and e.valid) then return end
    local d = storage.deaths
    d.count = d.count + 1
    d.by_name[e.name] = (d.by_name[e.name] or 0) + 1
    -- Kleiner Ring der letzten Vorfaelle, damit man die Stelle findet.
    d.ri = (d.ri % 16) + 1
    d.recent[d.ri] = { name = e.name, tick = event.tick,
                       x = math.floor(e.position.x), y = math.floor(e.position.y) }
  end, { { filter = "force", force = "player" } })

  -- --------------------------------------------------------------- Chunks
  -- Feuert bei Erkundung sehr oft -> Handler minimal halten.
  script.on_event(defines.events.on_chunk_generated, function(event)
    chunks.append(event.surface.index, event.position.x, event.position.y)
  end)

  script.on_event(defines.events.on_chunk_deleted, function(event)
    chunks.mark_dirty(event.surface_index)
  end)
end

return events
