-- Zentrale Budget-Verwaltung.
--
-- Genau EIN Tick-Handler fuer den ganzen Mod. Er verteilt ein hartes Budget
-- (`fdash-entity-budget`, Default 400 Entities) reihum auf alle faelligen
-- Collector und bricht ab, sobald es aufgebraucht ist. Damit ist die
-- Tick-Kosten des Mods nach oben begrenzt — unabhaengig davon, wie gross die
-- Fabrik wird. Waechst die Basis, dauern die Durchlaeufe laenger; die UPS
-- bleiben gleich.
--
-- Das ist der eigentliche Unterschied zum RCON-Ansatz: dort bestimmte die
-- Fabrikgroesse die Spike-Hoehe, hier bestimmt sie nur noch die Latenz.

local config = require("scripts.config")
local scan = require("scripts.scan")
local publish = require("scripts.publish")

local scheduler = {}

local JOBS = {
  require("scripts.jobs.meta"),
  require("scripts.jobs.power"),
  require("scripts.jobs.assemblers"),
  require("scripts.jobs.production"),
  require("scripts.jobs.trains"),
  require("scripts.jobs.logistics"),
  require("scripts.jobs.circuits"),
  require("scripts.jobs.research_state"),
  require("scripts.jobs.drills"),
  require("scripts.jobs.resources"),
  require("scripts.jobs.platforms"),
  require("scripts.jobs.orbital"),
}

scheduler.JOBS = JOBS

local META_JOB
for _, j in pairs(JOBS) do
  if j.name == "meta" then META_JOB = j end
end

--- Nach einem Fehler wird ein Job so lange ausgesetzt. Ein kaputter Collector
--- (z. B. weil ein Mod eine API anders belegt) soll den Rest nicht mitreissen.
local ERROR_BACKOFF = 3600

local function state_for(job, si)
  local per_job = storage.jobstate[job.name]
  if not per_job then
    per_job = {}
    storage.jobstate[job.name] = per_job
  end
  local st = per_job[si]
  if not st then
    st = { next_tick = 0, active = false }
    per_job[si] = st
  end
  return st
end

--- Alle (Job, Surface)-Paare in stabiler Reihenfolge, getrennt nach Budget-Art.
--- Entity-Jobs zaehlen angefasste Entities, Chunk-Jobs zaehlen Map-Chunks —
--- die beiden Einheiten sind nicht vergleichbar und haben deshalb je eine
--- eigene Einstellung und einen eigenen Topf.
local function build_tasks()
  local surface_indices = {}
  for _, s in pairs(game.surfaces) do surface_indices[#surface_indices + 1] = s.index end
  table.sort(surface_indices)

  local entity_tasks, chunk_tasks = {}, {}
  for j = 1, #JOBS do
    local job = JOBS[j]
    if job.enabled == nil or job.enabled() then
      local target = (job.budget_kind == "chunk") and chunk_tasks or entity_tasks
      if job.per_surface then
        for k = 1, #surface_indices do
          target[#target + 1] = { job = job, si = surface_indices[k] }
        end
      else
        target[#target + 1] = { job = job, si = 0 }
      end
    end
  end
  return entity_tasks, chunk_tasks
end

local function run_task(task, budget, tick)
  local job, si = task.job, task.si
  local st = state_for(job, si)

  if not st.active and tick < st.next_tick then return 0 end

  local ok, cost, payload = pcall(job.run, st, si, budget)
  if not ok then
    -- cost haelt hier die Fehlermeldung
    log("[fdash-exporter] job '" .. job.name .. "' failed: " .. tostring(cost))
    storage.errors[job.name] = tostring(cost)
    -- Zustand verwerfen, damit der naechste Pass sauber startet. Das Budget
    -- bleibt bewusst weitgehend erhalten: der Job ruht jetzt ohnehin eine
    -- Minute, den Tick sollen deshalb die anderen Collector nutzen duerfen.
    storage.jobstate[job.name][si] = { next_tick = tick + ERROR_BACKOFF, active = false }
    return 1
  end

  storage.errors[job.name] = nil
  cost = (type(cost) == "number" and cost > 0) and cost or 1

  if payload ~= nil then
    local key = job.name
    if job.per_surface then
      local surface = game.surfaces[si]
      key = job.name .. "@" .. (surface and surface.name or tostring(si))
    end
    publish.set(key, payload)
    st.active = false
    st.next_tick = tick + job.interval
  else
    st.active = true
  end

  return cost
end

--- Raeumt Zustaende zu Surfaces auf, die es nicht mehr gibt.
function scheduler.forget_surface(si)
  for _, per_job in pairs(storage.jobstate) do
    per_job[si] = nil
  end
  storage.shared_drills[si] = nil
  storage.covered_v[si] = nil
  storage.covered_t[si] = nil
end

--- Reihum abarbeiten, bis der Topf leer ist. Der Startpunkt wandert pro Tick
--- weiter, damit hintere Jobs nicht verhungern, wenn ein vorderer das Budget
--- regelmaessig aufbraucht.
local function drain(tasks, budget, rr_key, tick)
  local n = #tasks
  if n == 0 then return end
  local start = (storage.sched[rr_key] % n) + 1
  storage.sched[rr_key] = start
  for offset = 0, n - 1 do
    if budget <= 0 then break end
    local task = tasks[((start - 1 + offset) % n) + 1]
    budget = budget - run_task(task, budget, tick)
  end
end

function scheduler.tick()
  if not config.enabled() then return end

  local tick = game.tick

  -- Solange die Registries erstbefuellt werden, liefern die Collector nur
  -- Teilzahlen — deshalb erst scannen, dann sammeln. Einzige Ausnahme ist
  -- `meta`: es haengt an keiner Registry und transportiert den
  -- Scan-Fortschritt, damit das Dashboard waehrenddessen nicht blind ist.
  if scan.pending() then
    -- Waehrend des Erstscans laufen die Collector nicht, das Budget liegt also
    -- brach. Ein hoeherer Faktor haelt die Gesamtkosten pro Tick ungefaehr auf
    -- dem Niveau des Normalbetriebs und verkuerzt die blinde Phase deutlich.
    scan.step(config.chunk_budget() * 4)
    if META_JOB then run_task({ job = META_JOB, si = 0 }, 1, tick) end
    return
  end

  local entity_tasks, chunk_tasks = build_tasks()
  drain(entity_tasks, config.entity_budget(), "rr", tick)
  drain(chunk_tasks, config.chunk_budget(), "rr_chunk", tick)
end

return scheduler
