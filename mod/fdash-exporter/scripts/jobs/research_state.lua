-- Lesende Basis fuer Auto-Research: forschbare Kandidaten + Labor-Rate.
--
-- Zwei Phasen: Labore aus der Registry, danach die Technologieliste. Die
-- Namensliste selbst ist fuer die Session konstant und wird nur einmal
-- gebildet — auf grossen Modpacks sind das mehrere tausend Eintraege.

local sweep = require("scripts.sweep")

local job = { name = "research_state", per_surface = true, interval = 900 }

local LABS = { "lab" }

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

function job.run(st, si, budget)
  local force = game.forces.player
  if not force then return 0, nil end

  if not st.phase then
    st.phase = 1
    st.ti, st.slot = 1, 1
    st.active_labs = 0
    st.lab_speed = 0
    st.candidates = {}
    st.i = 0
  end

  -- ------------------------------------------------------------- 1 Labore
  if st.phase == 1 then
    local spent, done = sweep.registry(st, si, LABS, budget, function(lab)
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
  local technologies = force.technologies

  local spent, done = sweep.array(st, tech_names(force), budget, function(name)
    local t = technologies[name]
    if not t or t.researched or not t.enabled then return end
    for _, pre in pairs(t.prerequisites) do
      if not pre.researched then return end
    end
    local ingredients = {}
    for _, ing in pairs(t.research_unit_ingredients) do
      ingredients[#ingredients + 1] = { name = ing.name, amount = ing.amount }
    end
    candidates[#candidates + 1] = {
      name = name,
      level = t.level,
      unit_count = t.research_unit_count,
      energy = t.research_unit_energy,
      ingredients = ingredients
    }
  end)

  if not done then return spent, nil end

  local payload = {
    queue_len = #force.research_queue,
    research_speed_bonus = force.laboratory_speed_modifier,
    active_labs = st.active_labs,
    lab_speed = st.lab_speed,
    candidates = candidates
  }
  st.phase = nil
  return spent, payload
end

return job
