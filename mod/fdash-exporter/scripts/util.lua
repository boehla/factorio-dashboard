-- Gemeinsame Helfer. Entspricht dem frueheren _prelude.lua der RCON-Snippets,
-- nur dass die teuren Prototyp-Ableitungen hier einmal pro Session gecacht
-- werden statt bei jedem Poll neu zu laufen.
--
-- WICHTIG: Alles in diesem Modul ist aus Prototypen abgeleitet und damit fuer
-- die gesamte Session konstant. Solche Caches duerfen NICHT nach `storage`,
-- sondern gehoeren in Modul-Locals: sie sind auf jedem Peer identisch (kein
-- Desync-Risiko) und werden nach dem Laden einfach neu aufgebaut.

local util = {}

-- ---------------------------------------------------------------- Enum-Namen

local status_names = nil
local train_state_names = nil

--- Enum-Wert -> lesbarer Name, unbekannt (modded) als unknown_<n>.
function util.status_name(v)
  if not status_names then
    status_names = {}
    for k, val in pairs(defines.entity_status) do status_names[val] = k end
  end
  return status_names[v] or ("unknown_" .. tostring(v))
end

function util.train_state_name(v)
  if not train_state_names then
    train_state_names = {}
    for k, val in pairs(defines.train_state) do train_state_names[val] = k end
  end
  return train_state_names[v] or ("unknown_" .. tostring(v))
end

--- Status-Werte, bei denen eine Maschine wirklich auf Zutaten wartet.
--- Defensiv ueber Namen aufgebaut, damit ein in dieser Version fehlender
--- Status keinen nil-Key erzeugt.
local starving_set = nil
function util.starving_statuses()
  if not starving_set then
    starving_set = {}
    for _, n in pairs({ "no_ingredients", "item_ingredient_shortage",
                        "no_input_fluid", "low_input_fluid", "fluid_ingredient_shortage" }) do
      local v = defines.entity_status[n]
      if v then starving_set[v] = true end
    end
  end
  return starving_set
end

-- ------------------------------------------------------------- Statistiken

--- count=true (Default): absolute Menge ueber das Zeitfenster -> Items/Minute.
--- count=false: Mittelwert pro Tick -> fuer Strom, wo der Wert J/Tick ist und
--- erst x60 Watt ergibt.
--- Bewusst ohne pcall: die Namen stammen immer aus input_counts/output_counts
--- derselben Statistik bzw. sind gegen prototypes.item geprueft, koennen also
--- nicht unbekannt sein. Ein pcall pro Aufruf waere hier echter Overhead —
--- diese Funktion laeuft mehrere tausend Mal pro Durchlauf.
function util.statflow(stats, name, cat, count)
  if count == nil then count = true end
  return stats.get_flow_count{
    name = name, category = cat, count = count,
    precision_index = defines.flow_precision_index.one_minute
  }
end

-- ---------------------------------------------------- Prototyp-Ableitungen

local recipe_item_cache = nil

--- Item-Name, unter dem eine Maschine gruppiert wird: bevorzugt das
--- Haupt-Produkt des Rezepts, sonst das erste Produkt, sonst der Rezeptname.
--- Gruppierung nach Item (nicht nach Rezept), damit alle Maschinen, die
--- dasselbe Item ueber verschiedene Rezepte herstellen, zusammenzaehlen.
function util.recipe_item(recipe_name)
  if not recipe_item_cache then recipe_item_cache = {} end
  local cached = recipe_item_cache[recipe_name]
  if cached then return cached end
  local result = recipe_name
  local proto = prototypes.recipe[recipe_name]
  if proto then
    local mp = proto.main_product
    if mp and mp.name then
      result = mp.name
    else
      local products = proto.products
      if products and products[1] and products[1].name then result = products[1].name end
    end
  end
  recipe_item_cache[recipe_name] = result
  return result
end

local recipe_ing_cache = nil
local EMPTY = {}

--- Zutatenliste eines Rezepts als {name=, amount=}-Array.
--- LuaRecipe.ingredients legt bei JEDEM Zugriff neue Tabellen an; die Zutaten
--- sind aber eine Prototyp-Eigenschaft und aendern sich nie.
function util.recipe_ingredients(recipe_name)
  if not recipe_ing_cache then recipe_ing_cache = {} end
  local cached = recipe_ing_cache[recipe_name]
  if cached then return cached end
  local list = EMPTY
  local proto = prototypes.recipe[recipe_name]
  if proto then
    list = {}
    for _, ing in pairs(proto.ingredients) do
      list[#list + 1] = { name = ing.name, amount = ing.amount or 0 }
    end
  end
  recipe_ing_cache[recipe_name] = list
  return list
end

local ingredient_set = nil

--- Menge aller Items/Fluids, die in irgendeinem Rezept als Zutat vorkommen.
--- Was hier fehlt, wird nur "on the fly" fuer den Bau gefertigt und ist fuer
--- die Produktions-Ratio uninteressant. Auf Pyanodons sind das >10k Rezepte —
--- frueher lief diese Schleife bei JEDEM Produktions-Poll.
function util.is_ingredient_set()
  if not ingredient_set then
    ingredient_set = {}
    for _, proto in pairs(prototypes.recipe) do
      for _, ing in pairs(proto.ingredients) do
        ingredient_set[ing.name] = true
      end
    end
  end
  return ingredient_set
end

local resource_info_cache = nil

--- Pro Ressourcen-Prototyp: Abbauzeit, gefoerdertes Item, infinite-Flag.
--- Bei modded Ressourcen heisst das gefoerderte Item oft anders als die
--- Ressource (oder ist ein Fluid) — deshalb ueber mineable_properties.
function util.resource_info(name)
  if not resource_info_cache then
    resource_info_cache = {}
    for pname, proto in pairs(prototypes.entity) do
      if proto.type == "resource" then
        local mp = proto.mineable_properties
        local product = nil
        if mp and mp.products and mp.products[1] and mp.products[1].type == "item" then
          product = mp.products[1].name
        end
        resource_info_cache[pname] = {
          infinite = proto.infinite_resource or false,
          mining_time = (mp and mp.mining_time) or 1,
          normal = proto.normal_resource_amount or 0,
          product = product
        }
      end
    end
  end
  return resource_info_cache[name]
end

-- ------------------------------------------------------------------ Sonstiges

--- Leere Lua-Tabellen serialisiert table_to_json als [] statt {}. Wo das
--- Frontend ein Objekt erwartet, muss also explizit nil gesetzt werden.
function util.nil_if_empty(t)
  if t and next(t) then return t end
  return nil
end

function util.surface_key(job_name, surface_name)
  return job_name .. "@" .. surface_name
end

-- ---------------------------------------------------------- Chunk-Schluessel
--
-- Chunk-Koordinaten als eine Zahl. Ein String-Schluessel ("12:-7") waere
-- lesbarer, aber diese Tabellen haben auf einer grossen Karte zehntausende
-- Eintraege und landen in `storage` — jeder String dort kostet beim Speichern.
-- Der Offset faengt negative Koordinaten ab; ±32768 Chunks sind ±1 Mio. Kacheln
-- und damit weit jenseits jeder real bespielten Karte.

local CHUNK_OFFSET = 32768
local CHUNK_SPAN = 65536

function util.chunk_key(cx, cy)
  return (cx + CHUNK_OFFSET) * CHUNK_SPAN + (cy + CHUNK_OFFSET)
end

function util.chunk_xy(key)
  local cx = math.floor(key / CHUNK_SPAN) - CHUNK_OFFSET
  local cy = key % CHUNK_SPAN - CHUNK_OFFSET
  return cx, cy
end

--- Chunk-Schluessel zu einer Weltposition.
function util.chunk_key_at(x, y)
  return util.chunk_key(math.floor(x / 32), math.floor(y / 32))
end

return util
