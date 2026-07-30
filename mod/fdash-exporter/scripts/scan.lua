-- Erstbefuellung der Registries, gechunkt ueber viele Ticks.
--
-- Laeuft genau einmal pro Save (on_init) bzw. nach einem Mod-Update
-- (on_configuration_changed) und beim Anlegen einer neuen Surface. Danach
-- halten die Build-Events in events.lua die Registries aktuell.
--
-- Pro Chunk genau EIN find_entities_filtered ueber alle getrackten Typen —
-- Entities auf Chunk-Grenzen tauchen dabei mehrfach auf, das faengt der
-- Dedup ueber unit_number in registry.add ab.

local registry = require("scripts.registry")
local chunks = require("scripts.chunks")

local scan = {}

function scan.init()
  storage.scan = { queue = {}, si = nil, i = 0, total = 0, done_chunks = 0 }
end

--- Surface zur Erstbefuellung vormerken.
function scan.enqueue(si)
  local q = storage.scan.queue
  for _, v in pairs(q) do
    if v == si then return end
  end
  q[#q + 1] = si
end

function scan.enqueue_all()
  for _, surface in pairs(game.surfaces) do
    scan.enqueue(surface.index)
  end
end

function scan.pending()
  return storage.scan.si ~= nil or #storage.scan.queue > 0
end

--- Fortschritt 0..1 fuer die Statusausgabe.
function scan.progress()
  local s = storage.scan
  if not s.si then return (#s.queue > 0) and 0 or 1 end
  if s.total <= 0 then return 0 end
  return s.i / s.total
end

--- Verarbeitet bis zu `budget` Chunks. Liefert die Anzahl tatsaechlich
--- verarbeiteter Chunks zurueck (fuer die Budget-Abrechnung des Schedulers).
function scan.step(budget)
  local s = storage.scan
  local done = 0

  while done < budget do
    -- naechste Surface aus der Warteschlange holen
    if not s.si then
      local si = table.remove(s.queue, 1)
      if not si then return done end
      local surface = game.surfaces[si]
      if surface and surface.valid then
        local list = chunks.rebuild(si)
        s.si = si
        s.i = 0
        s.total = list.n
      end
    else
      local surface = game.surfaces[s.si]
      local list = storage.chunks[s.si]
      if not (surface and surface.valid and list) then
        s.si = nil
      elseif s.i >= list.n then
        -- Surface fertig
        s.si = nil
      else
        s.i = s.i + 1
        local ents = surface.find_entities_filtered{
          area = chunks.area(list, s.i),
          type = registry.TYPES
        }
        for k = 1, #ents do
          registry.add(ents[k])
        end
        done = done + 1
        s.done_chunks = s.done_chunks + 1
      end
    end
  end

  return done
end

return scan
