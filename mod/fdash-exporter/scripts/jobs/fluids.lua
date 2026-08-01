-- Fluidtanks: Fuellstand und Temperatur je Fluid.
--
-- Auf Pyanodons ist die Gasbilanz die halbe Diagnose. Die Produktionsstatistik
-- sagt zwar, wie viel entsteht und verschwindet, aber nicht, ob die Puffer voll
-- oder leer sind — und genau daran haengt die Frage, in welche Richtung man
-- suchen muss: voller Tank heisst Abnehmer fehlt, leerer heisst Erzeuger fehlt.
--
-- Ein Tank kostet einen Fluidbox-Zugriff. Das ist billig genug fuer den
-- Dauerbetrieb, laeuft aber trotzdem budgetiert.

local sweep = require("scripts.sweep")
local config = require("scripts.config")

local job = { name = "fluids", per_surface = true, interval = 1800 }

local TYPES = { "storage-tank" }

function job.enabled()
  return config.fluid_scan()
end

function job.run(st, si, budget)
  if not st.acc then
    st.ti, st.slot = 1, 1
    st.acc = {}
    st.tanks = 0
  end

  local acc = st.acc

  local spent, done = sweep.registry(st, si, TYPES, budget, function(e)
    st.tanks = st.tanks + 1
    local fb = e.fluidbox
    if not fb then return end
    for i = 1, #fb do
      local f = fb[i]
      -- Ein leerer Tank hat eine Fluidbox ohne Inhalt. Der zaehlt trotzdem
      -- mit: "20 Tanks, davon 19 leer" ist die Aussage, nicht "1 Tank".
      local cap = 0
      local okc, c = pcall(function() return fb.get_capacity(i) end)
      if okc and type(c) == "number" then cap = c end

      if f and f.name then
        local a = acc[f.name]
        if not a then
          a = { amount = 0, capacity = 0, tanks = 0, temp_sum = 0 }
          acc[f.name] = a
        end
        a.amount = a.amount + f.amount
        a.capacity = a.capacity + cap
        a.tanks = a.tanks + 1
        a.temp_sum = a.temp_sum + (f.temperature or 0) * f.amount
      else
        -- Leerer Tank: die Kapazitaet gehoert trotzdem irgendwohin, sonst
        -- sieht eine leergelaufene Anlage aus wie eine, die es nie gab.
        local a = acc["__empty__"]
        if not a then
          a = { amount = 0, capacity = 0, tanks = 0, temp_sum = 0 }
          acc["__empty__"] = a
        end
        a.capacity = a.capacity + cap
        a.tanks = a.tanks + 1
      end
    end
  end)

  if not done then return spent, nil end

  local empty = acc["__empty__"]
  acc["__empty__"] = nil

  local out = {}
  for name, a in pairs(acc) do
    out[name] = {
      amount = a.amount,
      capacity = a.capacity,
      tanks = a.tanks,
      fill = (a.capacity > 0) and (a.amount / a.capacity) or 0,
      -- Mengengewichtet: ein fast leerer Tank soll den Schnitt nicht kippen.
      temperature = (a.amount > 0) and (a.temp_sum / a.amount) or nil
    }
  end

  local surface = game.surfaces[si]
  local tanks = st.tanks
  st.acc, st.tanks = nil, nil
  return spent, {
    surface = surface and surface.name or tostring(si),
    fluids = out,
    tanks_total = tanks,
    tanks_empty = empty and empty.tanks or 0,
    empty_capacity = empty and empty.capacity or 0
  }
end

return job
