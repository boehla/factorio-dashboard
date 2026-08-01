-- Einmaliger Prototyp-Export nach script-output/fdash/prototypes.json.
--
-- Enthaelt Rezeptstruktur, Items, Ressourcen und Fluidnamen — alles, was das
-- Dashboard fuer den Abhaengigkeitsgraphen und die Icon-Zuordnung braucht.
-- Laeuft nach jedem Mod-Wechsel neu, sonst nie.
--
-- Auch das ist gestueckelt: auf Pyanodons sind es ueber 10.000 Rezepte. Statt
-- eine riesige Tabelle aufzubauen und in einem Rutsch zu serialisieren, wird
-- blockweise serialisiert und an die Datei angehaengt.
--
-- Hinweis: icon_stems bleibt leer. Die Runtime-API gibt keine Icon-Pfade heraus
-- ("LuaItemPrototype doesn't contain key icons"); die Zuordnung Prototyp -> PNG
-- kommt serverseitig aus data-raw-dump.json (factorio.exe --dump-data). Das
-- Feld bleibt aus Kompatibilitaet im Payload.

local util = require("scripts.util")
local publish = require("scripts.publish")

local protoexport = {}

local FILE = "fdash/prototypes.json"
local BLOCK = 400          -- Eintraege pro Serialisierungs-Block

--- Zutaten/Produkte als kompakte {n=name, t=type, a=amount}-Liste.
--- amount beruecksichtigt probability (Erwartungswert), damit sich /min exakt
--- aus energy + crafting_speed berechnen laesst. Die rohe Wahrscheinlichkeit
--- steht zusaetzlich in `p`: fuer eine Durchsatzrechnung ist der Erwartungswert
--- richtig, fuer die Frage "kommt das ueberhaupt sicher heraus" nicht.
local function io_list(list)
  local r = {}
  for _, e in pairs(list or {}) do
    local amt = e.amount or 1
    local prob = e.probability
    if prob then amt = amt * prob end
    r[#r + 1] = { n = e.name, t = e.type, a = amt, p = (prob and prob < 1) and prob or nil }
  end
  return r
end

--- Serialisiert eine Block-Tabelle und schneidet die aeusseren Klammern ab,
--- damit sich die Bloecke zu einem Objekt zusammensetzen lassen.
local function block_body(tbl)
  if not next(tbl) then return "" end
  local json = helpers.table_to_json(tbl)
  return string.sub(json, 2, #json - 1)
end

local function append(text)
  helpers.write_file(FILE, text, true, 0)
end

-- Phasen in fester Reihenfolge. `source` liefert die sortierte Namensliste,
-- `entry` den Wert zum Namen.
local PHASES = {
  {
    key = "recipes",
    names = function()
      local n = {}
      for name, _ in pairs(prototypes.recipe) do n[#n + 1] = name end
      table.sort(n)
      return n
    end,
    entry = function(name)
      local proto = prototypes.recipe[name]
      if not proto then return nil end
      -- Die Runtime-API kennt nur LuaRecipePrototype.energy; energy_required
      -- ist der Data-Stage-Name und wirft hier. Frueher fiel deshalb JEDES
      -- Rezept still auf 0.5 s zurueck.
      local ok, energy = pcall(function() return proto.energy end)
      if not ok or type(energy) ~= "number" or energy <= 0 then
        ok, energy = pcall(function() return proto.energy_required end)
      end
      -- allow_productivity entscheidet, ob sich ein Schritt ueber Module
      -- ueberhaupt beschleunigen laesst — bei Pyanodons die erste Frage, wenn
      -- eine Stufe zu langsam ist.
      local okp, prod_allowed = pcall(function() return proto.allow_productivity end)
      return {
        order = proto.order,
        category = proto.category,
        e = (ok and type(energy) == "number" and energy > 0) and energy or 0.5,
        prodmod = (okp and prod_allowed) and true or nil,
        ing = io_list(proto.ingredients),
        prod = io_list(proto.products)
      }
    end
  },
  {
    key = "technologies",
    names = function()
      local n = {}
      for name, _ in pairs(prototypes.technology) do n[#n + 1] = name end
      table.sort(n)
      return n
    end,
    -- Was eine Technologie kostet und was sie freischaltet. Der Laufzeit-Job
    -- research_state meldet nur, was gerade forschbar ist; ob sich das lohnt,
    -- steht hier. Prototypdaten, also einmalig und zur Laufzeit gratis.
    entry = function(name)
      local proto = prototypes.technology[name]
      if not proto then return nil end

      local pre = {}
      for pname, _ in pairs(proto.prerequisites or {}) do pre[#pre + 1] = pname end
      table.sort(pre)

      local unlocks = {}
      for _, effect in pairs(proto.effects or {}) do
        if effect.type == "unlock-recipe" and effect.recipe then
          unlocks[#unlocks + 1] = effect.recipe
        end
      end

      local packs = {}
      local unit = proto.research_unit_ingredients
      for _, ing in pairs(unit or {}) do
        packs[#packs + 1] = { n = ing.name, a = ing.amount }
      end

      return {
        order = proto.order,
        pre = (#pre > 0) and pre or nil,
        unlocks = (#unlocks > 0) and unlocks or nil,
        count = proto.research_unit_count,
        e = proto.research_unit_energy,
        packs = (#packs > 0) and packs or nil,
        max_level = proto.max_level,
        upgrade = proto.upgrade or nil
      }
    end
  },
  {
    key = "items",
    names = function()
      local n = {}
      for name, _ in pairs(prototypes.item) do n[#n + 1] = name end
      table.sort(n)
      return n
    end,
    entry = function(name)
      local proto = prototypes.item[name]
      if not proto then return nil end
      return { order = proto.order, group = proto.group and proto.group.name or nil }
    end
  },
  {
    key = "resources",
    names = function()
      local n = {}
      for name, proto in pairs(prototypes.entity) do
        if proto.type == "resource" then n[#n + 1] = name end
      end
      table.sort(n)
      return n
    end,
    entry = function(name)
      local info = util.resource_info(name)
      if not info then return nil end
      return {
        infinite = info.infinite,
        mining_time = info.mining_time,
        normal = (info.normal > 0) and info.normal or nil,
        product = info.product
      }
    end
  },
  {
    key = "fluid_names",
    names = function()
      local n = {}
      for name, _ in pairs(prototypes.fluid) do n[#n + 1] = name end
      table.sort(n)
      return n
    end,
    entry = function(_name) return true end
  },
}

function protoexport.start()
  storage.protoexport = { phase = 0, i = 0, names = nil, first = true, done = false }
end

function protoexport.running()
  local s = storage.protoexport
  return s ~= nil and not s.done
end

--- Verarbeitet bis zu `budget` Prototypen. Rueckgabe: verbrauchtes Budget.
function protoexport.step(budget)
  local s = storage.protoexport
  if not s or s.done then return 0 end

  -- Start: Datei anlegen und oeffnendes Objekt schreiben
  if s.phase == 0 then
    helpers.write_file(FILE, "{", false, 0)
    s.phase = 1
    s.i = 0
    s.names = nil
    s.first = true
    append(publish.jstr(PHASES[1].key) .. ":{")
    return 1
  end

  local phase = PHASES[s.phase]
  if not phase then
    -- entities/icon_stems bleiben absichtlich leer (siehe Kopfkommentar)
    append('},"entities":{},"icon_stems":{}}')
    s.done = true
    s.names = nil
    log("[fdash-exporter] prototype export written to script-output/" .. FILE)
    return 1
  end

  if not s.names then
    s.names = phase.names()
    s.i = 0
  end

  local names = s.names
  local total = #names
  local block = {}
  local count = 0

  while count < budget and count < BLOCK and s.i < total do
    s.i = s.i + 1
    local name = names[s.i]
    local value = phase.entry(name)
    if value ~= nil then block[name] = value end
    count = count + 1
  end

  if next(block) then
    local body = block_body(block)
    if body ~= "" then
      append((s.first and "" or ",") .. body)
      s.first = false
    end
  end

  if s.i >= total then
    -- Phase fertig: naechstes Objekt oeffnen
    s.phase = s.phase + 1
    s.names = nil
    s.first = true
    local nxt = PHASES[s.phase]
    if nxt then
      append("}," .. publish.jstr(nxt.key) .. ":{")
    end
  end

  return math.max(1, count)
end

return protoexport
