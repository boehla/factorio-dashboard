-- Forschung: laufender Stand, forschbare Kandidaten und Labor-Rate.
--
-- Zwei Phasen: Labore aus der Registry, danach die Technologieliste. Die
-- Namensliste selbst ist fuer die Session konstant und wird nur einmal
-- gebildet — auf grossen Modpacks sind das mehrere tausend Eintraege.
--
-- Der laufende Stand (current, queue) kostet nichts: das sind ein paar
-- Feldzugriffe am Ende des Durchlaufs, keine Schleife. Teuer ist nur die
-- Technologieliste, und die laeuft budgetiert.

local sweep = require("scripts.sweep")

local job = { name = "research_state", per_surface = true, interval = 900 }

local LABS = { "lab" }

--- Hoechstens so viele "fast forschbare" Technologien melden. Auf Pyanodons
--- haengen tausende Techs an fehlenden Voraussetzungen; die vollstaendige Liste
--- waere weder lesbar noch bezahlbar.
local BLOCKED_LIMIT = 40

local tech_names_cache = nil
local function tech_names(force)
  if not tech_names_cache then
    local names = {}
    for name, _ in pairs(force.technologies) do names[#names + 1] = name end
    table.sort(names)   -- deterministische Reihenfolge ueber alle Peers
    tech_names_cache = names
  end
  return tech_names_cache
end

-- Die Liste der erforschten Technologien ist auf grossen Modpacks mehrere
-- Dutzend Kilobyte gross. Sie aendert sich aber nur, wenn eine Forschung fertig
-- wird — deshalb wird sie nur mitgeschickt, wenn sich die Anzahl seit der
-- letzten Veroeffentlichung geaendert hat. Der Server haelt die zuletzt
-- gesehene Liste.
--
-- Pro Surface gefuehrt, weil dieser Job pro Oberflaeche laeuft und jede ihren
-- eigenen Payload veroeffentlicht; ein gemeinsamer Zaehler wuerde bedeuten, dass
-- nur die erste Oberflaeche die Liste je zu sehen bekommt.
--
-- Modul-lokal und nicht in `storage`: nach einem Ladevorgang wird die Liste
-- einmal zusaetzlich mitgeschickt, das ist billiger als sie in jedem Savegame
-- mitzuschleppen.
local last_researched_count = {}

-- Zusaetzlich alle paar Durchlaeufe unabhaengig von einer Aenderung. Der Server
-- haelt die Liste im Speicher; wird er neu gestartet, ohne dass zwischendurch
-- eine Forschung fertig wird, haette er sie sonst nie wieder gesehen. Bei knapp
-- tausend Namen und diesem Intervall faellt das nicht ins Gewicht.
local RESEND_EVERY = 20
local runs_since_full = {}

--- Zutatenliste einer Technologie als {name=, amount=}-Array.
local function unit_ingredients(t)
  local list = {}
  for _, ing in pairs(t.research_unit_ingredients) do
    list[#list + 1] = { name = ing.name, amount = ing.amount }
  end
  return list
end

--- Effektive Labor-Rate in "Units pro Sekunde" — dieselbe Rechnung wie im
--- Auto-Research des Servers, damit ETA hier und dort dasselbe bedeutet.
local function unit_rate(active_labs, lab_speed, bonus)
  local rate = active_labs * lab_speed * (1 + bonus)
  if rate <= 0 then return nil end
  return rate
end

--- Laufende Forschung inklusive Fortschritt und Restdauer.
local function current_research(force, rate)
  local t = force.current_research
  if not t then return nil end

  local progress = force.research_progress or 0
  local total = t.research_unit_count or 0
  local done = math.floor(total * progress)
  local energy = t.research_unit_energy or 0

  local eta = nil
  if rate and energy > 0 then
    eta = math.floor((total - done) * energy / rate)
  end

  return {
    name = t.name,
    level = t.level,
    progress = progress,
    units_total = total,
    units_done = done,
    energy = energy,
    packs = unit_ingredients(t),
    eta_seconds = eta
  }
end

local function queue_names(force)
  local names = {}
  for _, t in pairs(force.research_queue) do names[#names + 1] = t.name end
  return names
end

function job.run(st, si, budget)
  local force = game.forces.player
  if not force then return 0, nil end

  if not st.phase then
    st.phase = 1
    st.ti, st.slot = 1, 1
    st.active_labs = 0
    st.total_labs = 0
    st.lab_speed = 0
    st.candidates = {}
    st.blocked = {}
    st.blocked_count = 0
    st.researched_count = 0
    st.researched = {}
    st.i = 0
  end

  -- ------------------------------------------------------------- 1 Labore
  if st.phase == 1 then
    local spent, done = sweep.registry(st, si, LABS, budget, function(lab)
      st.total_labs = st.total_labs + 1
      if lab.status == defines.entity_status.working then
        st.active_labs = st.active_labs + 1
      end
      local ok, speed = pcall(function() return lab.prototype.get_researching_speed() end)
      local v = (ok and type(speed) == "number") and speed or 1
      if v > st.lab_speed then st.lab_speed = v end
    end)
    if done then
      st.phase = 2
      st.i = 0
    end
    return spent, nil
  end

  -- ------------------------------------------------------ 2 Technologien
  local candidates = st.candidates
  local blocked = st.blocked
  local researched = st.researched
  local technologies = force.technologies

  local spent, done = sweep.array(st, tech_names(force), budget, function(name)
    local t = technologies[name]
    if not t then return end
    if t.researched then
      st.researched_count = st.researched_count + 1
      researched[#researched + 1] = name
      return
    end
    if not t.enabled then return end

    -- Fehlende Voraussetzungen zaehlen statt nur "gibt es welche": eine
    -- Technologie, der genau eine fehlt, ist die naechste sinnvolle Frage.
    local missing = nil
    for _, pre in pairs(t.prerequisites) do
      if not pre.researched then
        missing = missing or {}
        missing[#missing + 1] = pre.name
      end
    end

    if missing then
      st.blocked_count = st.blocked_count + 1
      if #missing == 1 and #blocked < BLOCKED_LIMIT then
        blocked[#blocked + 1] = { name = name, missing_prerequisites = missing }
      end
      return
    end

    candidates[#candidates + 1] = {
      name = name,
      level = t.level,
      unit_count = t.research_unit_count,
      energy = t.research_unit_energy,
      ingredients = unit_ingredients(t)
    }
  end)

  if not done then return spent, nil end

  local bonus = force.laboratory_speed_modifier
  local rate = unit_rate(st.active_labs, st.lab_speed, bonus)

  local payload = {
    queue_len = #force.research_queue,
    queue = queue_names(force),
    current = current_research(force, rate),
    research_speed_bonus = bonus,
    active_labs = st.active_labs,
    total_labs = st.total_labs,
    lab_speed = st.lab_speed,
    researched_count = st.researched_count,
    total_count = #tech_names(force),
    blocked_count = st.blocked_count,
    blocked = st.blocked,
    candidates = candidates
  }

  -- Vollstaendige Liste bei Aenderung, sonst periodisch (siehe Kopfkommentar).
  runs_since_full[si] = (runs_since_full[si] or 0) + 1
  if last_researched_count[si] ~= st.researched_count or runs_since_full[si] >= RESEND_EVERY then
    payload.researched = researched
    last_researched_count[si] = st.researched_count
    runs_since_full[si] = 0
  end

  st.phase = nil
  st.researched = nil
  st.blocked = nil
  return spent, payload
end

return job
