-- Bohrer: Zaehler, Foerderrate und das Erz im Foerderradius.
--
-- Frueher liefen hier ZWEI Voll-Scans ueber alle Bohrer (drills.lua und
-- resources.lua machten jeweils ihren eigenen). Jetzt gibt es genau einen
-- Sweep; sein Ergebnis landet zusaetzlich in storage.shared_drills und wird
-- vom Ressourcen-Job mitbenutzt.
--
-- `covered` (Erz im Foerderradius eines Bohrers) war der mit Abstand teuerste
-- Einzelposten: ein find_entities_filtered PRO BOHRER, alle 60 Sekunden. Der
-- Wert aendert sich aber nur langsam, deshalb wird er pro Bohrer gecacht und
-- nur alle `fdash-covered-refresh-minutes` neu gemessen.

local sweep = require("scripts.sweep")
local util = require("scripts.util")
local config = require("scripts.config")

local TYPES = { "mining-drill" }

local job = { name = "drills", per_surface = true, interval = 1800 }

--- Was ein Flaechen-Scan im Budget kostet, ausgedrueckt in "Entities".
---
--- find_entities_filtered ueber den Foerderradius liefert je nach Bohrergroesse
--- dutzende bis hunderte Erz-Entities, ist also um Groessenordnungen teurer als
--- der uebrige Schleifenkoerper. Ohne diese Anrechnung konnte ein einzelner
--- Tick so viele Flaechen-Scans machen, wie das Budget Entities erlaubt (per
--- Default 400) — im F5-Overlay als 10-ms-Ausschlag messbar, waehrend der
--- Durchschnitt bei 0,7 ms lag.
local MEASURE_COST = 50

--- Erzmenge im Foerderradius eines Bohrers.
local function measure_covered(d, target_name)
  local proto = d.prototype
  local rad = proto.mining_drill_radius or 0
  if rad <= 0 then return 0 end
  local pos = d.position
  local area = { { pos.x - rad, pos.y - rad }, { pos.x + rad, pos.y + rad } }
  local sum = 0
  local ores = d.surface.find_entities_filtered{ area = area, name = target_name }
  for i = 1, #ores do
    sum = sum + ores[i].amount
  end
  return sum
end

function job.run(st, si, budget)
  if not st.by_res then
    st.ti, st.slot = 1, 1
    st.by_res = {}
    st.cov_v, st.cov_t = {}, {}
  end

  local by_res = st.by_res
  local tick = game.tick
  local refresh = config.covered_refresh_ticks()
  local old_v = (storage.covered_v[si] or {})
  local old_t = (storage.covered_t[si] or {})
  local cov_v, cov_t = st.cov_v, st.cov_t

  local spent, done = sweep.registry(st, si, TYPES, budget, function(d)
    local tgt = d.mining_target
    if not (tgt and tgt.valid) then return end
    local name = tgt.name

    local r = by_res[name]
    if not r then
      r = { total = 0, working = 0, rate_max = 0, covered = 0, chunks = {} }
      by_res[name] = r
    end
    r.total = r.total + 1
    if d.status == defines.entity_status.working then r.working = r.working + 1 end

    -- In welchem Chunk der Bohrer steht. Der Ressourcen-Job ordnet damit
    -- Bohrer den einzelnen Erzfeldern zu, ohne dafuer einen eigenen Scan zu
    -- brauchen — hier kostet es eine Division und einen Tabellenzugriff.
    local pos = d.position
    local ck = util.chunk_key_at(pos.x, pos.y)
    local c = r.chunks[ck]
    if c then
      c.total = c.total + 1
      if d.status == defines.entity_status.working then c.working = c.working + 1 end
    else
      r.chunks[ck] = { total = 1, working = (d.status == defines.entity_status.working) and 1 or 0 }
    end

    -- theoretisches Maximum: alle Bohrer arbeiten
    local proto = d.prototype
    local speed = (proto.mining_speed or 1) * (1 + (d.speed_bonus or 0))
    local info = util.resource_info(name)
    local mining_time = (info and info.mining_time) or 1
    r.rate_max = r.rate_max + (speed * (1 + (d.productivity_bonus or 0))) / mining_time * 60

    -- covered: gecacht, nur alle N Minuten neu messen
    local u = d.unit_number
    local v = old_v[u]
    local t = old_t[u]
    local charged = nil
    if v == nil or t == nil or (tick - t) >= refresh then
      v = measure_covered(d, name)
      -- Zeitstempel pro Bohrer versetzen. Wuerde hier schlicht `tick` stehen,
      -- fuellten sich alle Caches im selben Pass — und liefen 10 Minuten
      -- spaeter auch alle im selben Pass wieder ab. Genau diese Herde ist der
      -- periodische Ausschlag. u ist deterministisch, der Versatz also auf
      -- allen Peers gleich und damit desync-sicher.
      t = tick - (u % refresh)
      charged = MEASURE_COST
    end
    cov_v[u] = v
    cov_t[u] = t

    -- Ueberlappende Foerderradien zaehlen dasselbe Erz mehrfach. Der frueher
    -- verwendete Dedup ueber Erz-Positionen setzte voraus, dass ALLE Bohrer im
    -- selben Durchlauf ihre Flaeche neu aufzaehlen — genau das vermeidet der
    -- Cache hier. Stattdessen klammert resources.lua die Summe gegen die
    -- tatsaechlich vorhandene Gesamtmenge der Ressource; damit ist der Wert
    -- nach oben durch die Physik begrenzt statt durch eine teure Menge.
    r.covered = r.covered + v
    return charged
  end)

  if not done then return spent, nil end

  -- Pass fertig: Cache und geteiltes Ergebnis umschalten (Double-Buffer).
  storage.covered_v[si] = cov_v
  storage.covered_t[si] = cov_t
  storage.shared_drills[si] = by_res

  local out = {}
  for name, r in pairs(by_res) do
    out[name] = { total = r.total, working = r.working, rate_max = r.rate_max }
  end

  local surface = game.surfaces[si]
  st.by_res, st.cov_v, st.cov_t = nil, nil, nil
  return spent, {
    surface = surface and surface.name or tostring(si),
    drills = out
  }
end

return job
