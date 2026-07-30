-- Chunk-Liste pro Surface.
--
-- Warum ueberhaupt gecacht: LuaSurface.get_chunks() liefert einen Iterator, der
-- sich nicht nach `storage` speichern laesst — ein ueber mehrere Ticks
-- verteilter Scan kann ihn also nicht anhalten und fortsetzen. Deshalb wird die
-- Positionsliste einmal materialisiert (zwei flache Integer-Arrays, guenstiger
-- als eine Liste von {x,y}-Tabellen) und danach nur noch per
-- on_chunk_generated fortgeschrieben.
--
-- Die Materialisierung selbst ist der einzige nicht-budgetierte Vorgang im Mod.
-- Sie laeuft in on_init / on_configuration_changed, also waehrend des Ladens,
-- wo ein Spike niemanden stoert.

local chunks = {}

local function new_list()
  return { cx = {}, cy = {}, n = 0, dirty = false }
end

--- Baut die Liste fuer eine Surface komplett neu auf.
function chunks.rebuild(si)
  local surface = game.surfaces[si]
  local list = new_list()
  if surface and surface.valid then
    local cx, cy, n = list.cx, list.cy, 0
    for chunk in surface.get_chunks() do
      n = n + 1
      cx[n] = chunk.x
      cy[n] = chunk.y
    end
    list.n = n
  end
  storage.chunks[si] = list
  return list
end

--- Liste holen; baut sie bei Bedarf (oder wenn als dirty markiert) neu auf.
--- `allow_rebuild` sollte nur an Pass-Grenzen true sein, damit der Neuaufbau
--- nicht mitten in einem laufenden Sweep den Cursor entwertet.
function chunks.get(si, allow_rebuild)
  local list = storage.chunks[si]
  if not list or (list.dirty and allow_rebuild) then
    return chunks.rebuild(si)
  end
  return list
end

function chunks.mark_dirty(si)
  local list = storage.chunks[si]
  if list then list.dirty = true end
end

--- Neu generierten Chunk anhaengen. Laeuft potenziell sehr oft (Erkundung,
--- Roboter-Bau an der Grenze), deshalb bewusst minimal gehalten.
function chunks.append(si, x, y)
  local list = storage.chunks[si]
  if not list then return end
  local n = list.n + 1
  list.cx[n] = x
  list.cy[n] = y
  list.n = n
end

function chunks.drop_surface(si)
  storage.chunks[si] = nil
end

--- Bounding-Box eines Chunks als area fuer find_entities_filtered.
function chunks.area(list, i)
  local x, y = list.cx[i] * 32, list.cy[i] * 32
  return { { x, y }, { x + 32, y + 32 } }
end

return chunks
