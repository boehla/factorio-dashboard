-- Strom pro elektrischem Netz.
--
-- Vier Phasen, jede fuer sich budgetiert:
--   1 Generatoren  -> Nennleistung je Netz (die echte Reserve nach oben)
--   2 Akkus        -> gespeicherte Energie, Kapazitaet, Prototyp-Namen
--   3 Strommasten  -> ein Repraesentant je electric_network_id
--   4 Statistik    -> Ein-/Ausspeisung je Netz ueber den Repraesentanten
--
-- Phase 3+4 sind der Grund fuer die Aufteilung: Masten gibt es zehntausende,
-- Netze aber nur eine Handvoll. Nach Phase 3 wird nur noch pro NETZ gearbeitet.

local sweep = require("scripts.sweep")
local util = require("scripts.util")

local job = { name = "power", per_surface = true, interval = 300 }

local GEN = { "generator" }
local ACC = { "accumulator" }
local POLE = { "electric-pole" }

-- max_power_output ist eine Prototyp-Eigenschaft und damit fuer die Session
-- konstant -> einmal pro Prototyp-Name aufloesen statt pro Entity.
local max_output_cache = nil
local function max_output(proto_name, proto)
  if not max_output_cache then max_output_cache = {} end
  local v = max_output_cache[proto_name]
  if v ~= nil then return v end
  local ok, mx = pcall(function() return proto.max_power_output end)
  v = (ok and type(mx) == "number") and mx or 0
  max_output_cache[proto_name] = v
  return v
end

local function begin(st)
  st.phase = 1
  st.ti, st.slot = 1, 1
  st.cap = {}
  st.acc = {}
  st.rep = {}
  st.netids = nil
  st.i = 0
  st.nets = {}
end

function job.run(st, si, budget)
  if not st.phase then begin(st) end
  local spent, done = 0, false

  -- ---------------------------------------------------------- 1 Generatoren
  if st.phase == 1 then
    local cap = st.cap
    spent, done = sweep.registry(st, si, GEN, budget, function(g)
      local gid = g.electric_network_id
      if not gid then return end
      -- max_power_output ist J/Tick -> x60 = Watt. Solar und Akkus zaehlen hier
      -- bewusst NICHT mit; es geht um steuerbare Nennleistung.
      local mx = max_output(g.name, g.prototype)
      if mx > 0 then cap[gid] = (cap[gid] or 0) + mx * 60 end
    end)
    if done then st.phase, st.ti, st.slot = 2, 1, 1 end
    return spent, nil
  end

  -- --------------------------------------------------------------- 2 Akkus
  if st.phase == 2 then
    local acc = st.acc
    spent, done = sweep.registry(st, si, ACC, budget, function(a)
      local aid = a.electric_network_id
      if not aid then return end
      local t = acc[aid]
      if not t then t = { e = 0, c = 0, n = 0, names = {} }; acc[aid] = t end
      t.e = t.e + a.energy
      t.c = t.c + a.electric_buffer_size
      t.n = t.n + 1
      t.names[a.name] = true
    end)
    if done then st.phase, st.ti, st.slot = 3, 1, 1 end
    return spent, nil
  end

  -- ---------------------------------------------------------- 3 Strommasten
  if st.phase == 3 then
    local rep = st.rep
    spent, done = sweep.registry(st, si, POLE, budget, function(p)
      local id = p.electric_network_id
      if id and rep[id] == nil then rep[id] = p end
    end)
    if done then
      local ids = {}
      for id, _ in pairs(rep) do ids[#ids + 1] = id end
      -- Deterministische Reihenfolge: pairs() ist in Lua nicht stabil, und die
      -- Ausgabe soll auf allen Peers identisch sein.
      table.sort(ids)
      st.netids = ids
      st.i = 0
      st.phase = 4
    end
    return spent, nil
  end

  -- ------------------------------------------------------------ 4 Statistik
  st.extra = 0
  local nets = st.nets
  local cap, acc, rep = st.cap, st.acc, st.rep

  spent, done = sweep.array(st, st.netids, budget, function(id)
    local p = rep[id]
    if not (p and p.valid) then return end
    local st_ = p.electric_network_statistics
    if not st_ then return end

    -- Strom-Statistik liefert mit count=false J/Tick; x60 -> Watt (entspricht
    -- 1:1 der In-Game-Strom-GUI). count=true lieferte hier nur eine
    -- bedeutungslose Sample-Zahl.
    local prod, cons = 0, 0
    local by_prod, by_cons = {}, {}
    for name, _ in pairs(st_.output_counts) do
      local v = util.statflow(st_, name, "output", false) * 60
      prod = prod + v
      by_prod[name] = v
      st.extra = st.extra + 1
    end
    for name, _ in pairs(st_.input_counts) do
      local v = util.statflow(st_, name, "input", false) * 60
      cons = cons + v
      by_cons[name] = v
      st.extra = st.extra + 1
    end

    -- Akku laedt -> taucht als Verbraucher (input) auf, entlaedt -> als
    -- Erzeuger (output). charge/discharge sind 1-Min-Mittel in Watt; das
    -- Frontend bildet daraus Rest-/Ladezeit.
    local accinfo = nil
    local a = acc[id]
    if a then
      local charge, discharge = 0, 0
      for nm, _ in pairs(a.names) do
        charge = charge + util.statflow(st_, nm, "input", false) * 60
        discharge = discharge + util.statflow(st_, nm, "output", false) * 60
        st.extra = st.extra + 2
      end
      accinfo = {
        count = a.n, energy = a.e, capacity = a.c,
        charge_rate = charge, discharge_rate = discharge
      }
    end

    nets[#nets + 1] = {
      id = id,
      production = prod,
      consumption = cons,
      satisfaction = cons > 0 and math.min(1, prod / cons) or 1,
      capacity = cap[id] or 0,
      accumulators = accinfo,
      by_producer = by_prod,
      by_consumer_group = by_cons
    }
  end)

  local cost = spent + (st.extra or 0)
  if not done then return cost, nil end

  local surface = game.surfaces[si]
  local payload = {
    surface = surface and surface.name or tostring(si),
    networks = nets
  }
  -- Zwischenstaende freigeben: sie landen sonst bis zum naechsten Pass-Start
  -- im Savegame.
  st.phase, st.cap, st.acc, st.rep, st.netids, st.nets = nil, nil, nil, nil, nil, nil
  return cost, payload
end

return job
