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
  st.extra = 0

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
      r = { total = 0, working = 0, rate_max = 0, covered = 0 }
      by_res[name] = r
    end
    r.total = r.total + 1
    if d.status == defines.entity_status.working then r.working = r.working + 1 end

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
    if v == nil or t == nil or (tick - t) >= refresh then
      -- Ein Area-Scan kostet ein Vielfaches des restlichen Schleifenkoerpers.
      st.extra = st.extra + 20
      v = measure_covered(d, name)
      t = tick
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
  end)

  local cost = spent + st.extra
  if not done then return cost, nil end

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
  return cost, {
    surface = surface and surface.name or tostring(si),
    drills = out
  }
end

return job
