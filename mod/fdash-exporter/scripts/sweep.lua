-- Budgetierter Iterator ueber Registries und Chunk-Listen.
--
-- Alle Collector arbeiten nach demselben Muster ("Rolling Pass"):
--   1. Pass-Start: leerer Akkumulator
--   2. pro Tick: hoechstens `budget` Entities anfassen, Ergebnis aufaddieren
--   3. Pass-Ende: Akkumulator einfrieren und veroeffentlichen (Double-Buffer)
--
-- Der veroeffentlichte Stand ist dadurch immer in sich konsistent (er stammt
-- aus genau einem vollen Durchlauf), nur eben bis zu einer Pass-Dauer alt.
-- Fuer ein Dashboard ist das genau der richtige Tausch: aus einem 200-ms-Spike
-- alle 5 Sekunden werden ein paar Dutzend Mikrosekunden pro Tick.

local registry = require("scripts.registry")
local chunks = require("scripts.chunks")

local sweep = {}

--- Iteriert die Registries mehrerer Typen einer Surface.
---
--- `st` haelt den Cursor (`ti` = Index in `types`, `slot` = Slot im Bucket) und
--- muss vom Aufrufer in `storage` liegen. Rueckgabe: verbrauchtes Budget und
--- ob der Durchlauf komplett ist.
---
--- Ungueltige Entities werden hier aussortiert — das ist der Ersatz fuer
--- Destroy-Events (siehe registry.lua).
function sweep.registry(st, si, types, budget, fn)
  local spent, scanned = 0, 0
  -- Schutz gegen Tombstone-Wueste: nach massenhaftem Abriss koennen viele
  -- Slots leer sein, das Ueberspringen soll das Budget nicht sprengen.
  local max_scan = budget * 4 + 16

  while true do
    local etype = types[st.ti]
    if not etype then return spent, true end

    local b = registry.peek(si, etype)
    if not b or st.slot > b.n then
      st.ti = st.ti + 1
      st.slot = 1
    else
      local slot = st.slot
      st.slot = slot + 1
      local e = b.ents[slot]
      scanned = scanned + 1
      if e ~= nil then
        if e.valid then
          -- Der Callback darf Zusatzkosten zurueckgeben, wenn er fuer dieses
          -- eine Entity etwas wesentlich Teureres getan hat als ein paar
          -- Feldzugriffe (drills.lua misst per find_entities_filtered das Erz
          -- im Foerderradius). Sie zaehlen SOFORT gegen das Budget — wird die
          -- Zusatzarbeit erst nach der Schleife verrechnet, laeuft der Tick
          -- munter weiter und das Budget schuetzt genau dann nicht, wenn es
          -- gebraucht wird.
          local extra = fn(e)
          spent = spent + 1
          if type(extra) == "number" and extra > 0 then spent = spent + extra end
        else
          registry.release(b, slot)
        end
      end
      if spent >= budget or scanned >= max_scan then return spent, false end
    end
  end
end

--- Iteriert die Chunk-Liste einer Surface.
---
--- Budget wird hier in Chunks gezaehlt, nicht in Entities: was in einem Chunk
--- steckt, laesst sich nicht teilweise abfragen. Ein dichter Erz-Chunk kann
--- deshalb einmalig ueber das Budget schiessen — das ist der Grund, warum das
--- Chunk-Budget per Default sehr klein ist (4).
function sweep.chunks(st, si, budget, fn)
  local surface = game.surfaces[si]
  local list = storage.chunks[si]
  if not (surface and surface.valid and list) then return 0, true end

  local spent = 0
  while spent < budget do
    if st.i >= list.n then return spent, true end
    st.i = st.i + 1
    -- Wie bei sweep.registry darf der Callback Zusatzkosten melden. Ein Chunk
    -- ist eben nicht wie der andere: ein leerer kostet nichts, ein voll mit Erz
    -- belegter liefert ueber tausend Entities. Ohne diese Rueckmeldung waere
    -- das Budget in Chunks gezaehlt und die tatsaechliche Arbeit unbegrenzt.
    local extra = fn(surface, chunks.area(list, st.i))
    spent = spent + 1
    if type(extra) == "number" and extra > 0 then spent = spent + extra end
  end
  return spent, false
end

--- Iteriert ein einfaches Array aus `storage` (z. B. die zum Pass-Start
--- eingefrorene Zugliste oder eine Namensliste aus der Statistik).
function sweep.array(st, arr, budget, fn)
  local spent = 0
  local n = #arr
  while spent < budget do
    if st.i >= n then return spent, true end
    st.i = st.i + 1
    fn(arr[st.i], st.i)
    spent = spent + 1
  end
  return spent, false
end

return sweep
