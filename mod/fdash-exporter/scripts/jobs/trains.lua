-- Zuege: nur Problemzuege uebertragen, der Rest als Zaehler.
--
-- Global (nicht pro Surface), weil der Train-Manager alle Oberflaechen kennt.
-- Die Zugliste wird zum Pass-Start eingefroren und danach budgetiert
-- abgearbeitet; LuaTrain-Referenzen sind speicherbar, ungueltige werden beim
-- Durchlauf uebersprungen.

local sweep = require("scripts.sweep")
local util = require("scripts.util")

local job = { name = "trains", per_surface = false, interval = 300 }

-- Robust gegen fehlende defines: Vergleich ueber die Status-NAMEN, nicht ueber
-- defines.train_state.X als Tabellen-Key (kann in manchen Versionen nil sein).
local PROBLEM_STATES = {
  no_path = true, path_lost = true, no_schedule = true,
  destination_full = true, manual_control = true, manual_control_stop = true
}

function job.run(st, _si, budget)
  if not st.trains then
    st.trains = game.train_manager.get_trains{}
    st.i = 0
    st.problems = {}
    st.total = 0
    st.ok = 0
  end
  st.extra = 0

  local problems = st.problems

  local spent, done = sweep.array(st, st.trains, budget, function(tr)
    if not (tr and tr.valid) then return end
    st.total = st.total + 1

    local sname = util.train_state_name(tr.state)
    if not PROBLEM_STATES[sname] then
      st.ok = st.ok + 1
      return
    end

    -- Nur fuer Problemzuege lohnt der teure Inhalts-Read.
    st.extra = st.extra + 4

    local station = nil
    local sched = tr.schedule
    if sched and sched.records and sched.current and sched.records[sched.current] then
      station = sched.records[sched.current].station
    end

    -- Ladung: was steckt fest? Nur die groessten Posten, damit die Payload
    -- klein bleibt.
    local cargo = {}
    local okc, contents = pcall(function() return tr.get_contents() end)
    if okc and contents then
      for _, c in pairs(contents) do
        cargo[#cargo + 1] = { name = c.name, count = c.count }
      end
    end
    local okf, fluids = pcall(function() return tr.get_fluid_contents() end)
    if okf and fluids then
      for fname, amount in pairs(fluids) do
        cargo[#cargo + 1] = { name = fname, count = math.floor(amount) }
      end
    end
    table.sort(cargo, function(a, b) return a.count > b.count end)
    local top = {}
    for i = 1, math.min(3, #cargo) do top[i] = cargo[i] end

    local loco = tr.front_stock
    problems[#problems + 1] = {
      id = tr.id,
      surface = (loco and loco.valid) and loco.surface.name or nil,
      state = sname,
      schedule_station = station,
      -- Eine leere Lua-Tabelle serialisiert als {} statt []. Der Consumer
      -- erwartet hier eine Liste, deshalb den Schluessel lieber ganz weglassen.
      cargo = #top > 0 and top or nil
    }
  end)

  local cost = spent + st.extra
  if not done then return cost, nil end

  local total, ok = st.total, st.ok
  st.trains, st.problems = nil, nil
  return cost, {
    problems = problems,
    totals = { total = total, ok = ok, problem = total - ok }
  }
end

return job
