-- Space-Age-Plattformen: Treibstoff und Warnungen.
--
-- Global, klein und deshalb ohne Phasen — aber trotzdem ueber die Liste
-- budgetiert, weil ein Spaetspiel-Save dutzende Plattformen haben kann.
-- Ohne Space Age liefert der Job einfach eine leere Liste.

local sweep = require("scripts.sweep")

local job = { name = "platforms", per_surface = false, interval = 600 }

local FUELS = { "thruster-fuel", "thruster-oxidizer" }
local TANK_CAPACITY = 24000

function job.enabled()
  return script.active_mods["space-age"] ~= nil
end

function job.run(st, _si, budget)
  local force = game.forces.player
  if not (force and force.platforms) then
    return 0, { platforms = {} }
  end

  if not st.list then
    local list = {}
    for _, plat in pairs(force.platforms) do list[#list + 1] = plat end
    st.list = list
    st.i = 0
    st.out = {}
  end

  local out = st.out

  local spent, done = sweep.array(st, st.list, budget, function(plat)
    if not (plat and plat.valid) then return end
    local hub = plat.hub
    local fuel = {}
    local warnings = {}
    if hub and hub.valid then
      for _, fname in pairs(FUELS) do
        local cur = hub.get_fluid_count(fname)
        local pct = cur / TANK_CAPACITY
        fuel[fname] = { current = cur, max = TANK_CAPACITY, pct = pct }
        if pct < 0.25 then warnings[#warnings + 1] = fname .. "_low" end
      end
    end
    out[#out + 1] = {
      name = plat.name,
      state = tostring(plat.state),
      location = plat.space_location and plat.space_location.name or "in_transit",
      speed = plat.speed or 0,
      thrusters = 0,
      fuel = fuel,
      hub_contents = {},
      warnings = warnings
    }
  end)

  if not done then return spent, nil end

  st.list, st.out = nil, nil
  return spent, { platforms = out }
end

return job
