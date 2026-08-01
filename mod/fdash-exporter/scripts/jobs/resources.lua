-- Erzfelder: Restmengen, Ausbeute und Erschoepfungs-Prognose.
--
-- Das war der teuerste Job ueberhaupt: ein find_entities_filtered{type=
-- "resource"} ueber die komplette Oberflaeche, alle 60 Sekunden, synchron im
-- Tick. Auf einer erkundeten Karte sind das Millionen Entities.
--
-- Jetzt laeuft der Scan chunkweise und rollierend: pro Tick werden nur
-- `fdash-chunk-budget` Chunks angefasst, ein voller Durchlauf dauert je nach
-- Kartengroesse ein paar Sekunden bis Minuten. Fuer Restmengen und
-- Erschoepfungs-Prognosen ist das mehr als genau genug — die aendern sich in
-- Stunden, nicht in Sekunden.

local sweep = require("scripts.sweep")
local util = require("scripts.util")
local chunks = require("scripts.chunks")
local config = require("scripts.config")

-- budget_kind = "chunk": dieser Job rechnet in Map-Chunks und zieht aus
-- `fdash-chunk-budget`, nicht aus dem Entity-Budget der uebrigen Collector.
local job = { name = "resources", per_surface = true, interval = 3600, budget_kind = "chunk" }

--- Wie viele Erz-Entities einer Chunk-Einheit im Budget entsprechen.
local ORES_PER_CHUNK_UNIT = 250

function job.enabled()
  return config.resource_scan()
end

--- Hoechstens so viele Felder je Ressource melden. Auf einer erkundeten Karte
--- gibt es von manchen Erzen hunderte Vorkommen; interessant sind die grossen.
local PATCH_LIMIT = 8

--- Zusammenhaengende Chunk-Mengen finden (Flutfuellung ueber die
--- 4er-Nachbarschaft). Ein Erzfeld ist genau das: eine zusammenhaengende
--- Flaeche. Die Alternative — Clustern ueber Abstaende — braeuchte einen
--- Parameter, den niemand richtig raten kann.
---
--- Laeuft einmal am Ende eines vollen Durchlaufs (also alle paar Minuten) ueber
--- die belegten Chunks einer Ressource, nicht ueber die ganze Karte.
local function patches_of(chunk_amounts, drill_chunks)
  local seen = {}
  local out = {}

  for key, _ in pairs(chunk_amounts) do
    if not seen[key] then
      -- Flutfuellung mit einer Arbeitsliste statt Rekursion: ein grosses Feld
      -- hat leicht hunderte Chunks, und Lua-Stacktiefe ist nichts, worauf man
      -- sich hier verlassen sollte.
      local queue = { key }
      seen[key] = true
      local amount, count = 0, 0
      local sx, sy = 0, 0
      local drills_total, drills_working = 0, 0
      local qi = 1

      while qi <= #queue do
        local k = queue[qi]
        qi = qi + 1
        local a = chunk_amounts[k]
        amount = amount + a
        count = count + 1

        local cx, cy = util.chunk_xy(k)
        -- Chunk-Mittelpunkt, gewichtet mit der Erzmenge darin: das Zentrum soll
        -- dort liegen, wo das Erz ist, nicht in der Mitte der Bounding-Box.
        sx = sx + (cx * 32 + 16) * a
        sy = sy + (cy * 32 + 16) * a

        local d = drill_chunks and drill_chunks[k]
        if d then
          drills_total = drills_total + d.total
          drills_working = drills_working + d.working
        end

        local neighbours = {
          util.chunk_key(cx + 1, cy), util.chunk_key(cx - 1, cy),
          util.chunk_key(cx, cy + 1), util.chunk_key(cx, cy - 1)
        }
        for i = 1, 4 do
          local nk = neighbours[i]
          if chunk_amounts[nk] and not seen[nk] then
            seen[nk] = true
            queue[#queue + 1] = nk
          end
        end
      end

      out[#out + 1] = {
        amount = amount,
        chunks = count,
        x = (amount > 0) and math.floor(sx / amount) or 0,
        y = (amount > 0) and math.floor(sy / amount) or 0,
        drills = drills_total,
        drills_working = drills_working
      }
    end
  end

  table.sort(out, function(a, b) return a.amount > b.amount end)
  return out
end

function job.run(st, si, budget)
  if not st.acc then
    chunks.get(si, true)   -- Chunk-Liste ggf. neu aufbauen (nur an Pass-Grenzen)
    st.i = 0
    st.acc = {}
  end

  local acc = st.acc

  local spent, done = sweep.chunks(st, si, budget, function(surface, area)
    local ores = surface.find_entities_filtered{ area = area, type = "resource" }
    -- Ein Chunk voller Erz liefert ueber tausend Entities, und jede kostet
    -- einen API-Zugriff fuer `amount`. Gemessen waren daraus bis zu 41 ms in
    -- einem Tick geworden, waehrend der Schnitt bei 0,5 ms lag. Die Menge wird
    -- deshalb in Chunk-Einheiten umgerechnet und gegen das Budget verrechnet:
    -- ein dichter Chunk beendet den Tick, statt drei weitere nachzuziehen.
    local charged = #ores / ORES_PER_CHUNK_UNIT
    local ck = util.chunk_key_at(area[1][1], area[1][2])
    for k = 1, #ores do
      local e = ores[k]
      local n = e.name
      local a = acc[n]
      if not a then
        local info = util.resource_info(n)
        a = {
          total = 0,
          infinite = (info and info.infinite) or false,
          normal = (info and info.normal) or 0,
          yield_sum = 0, yield_n = 0,
          chunks = {}
        }
        acc[n] = a
      end
      a.total = a.total + e.amount
      -- Menge je Chunk mitfuehren: daraus entstehen am Pass-Ende die einzelnen
      -- Erzfelder. Ein Tabellenzugriff je Erz-Entity, in `charged` schon drin.
      a.chunks[ck] = (a.chunks[ck] or 0) + e.amount
      if a.infinite and a.normal > 0 then
        a.yield_sum = a.yield_sum + (e.amount / a.normal)
        a.yield_n = a.yield_n + 1
      end
    end
    return charged
  end)

  if not done then return spent, nil end

  -- ---- Pass fertig: mit Bohrer- und Produktionsdaten zusammenfuehren ----

  local surface = game.surfaces[si]
  if not (surface and surface.valid) then
    st.acc = nil
    return spent, nil
  end

  local drills = storage.shared_drills[si] or {}
  local force = game.forces.player
  local pstat = force and force.get_item_production_statistics(surface) or nil

  local out = {}
  for name, a in pairs(acc) do
    local dr = drills[name] or { total = 0, working = 0, rate_max = 0, covered = 0 }
    local info = util.resource_info(name)

    -- Das gefoerderte Item heisst bei modded Ressourcen oft anders als die
    -- Ressource (oder ist ein Fluid) -> nur bei einem echten Item die
    -- Produktionsstatistik abfragen.
    local rate_current = 0
    local product = info and info.product
    if pstat and product and prototypes.item[product] then
      rate_current = util.statflow(pstat, product, "input")
    end

    -- Ueberlappende Foerderradien koennen `covered` aufblaehen (siehe
    -- drills.lua) — gegen die real vorhandene Menge klammern.
    local covered = dr.covered or 0
    if covered > a.total then covered = a.total end

    -- Einzelne Felder statt nur der Summe: "noch 600 Mio. Eisen" hilft nicht,
    -- wenn davon 90 % in einem Feld liegen, unter dem kein Bohrer steht.
    local fields = patches_of(a.chunks, dr.chunks)
    local shown = {}
    for i = 1, math.min(PATCH_LIMIT, #fields) do
      local f = fields[i]
      shown[i] = {
        x = f.x, y = f.y,
        amount = f.amount,
        chunks = f.chunks,
        drills = f.drills,
        drills_working = f.drills_working,
        -- Nur bei endlichen Ressourcen sinnvoll: bei infinite Ores ist die
        -- Restmenge kein Vorrat, sondern eine Ergiebigkeit.
        depletion_pct = (not a.infinite and a.normal > 0 and f.chunks > 0)
          and math.min(1, 1 - f.amount / (f.chunks * 32 * 32 * a.normal)) or nil
      }
    end

    local entry = {
      infinite = a.infinite,
      total_amount = a.total,
      covered_amount = covered,
      drills = { total = dr.total, working = dr.working },
      rate_current = rate_current,
      rate_max = dr.rate_max,
      patches_total = #fields,
      patches = shown
    }
    if a.infinite then
      entry.yield_pct = a.yield_n > 0 and (a.yield_sum / a.yield_n) or 0
    elseif rate_current > 0 then
      -- ehrlichere Zahl: was unter Bohrern liegt, geteilt durch die Foerderrate
      local base = covered > 0 and covered or a.total
      entry.depletion_seconds = base / (rate_current / 60)
    end
    out[name] = entry
  end

  local total_chunks = (storage.chunks[si] and storage.chunks[si].n) or 0
  st.acc = nil
  return spent, {
    surface = surface.name,
    resources = out,
    scan_step = 1,
    scan_total = 1,
    scanned_chunks = total_chunks,
    last_complete = game.tick
  }
end

return job
