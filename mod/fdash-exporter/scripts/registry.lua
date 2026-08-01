-- Event-getriebene Entity-Registries.
--
-- Das ist der Kern des Umbaus. Die alten RCON-Snippets riefen bei JEDEM Poll
-- surface.find_entities_filtered{type=...} ueber die ganze Oberflaeche auf —
-- auf einer grossen Karte sind das sechsstellige Entity-Zahlen, komplett
-- synchron im Tick. Hier wird stattdessen genau einmal gescannt (gechunkt,
-- siehe scan.lua) und danach halten Build-Events die Liste aktuell.
--
-- Geloeschte Entities fangen wir bewusst NICHT ueber Destroy-Events ab: die
-- feuern bei Kaempfen und Deconstruction massenhaft und wuerden genau die
-- Tick-Zeit kosten, die wir sparen wollen. Stattdessen werden ungueltige
-- Eintraege lazy beim Sweep aussortiert — spaetestens nach einem vollen Pass
-- ist die Registry wieder sauber.
--
-- Datenstruktur pro (Surface, Typ):
--   ents  : slot -> LuaEntity        (nil = Tombstone)
--   uns   : slot -> unit_number
--   index : unit_number -> slot      (Dedup beim Hinzufuegen)
--   free  : Stack freier Slots       (statt teurer Kompaktierung)
--   n     : hoechster je belegter Slot
--
-- Die Sweeps laufen ueber `slot = 1..n`. Neue Eintraege landen in Luecken oder
-- am Ende; das ist waehrend eines laufenden Passes unproblematisch, weil wir
-- ueber einen Integer-Index iterieren und nicht ueber next().

local registry = {}

--- Alle Typen, fuer die eine Registry gefuehrt wird.
registry.TYPES = {
  "assembling-machine",
  "furnace",
  "rocket-silo",
  "mining-drill",
  "lab",
  "generator",
  "accumulator",
  "electric-pole",
  "roboport",
  "constant-combinator",
  "train-stop",
  "storage-tank",
  "container",
  "logistic-container",
}

local type_set = nil
local function tracked(t)
  if not type_set then
    type_set = {}
    for _, v in pairs(registry.TYPES) do type_set[v] = true end
  end
  return type_set[t] == true
end

registry.tracked = tracked

local function new_bucket()
  return { ents = {}, uns = {}, index = {}, free = {}, nfree = 0, n = 0 }
end

--- Bucket fuer (Surface-Index, Typ); legt ihn bei Bedarf an.
function registry.bucket(si, etype)
  local per_surface = storage.reg[si]
  if not per_surface then
    per_surface = {}
    storage.reg[si] = per_surface
  end
  local b = per_surface[etype]
  if not b then
    b = new_bucket()
    per_surface[etype] = b
  end
  return b
end

--- Bucket nur lesen (kein Anlegen) — fuer Sweeps ueber evtl. leere Surfaces.
function registry.peek(si, etype)
  local per_surface = storage.reg[si]
  if not per_surface then return nil end
  return per_surface[etype]
end

--- Fuegt eine Entity hinzu. Idempotent: doppelte Aufrufe (Erstscan trifft eine
--- Entity, die parallel schon per Build-Event kam) sind harmlos.
function registry.add(entity)
  if not (entity and entity.valid) then return end
  local etype = entity.type
  if not tracked(etype) then return end
  local u = entity.unit_number
  if not u then return end

  local b = registry.bucket(entity.surface.index, etype)
  if b.index[u] then return end

  local slot
  if b.nfree > 0 then
    slot = b.free[b.nfree]
    b.free[b.nfree] = nil
    b.nfree = b.nfree - 1
  else
    slot = b.n + 1
    b.n = slot
  end
  b.ents[slot] = entity
  b.uns[slot] = u
  b.index[u] = slot
end

--- Gibt einen Slot frei (wird vom Sweep aufgerufen, wenn die Entity ungueltig
--- geworden ist). Die unit_number kommt aus `uns`, weil eine ungueltige
--- LuaEntity sie nicht mehr herausgibt.
function registry.release(b, slot)
  local u = b.uns[slot]
  if u then b.index[u] = nil end
  b.ents[slot] = nil
  b.uns[slot] = nil
  b.nfree = b.nfree + 1
  b.free[b.nfree] = slot
end

--- Verwirft alle Registries einer Surface (Surface geloescht/geleert).
function registry.drop_surface(si)
  storage.reg[si] = nil
end

--- Ungefaehre Anzahl lebender Eintraege — nur fuer Diagnose/Statuszeile.
function registry.count(si, etype)
  local b = registry.peek(si, etype)
  if not b then return 0 end
  return b.n - b.nfree
end

return registry
