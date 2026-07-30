-- Logistik- und Baurobooter pro Netzwerk.
--
-- Zwei Phasen wie bei power.lua: erst die Roboports durchgehen und je
-- Netzwerk-ID einen Repraesentanten merken, dann nur noch pro NETZWERK
-- aggregieren. Roboports gibt es tausende, Netzwerke ein paar Dutzend.

local sweep = require("scripts.sweep")

local job = { name = "logistics", per_surface = true, interval = 300 }

local PORTS = { "roboport" }

function job.run(st, si, budget)
  if not st.phase then
    st.phase = 1
    st.ti, st.slot = 1, 1
    st.rep = {}
    st.nets = {}
    st.i = 0
  end

  -- ------------------------------------------------------------ 1 Roboports
  if st.phase == 1 then
    local rep = st.rep
    local spent, done = sweep.registry(st, si, PORTS, budget, function(rp)
      local net = rp.logistic_network
      if not net then return end
      local id = net.network_id
      if id and rep[id] == nil then rep[id] = rp end
    end)
    if done then
      local ids = {}
      for id, _ in pairs(rep) do ids[#ids + 1] = id end
      table.sort(ids)
      st.ids = ids
      st.i = 0
      st.phase = 2
    end
    return spent, nil
  end

  -- ----------------------------------------------------------- 2 Netzwerke
  st.extra = 0
  local nets = st.nets
  local rep = st.rep

  local spent, done = sweep.array(st, st.ids, budget, function(id)
    local rp = rep[id]
    if not (rp and rp.valid) then return end
    local net = rp.logistic_network
    if not (net and net.valid) then return end

    local cells = net.cells
    -- Die Zellen-Schleife skaliert mit der Netzgroesse -> mitverrechnen.
    st.extra = st.extra + #cells

    local charging, waiting = 0, 0
    for k = 1, #cells do
      charging = charging + cells[k].charging_robot_count
      waiting = waiting + cells[k].to_charge_robot_count
    end

    local all_log = net.all_logistic_robots
    local avail_log = net.available_logistic_robots
    local all_con = net.all_construction_robots
    local avail_con = net.available_construction_robots

    nets[#nets + 1] = {
      id = id,
      roboports = #cells,
      logistic_robots = {
        total = all_log,
        idle = avail_log,
        -- working = all - available - charging - waiting
        working = math.max(0, all_log - avail_log - charging - waiting),
        charging = charging,
        waiting_for_charge = waiting
      },
      construction_robots = {
        total = all_con,
        idle = avail_con,
        working = math.max(0, all_con - avail_con),
        charging = 0,
        waiting_for_charge = 0
      }
    }
  end)

  local cost = spent + (st.extra or 0)
  if not done then return cost, nil end

  local surface = game.surfaces[si]
  local payload = {
    surface = surface and surface.name or tostring(si),
    networks = nets
  }
  st.phase, st.rep, st.ids, st.nets = nil, nil, nil, nil
  return cost, payload
end

return job
