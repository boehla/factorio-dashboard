-- Veroeffentlichung: Serialisierung, Snapshot-Dokument, Dateiausgabe.
--
-- Jeder Collector serialisiert sein Ergebnis genau einmal — am Ende seines
-- Durchlaufs — und legt den fertigen JSON-String ab. Das Snapshot-Dokument
-- entsteht danach nur noch durch Aneinanderhaengen dieser Fragmente; es wird
-- nie eine grosse Tabelle als Ganzes serialisiert.
--
-- Dateiausgabe mit Rotation: der Snapshot geht abwechselnd in eine von drei
-- Dateien, danach wird index.json geschrieben. Ein Leser, der erst index.json
-- liest und dann die dort genannte Datei, kann damit keine halb geschriebene
-- Datei erwischen — die naechsten zwei Schreibvorgaenge gehen in andere Slots.

local config = require("scripts.config")

local publish = {}

local DIR = "fdash/"
local SLOTS = 3
local PROTOCOL = 1

-- ------------------------------------------------------------ JSON-Helfer

local ESCAPES = {
  ['"'] = '\\"', ['\\'] = '\\\\', ['\n'] = '\\n',
  ['\r'] = '\\r', ['\t'] = '\\t', ['\b'] = '\\b', ['\f'] = '\\f'
}

--- JSON-String-Literal. Surface-Namen koennen aus Mods stammen und beliebige
--- Zeichen enthalten, deshalb wird hier wirklich escaped statt nur zitiert.
local function jstr(s)
  return '"' .. tostring(s):gsub('[%c"\\]', function(c)
    return ESCAPES[c] or string.format('\\u%04x', string.byte(c))
  end) .. '"'
end

publish.jstr = jstr

-- --------------------------------------------------------------- Ablegen

--- Ergebnis eines abgeschlossenen Durchlaufs uebernehmen.
---
--- Anmerkung zu den Zahlen im Snapshot: helpers.table_to_json schreibt jeden
--- nicht-ganzzahligen Double in voller Dezimalentwicklung aus — 1.6 wird zu
--- "1.600000000000000088817841970012523233890533447265625". Ein Runden auf n
--- Nachkommastellen bringt dagegen nichts: 0.333 ist als Double genauso wenig
--- exakt und druckt genauso lang. Kuerzer wird nur, was exakt ganzzahlig ist.
--- Gemessen an einer echten Py-Karte liessen sich so keine 2 % der Payload
--- sparen, und nur um den Preis, Maschinengeschwindigkeiten und Verhaeltnisse
--- auf ganze Zahlen zu runden. Bleibt bewusst wie es ist.
function publish.set(key, payload)
  local ok, json = pcall(function() return helpers.table_to_json(payload) end)
  if not ok then
    log("[fdash-exporter] serialisation failed for '" .. key .. "': " .. tostring(json))
    return
  end
  storage.published[key] = json
  storage.published_tick[key] = game.tick
  storage.pubseq = storage.pubseq + 1
end

--- Einzelnes Fragment (fuer das Remote-Interface).
function publish.get(key)
  return storage.published[key]
end

--- Vollstaendiges Snapshot-Dokument als String.
function publish.snapshot_json()
  local parts = {
    '{"protocol":', PROTOCOL,
    ',"seq":', storage.pubseq,
    ',"tick":', game.tick,
    ',"jobs":{'
  }
  local n = #parts

  -- Stabile Schluesselreihenfolge: pairs() ist in Lua nicht deterministisch,
  -- und der Snapshot soll auf allen Peers byte-gleich sein.
  local keys = {}
  for k, _ in pairs(storage.published) do keys[#keys + 1] = k end
  table.sort(keys)

  for i = 1, #keys do
    local k = keys[i]
    if i > 1 then n = n + 1; parts[n] = "," end
    n = n + 1; parts[n] = jstr(k)
    n = n + 1; parts[n] = ':{"tick":'
    n = n + 1; parts[n] = storage.published_tick[k] or 0
    n = n + 1; parts[n] = ',"data":'
    n = n + 1; parts[n] = storage.published[k]
    n = n + 1; parts[n] = "}"
  end

  n = n + 1; parts[n] = "}}"
  return table.concat(parts)
end

-- ---------------------------------------------------------- Dateiausgabe

--- Schreibt Snapshot + Index, wenn das Intervall abgelaufen ist.
function publish.write_if_due()
  if not config.file_output() then return end
  local tick = game.tick
  if tick < storage.next_write then return end
  storage.next_write = tick + config.write_interval()

  -- Nichts veroeffentlicht -> nichts schreiben (spart den Leerlauf-Write
  -- waehrend des Erstscans).
  if storage.pubseq == storage.written_seq then return end

  local slot = storage.write_slot % SLOTS
  storage.write_slot = slot + 1
  local name = "snapshot-" .. slot .. ".json"

  helpers.write_file(DIR .. name, publish.snapshot_json(), false, 0)
  helpers.write_file(DIR .. "index.json", table.concat({
    '{"protocol":', PROTOCOL,
    ',"seq":', storage.pubseq,
    ',"tick":', tick,
    ',"file":', jstr(name), '}'
  }), false, 0)

  storage.written_seq = storage.pubseq
end

return publish
