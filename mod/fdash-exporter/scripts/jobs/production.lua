-- Produktions-Statistik pro Item UND Fluid.
--
-- Kein Entity-Zugriff, aber trotzdem budgetiert: auf grossen Modpacks stehen
-- mehrere tausend Namen in der Statistik, und jeder kostet zwei
-- get_flow_count-Aufrufe. Frueher liefen die alle in einem einzigen Tick.
--
-- Die Menge "kommt in irgendeinem Rezept als Zutat vor" wird in util einmal
-- pro Session gebildet — vorher lief diese Schleife ueber ALLE Rezepte bei
-- jedem Poll (auf Pyanodons >10.000 Rezepte).

local sweep = require("scripts.sweep")
local util = require("scripts.util")

local job = { name = "production", per_surface = true, interval = 600 }

local function stats_for(surface)
  local force = game.forces.player
  if not (force and surface and surface.valid) then return nil, nil end
  local item_stats = force.get_item_production_statistics(surface)
  -- Fluide kommen aus einer eigenen Statistik; sie fehlt in aelteren Versionen,
  -- deshalb defensiv. Laeuft nur einmal pro Tick, der Closure-pcall ist hier ok.
  local ok, fluid_stats = pcall(function() return force.get_fluid_production_statistics(surface) end)
  return item_stats, (ok and fluid_stats or nil)
end

function job.run(st, si, budget)
  local surface = game.surfaces[si]
  if not (surface and surface.valid) then
    st.names = nil
    return 0, nil
  end

  if not st.names then
    local item_stats, fluid_stats = stats_for(surface)
    local names, kinds, seen = {}, {}, {}
    local function collect(stats, kind)
      if not stats then return end
      for n, _ in pairs(stats.input_counts) do
        if not seen[n] then seen[n] = true; names[#names + 1] = n; kinds[#names] = kind end
      end
      for n, _ in pairs(stats.output_counts) do
        if not seen[n] then seen[n] = true; names[#names + 1] = n; kinds[#names] = kind end
      end
    end
    collect(item_stats, "item")
    collect(fluid_stats, "fluid")
    st.names, st.kinds = names, kinds
    st.i = 0
    st.items = {}
  end

  local item_stats, fluid_stats = stats_for(surface)
  local is_ingredient = util.is_ingredient_set()
  local items = st.items
  local kinds = st.kinds

  local spent, done = sweep.array(st, st.names, budget, function(n, idx)
    local kind = kinds[idx]
    local stats = (kind == "fluid") and fluid_stats or item_stats
    if not stats then return end
    -- produziert = input in die Statistik, verbraucht = output
    local produced = util.statflow(stats, n, "input")
    local consumed = util.statflow(stats, n, "output")

    -- Namen stehen in der Statistik, sobald sie EINMAL geflossen sind. Auf
    -- einer gewachsenen Karte sind darum knapp zwei Drittel der Eintraege
    -- laengst stillgelegt und melden in beide Richtungen null. Sie kosten
    -- Serialisierung (der groesste Einzelposten im Tick) und sagen nichts:
    -- ohne Durchsatz gibt es weder Ratio noch Engpass. Das Dashboard hat sie
    -- ohnehin auf beiden Seiten weggefiltert.
    if produced == 0 and consumed == 0 then return end

    items[#items + 1] = {
      item = n,
      type = kind,
      produced_per_min = produced,
      consumed_per_min = consumed,
      is_ingredient = is_ingredient[n] or false,
      ratio = consumed > 0 and (produced / consumed) or (produced > 0 and 999 or 0)
    }
  end)

  -- Zwei get_flow_count je Name -> doppelt verrechnen.
  local cost = spent * 2
  if not done then return cost, nil end

  st.names, st.kinds, st.items = nil, nil, nil
  return cost, { surface = surface.name, items = items }
end

return job
