-- Kisteninhalte, aggregiert je Item.
--
-- Der teuerste Collector des Mods: ein Inventar-Read pro Kiste, und davon hat
-- eine gewachsene Basis leicht zehntausende. Deshalb per Default AUS
-- (fdash-container-scan) und mit einem langen Intervall.
--
-- Der Nutzen rechtfertigt den Preis trotzdem, sobald man ihn einschaltet:
-- zusammen mit dem Maschinenstatus sagt der Fuellstand, in welche Richtung man
-- suchen muss. Volle Puffer heissen "der Abnehmer fehlt", leere "der Erzeuger
-- fehlt". Ohne das bleibt bei "Maschine wartet auf Zutat" offen, wo genau.

local sweep = require("scripts.sweep")
local config = require("scripts.config")

local job = { name = "containers", per_surface = true, interval = 3600 }

local TYPES = { "container", "logistic-container" }

function job.enabled()
  return config.container_scan()
end

function job.run(st, si, budget)
  if not st.acc then
    st.ti, st.slot = 1, 1
    st.acc = {}
    st.boxes = 0
    st.slots_used = 0
    st.slots_total = 0
  end

  local acc = st.acc

  local spent, done = sweep.registry(st, si, TYPES, budget, function(e)
    local inv = e.get_inventory(defines.inventory.chest)
    if not inv then return end
    st.boxes = st.boxes + 1
    st.slots_total = st.slots_total + #inv

    local contents = inv.get_contents()
    local n = 0
    for k, v in pairs(contents) do
      -- 2.0 liefert eine Liste {name=,count=,quality=}, aeltere ein name->count-Dict.
      local name, count
      if type(v) == "table" then name, count = v.name, v.count else name, count = k, v end
      acc[name] = (acc[name] or 0) + count
      n = n + 1
    end
    st.slots_used = st.slots_used + n

    -- Ein Inventar-Read kostet ein Vielfaches eines Feldzugriffs; ohne diese
    -- Rueckmeldung waere das Budget in Kisten gezaehlt statt in Arbeit.
    return 3
  end)

  if not done then return spent, nil end

  local surface = game.surfaces[si]
  local out, boxes = acc, st.boxes
  local used, total = st.slots_used, st.slots_total
  st.acc, st.boxes, st.slots_used, st.slots_total = nil, nil, nil, nil
  return spent, {
    surface = surface and surface.name or tostring(si),
    items = out,
    containers = boxes,
    slots_used = used,
    slots_total = total
  }
end

return job
