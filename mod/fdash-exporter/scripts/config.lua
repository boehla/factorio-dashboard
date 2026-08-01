-- Zugriff auf die Runtime-Settings. Bewusst bei jedem Lesen frisch aus
-- settings.global: die Werte sind billig zu lesen und duerfen sich zur
-- Laufzeit aendern, ohne dass irgendwo ein Cache invalidiert werden muss.

local config = {}

local function int(name, fallback)
  local s = settings.global[name]
  if s and type(s.value) == "number" then return math.floor(s.value) end
  return fallback
end

local function bool(name, fallback)
  local s = settings.global[name]
  if s and type(s.value) == "boolean" then return s.value end
  return fallback
end

function config.enabled() return bool("fdash-enabled", true) end

--- Obergrenze fuer die Anzahl Entities, die ein Tick ueber ALLE Collector
--- hinweg anfassen darf. Das ist die zentrale Stellschraube gegen UPS-Verlust.
function config.entity_budget() return int("fdash-entity-budget", 400) end

--- Chunks pro Tick fuer Erstscan und rollierenden Erz-Scan.
function config.chunk_budget() return int("fdash-chunk-budget", 4) end

function config.write_interval() return int("fdash-write-interval", 300) end

function config.file_output() return bool("fdash-file-output", true) end

function config.resource_scan() return bool("fdash-resource-scan", true) end

function config.fluid_scan() return bool("fdash-fluid-scan", true) end

--- Aus, solange es niemand einschaltet: der Sweep ueber alle Kisten ist der
--- teuerste Collector des Mods (ein Inventar-Read je Kiste).
function config.container_scan() return bool("fdash-container-scan", false) end

--- Nester zaehlen heisst die ganze Karte abgehen. Der Rest des Threat-Jobs
--- (Evolution, Pollution) laeuft auch ohne.
function config.threat_scan() return bool("fdash-threat-scan", false) end

function config.covered_refresh_ticks()
  return int("fdash-covered-refresh-minutes", 10) * 60 * 60
end

return config
