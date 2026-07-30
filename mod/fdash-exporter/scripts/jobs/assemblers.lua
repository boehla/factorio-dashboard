-- Maschinen aggregiert pro produziertem Item.
--
-- Gruppiert wird nach Haupt-Produkt statt nach Rezept, damit alle Maschinen,
-- die dasselbe Item ueber ggf. mehrere Rezepte herstellen, zusammenzaehlen —
-- und damit der Gruppenname ein Item-Name ist, zu dem ein Icon existiert.

local sweep = require("scripts.sweep")
local util = require("scripts.util")

local TYPES = { "assembling-machine", "furnace", "rocket-silo" }

local job = { name = "assemblers", per_surface = true, interval = 300 }

--- Zutatenpuffer einer hungernden Maschine: name -> vorhandene Menge.
--- Items aus dem Input-Inventar, Fluide aus den Fluidboxen (dort stehen auch
--- Ausgangsfluide drin, die filtert der Abgleich mit den Rezept-Zutaten weg).
local function have_of(e)
  local have = {}
  -- Index 2 ist bei Assembler, Ofen und Silo derselbe Input-Slotsatz.
  local inv = e.get_inventory(defines.inventory.assembling_machine_input)
  if inv then
    for k, v in pairs(inv.get_contents()) do
      -- 2.0 liefert eine Liste {name=,count=,quality=}, aeltere ein name->count-Dict
      if type(v) == "table" then have[v.name] = (have[v.name] or 0) + v.count
      else have[k] = (have[k] or 0) + v end
    end
  end
  local fb = e.fluidbox
  if fb then
    for i = 1, #fb do
      local f = fb[i]
      if f and f.name then have[f.name] = (have[f.name] or 0) + f.amount end
    end
  end
  return have
end

function job.run(st, si, budget)
  if not st.by_item then
    st.ti, st.slot = 1, 1
    st.by_item = {}
    st.no_recipe = 0
  end
  st.extra = 0

  local starving = util.starving_statuses()
  local by_item = st.by_item

  local spent, done = sweep.registry(st, si, TYPES, budget, function(e)
    local r = e.get_recipe()
    if not r then
      st.no_recipe = st.no_recipe + 1
      return
    end
    local key = util.recipe_item(r.name)
    local rec = by_item[key]
    if not rec then
      rec = { total = 0, status = {}, speed_sum = 0, recipes = {}, starving = 0, missing = {} }
      by_item[key] = rec
    end
    rec.total = rec.total + 1
    local status = e.status or -1
    local sn = util.status_name(status)
    rec.status[sn] = (rec.status[sn] or 0) + 1
    rec.speed_sum = rec.speed_sum + (e.crafting_speed or 0)
    rec.recipes[r.name] = (rec.recipes[r.name] or 0) + 1

    -- Nur fuer wirklich hungernde Maschinen lohnt der teure Inventar-/
    -- Fluidbox-Read: eine laufende Maschine hat ebenfalls einen fast leeren
    -- Eingangspuffer, ihr fehlt aber nichts.
    if starving[status] then
      rec.starving = rec.starving + 1
      -- Der Read kostet ein Vielfaches des restlichen Schleifenkoerpers und
      -- wird deshalb hoeher verrechnet, damit das Tick-Budget realistisch ist.
      st.extra = st.extra + 4
      local have = have_of(e)
      -- Fehlmenge je Zutat aufsummieren. Fehlt eine Zutat komplett, ist das die
      -- volle Rezeptmenge; so wiegt ein ganz leerer Puffer schwerer als ein knapper.
      for _, ing in pairs(util.recipe_ingredients(r.name)) do
        local short = ing.amount - (have[ing.name] or 0)
        if short > 0 then rec.missing[ing.name] = (rec.missing[ing.name] or 0) + short end
      end
    end
  end)

  local cost = spent + st.extra
  if not done then return cost, nil end

  local out = {}
  for k, v in pairs(by_item) do
    out[k] = {
      total = v.total,
      status = v.status,
      avg_speed = v.total > 0 and (v.speed_sum / v.total) or 0,
      recipes = v.recipes,
      starving = v.starving,
      missing = util.nil_if_empty(v.missing)
    }
  end

  local surface = game.surfaces[si]
  st.by_item = nil
  return cost, {
    surface = surface and surface.name or tostring(si),
    by_item = out,
    no_recipe = st.no_recipe
  }
end

return job
