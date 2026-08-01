-- Evolution, Pollution und (optional) Nester.
--
-- Der billige Teil kostet nichts: Evolution und die Pollution-Bilanz sind ein
-- paar Feldzugriffe auf Statistiken, die das Spiel ohnehin fuehrt.
--
-- Der teure Teil ist das Zaehlen der Nester. Das geht nur ueber die Karte, und
-- eine Karte hat auf einem gewachsenen Save fuenfstellig viele Chunks — deshalb
-- ein eigener rollierender Chunk-Scan hinter einer Einstellung, die per Default
-- aus ist (fdash-threat-scan).

local sweep = require("scripts.sweep")
local chunks = require("scripts.chunks")
local config = require("scripts.config")

-- budget_kind = "chunk": teilt sich das Chunk-Budget mit dem Erz-Scan.
local job = { name = "threat", per_surface = true, interval = 3600, budget_kind = "chunk" }

--- Evolution und ihre Anteile. Die Aufschluesselung sagt, woran es liegt:
--- Zeit laesst sich nicht abstellen, Pollution schon.
local function evolution(surface)
  local enemy = game.forces.enemy
  if not enemy then return nil end

  local ok, factor = pcall(function() return enemy.get_evolution_factor(surface) end)
  if not ok then
    ok, factor = pcall(function() return enemy.evolution_factor end)
  end
  if not (ok and type(factor) == "number") then return nil end

  -- In 2.0 sind die Anteile Funktionen mit Surface-Argument, keine Felder mehr
  -- (LuaForce kennt evolution_factor_by_time schlicht nicht). Erst die Funktion
  -- versuchen, dann das alte Feld — sonst bleibt die Aufschluesselung leer und
  -- man sieht nur, DASS die Evolution steigt, nicht woran es liegt.
  local function part(fn_name, field_name)
    local okf, v = pcall(function() return enemy[fn_name](surface) end)
    if okf and type(v) == "number" then return v end
    local okp, w = pcall(function() return enemy[field_name] end)
    return (okp and type(w) == "number") and w or nil
  end

  return {
    factor = factor,
    by_pollution = part("get_evolution_factor_by_pollution", "evolution_factor_by_pollution"),
    by_time = part("get_evolution_factor_by_time", "evolution_factor_by_time"),
    by_killing_spawners = part("get_evolution_factor_by_killing_spawners",
                               "evolution_factor_by_killing_spawners")
  }
end

--- Pollution: Erzeugung gegen Absorption. Beide stehen in derselben Statistik,
--- Eingang ist Erzeugung, Ausgang Absorption (wie bei den Item-Statistiken).
local function pollution(surface)
  local ok, stats = pcall(function() return game.get_pollution_statistics(surface) end)
  if not (ok and stats) then
    -- Aeltere Versionen kennen nur die globale Statistik.
    ok, stats = pcall(function() return game.pollution_statistics end)
    if not (ok and stats) then return nil end
  end

  local produced, absorbed = 0, 0
  local by_source = {}
  for name, _ in pairs(stats.input_counts) do
    local v = stats.get_flow_count{ name = name, category = "input", count = true,
                                    precision_index = defines.flow_precision_index.one_minute }
    produced = produced + v
    by_source[name] = v
  end
  for name, _ in pairs(stats.output_counts) do
    absorbed = absorbed + stats.get_flow_count{ name = name, category = "output", count = true,
                                                precision_index = defines.flow_precision_index.one_minute }
  end

  -- Ist Pollution in den Karteneinstellungen abgeschaltet, sind alle Werte 0.
  -- Ohne dieses Flag sieht das aus wie ein kaputter Collector.
  local okp, enabled = pcall(function() return game.map_settings.pollution.enabled end)

  return {
    enabled = okp and enabled or false,
    produced_per_min = produced,
    absorbed_per_min = absorbed,
    net_per_min = produced - absorbed,
    on_map = surface.get_total_pollution(),
    by_source = by_source
  }
end

function job.run(st, si, budget)
  local surface = game.surfaces[si]
  if not (surface and surface.valid) then
    st.nests = nil
    return 0, nil
  end

  -- Ohne Nester-Scan ist der Job in einem Durchlauf fertig.
  if not config.threat_scan() then
    return 1, {
      surface = surface.name,
      evolution = evolution(surface),
      pollution = pollution(surface),
      nests = nil,
      nest_scan = false
    }
  end

  if not st.nests then
    chunks.get(si, true)
    st.i = 0
    st.nests = 0
    st.worms = 0
  end

  local spent, done = sweep.chunks(st, si, budget, function(surf, area)
    local n = surf.count_entities_filtered{ area = area, type = "unit-spawner", force = "enemy" }
    local w = surf.count_entities_filtered{ area = area, type = "turret", force = "enemy" }
    st.nests = st.nests + n
    st.worms = st.worms + w
    -- Zwei Zaehl-Aufrufe je Chunk sind teurer als ein Feldzugriff.
    return 1
  end)

  if not done then return spent, nil end

  local nests, worms = st.nests, st.worms
  st.nests, st.worms = nil, nil
  return spent, {
    surface = surface.name,
    evolution = evolution(surface),
    pollution = pollution(surface),
    nests = nests,
    worms = worms,
    nest_scan = true
  }
end

return job
