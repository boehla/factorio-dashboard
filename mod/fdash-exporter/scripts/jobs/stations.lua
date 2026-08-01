-- Bahnhoefe, zusammengefasst nach Stationsnamen.
--
-- Der Zug-Job meldet Zuege, die haengen. Die andere Haelfte des Bildes fehlte:
-- Stationen, die nie angefahren werden (tote Route) und Stationen mit
-- Dauerwarteschlange (Ladeengpass). Beides sieht man nur an den Stops selbst.
--
-- Gruppiert wird nach Namen, weil ein "Eisen Beladen" in Factorio ueblicherweise
-- zwanzigmal existiert und der Fahrplan nur den Namen kennt.

local sweep = require("scripts.sweep")

local job = { name = "stations", per_surface = true, interval = 600 }

local TYPES = { "train-stop" }

function job.run(st, si, budget)
  if not st.acc then
    st.ti, st.slot = 1, 1
    st.acc = {}
  end

  local acc = st.acc

  local spent, done = sweep.registry(st, si, TYPES, budget, function(e)
    local name = e.backer_name or "?"
    local a = acc[name]
    if not a then
      a = { stops = 0, trains = 0, limit = 0, unlimited = 0, disabled = 0 }
      acc[name] = a
    end
    a.stops = a.stops + 1

    -- trains_count = Zuege, die diese Station gerade als Ziel haben (unterwegs
    -- oder da). trains_limit ist nil, wenn kein Limit gesetzt ist.
    local ok, count = pcall(function() return e.trains_count end)
    if ok and type(count) == "number" then a.trains = a.trains + count end

    local okl, limit = pcall(function() return e.trains_limit end)
    if okl and type(limit) == "number" then a.limit = a.limit + limit
    else a.unlimited = a.unlimited + 1 end

    if e.status == defines.entity_status.disabled_by_control_behavior then
      a.disabled = a.disabled + 1
    end
  end)

  if not done then return spent, nil end

  local out = {}
  for name, a in pairs(acc) do
    out[name] = {
      stops = a.stops,
      trains = a.trains,
      -- Nur aussagekraeftig, wenn ueberhaupt Limits gesetzt sind.
      limit = (a.unlimited == 0) and a.limit or nil,
      disabled = (a.disabled > 0) and a.disabled or nil
    }
  end

  local surface = game.surfaces[si]
  st.acc = nil
  return spent, {
    surface = surface and surface.name or tostring(si),
    stations = out
  }
end

return job
