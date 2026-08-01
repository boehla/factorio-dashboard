-- fdash-exporter — Live-Statistiken fuer ein externes Dashboard.
--
-- Zwei Ausgabewege, beide gleichzeitig nutzbar:
--   * Datei:  script-output/fdash/index.json + snapshot-N.json
--   * RCON:   /silent-command rcon.print(remote.call("fdash", "snapshot"))
--
-- Entwurfsziel ist ausschliesslich die Tick-Zeit. Alles, was mit der
-- Fabrikgroesse skaliert, laeuft budgetiert ueber viele Ticks verteilt; der
-- Mod hat genau einen on_tick-Handler mit einer harten Obergrenze.

local registry = require("scripts.registry")
local chunks = require("scripts.chunks")
local scan = require("scripts.scan")
local scheduler = require("scripts.scheduler")
local publish = require("scripts.publish")
local protoexport = require("scripts.protoexport")
local events = require("scripts.events")
local config = require("scripts.config")

-- --------------------------------------------------------------- storage

local function init_storage()
  storage.reg = storage.reg or {}
  storage.chunks = storage.chunks or {}
  storage.jobstate = storage.jobstate or {}
  storage.published = storage.published or {}
  storage.published_tick = storage.published_tick or {}
  storage.pubseq = storage.pubseq or 0
  storage.written_seq = storage.written_seq or -1
  storage.write_slot = storage.write_slot or 0
  storage.next_write = storage.next_write or 0
  storage.sched = storage.sched or { rr = 0, rr_chunk = 0 }
  storage.shared_drills = storage.shared_drills or {}
  storage.covered_v = storage.covered_v or {}
  storage.covered_t = storage.covered_t or {}
  storage.errors = storage.errors or {}
  -- Zerstoerte Gebaeude der Spieler-Force (siehe events.lua). Bewusst klein:
  -- ein Zaehler, eine Namensliste und ein Ring der letzten 16 Vorfaelle.
  storage.deaths = storage.deaths or { count = 0, by_name = {}, recent = {}, ri = 0 }
  if not storage.scan then scan.init() end
end

--- Kompletter Neuaufbau: nach Mod-Aenderungen koennen sich Prototypen,
--- Entity-Typen und Rezepte geaendert haben.
local function full_reset()
  storage.reg = {}
  storage.chunks = {}
  storage.jobstate = {}
  storage.published = {}
  storage.published_tick = {}
  storage.shared_drills = {}
  storage.covered_v = {}
  storage.covered_t = {}
  storage.errors = {}
  scan.init()
  scan.enqueue_all()
  protoexport.start()
end

script.on_init(function()
  init_storage()
  full_reset()
end)

script.on_configuration_changed(function()
  init_storage()
  full_reset()
end)

-- ------------------------------------------------------------------ tick

script.on_event(defines.events.on_tick, function()
  if not config.enabled() then return end

  -- Der Prototyp-Export laeuft exklusiv: er ist kurz (einige Sekunden) und
  -- soll sich nicht mit dem Erstscan zu einem doppelten Budget addieren.
  if protoexport.running() then
    protoexport.step(math.max(50, config.entity_budget()))
    return
  end

  scheduler.tick()
  publish.write_if_due()
end)

events.register()

-- ------------------------------------------------------- Remote-Interface

--- Fuer den RCON-Weg. Alle Rueckgaben sind fertige JSON-Strings, damit der
--- Aufrufer nichts mehr serialisieren muss:
---   /silent-command rcon.print(remote.call("fdash", "snapshot"))
remote.add_interface("fdash", {

  --- Vollstaendiger Snapshot (alle Jobs).
  snapshot = function()
    return publish.snapshot_json()
  end,

  --- Aktuelle Sequenznummer. Bewusst winzig: der RCON-Leser fragt sie bei
  --- jedem Poll ab und holt den vollen Snapshot nur, wenn sie sich geaendert
  --- hat. Jeder RCON-Aufruf belegt einen Tick-Slot, also soll der Normalfall
  --- (nichts Neues) so billig wie moeglich sein.
  seq = function()
    return tostring(storage.pubseq)
  end,

  --- Einzelnes Job-Fragment, z. B. "power@nauvis". nil wenn noch nichts vorliegt.
  get = function(key)
    return storage.published[key]
  end,

  --- Verfuegbare Job-Schluessel.
  keys = function()
    local keys = {}
    for k, _ in pairs(storage.published) do keys[#keys + 1] = k end
    table.sort(keys)
    return helpers.table_to_json(keys)
  end,

  --- Betriebszustand: laeuft noch ein Erstscan, wie sind die Budgets gesetzt,
  --- gab es Fehler in einzelnen Collectorn?
  status = function()
    local reg_counts = {}
    for _, surface in pairs(game.surfaces) do
      local per_type = {}
      for _, t in pairs(registry.TYPES) do
        local c = registry.count(surface.index, t)
        if c > 0 then per_type[t] = c end
      end
      reg_counts[surface.name] = per_type
    end
    return helpers.table_to_json({
      version = script.active_mods["fdash-exporter"],
      protocol = 1,
      enabled = config.enabled(),
      scanning = scan.pending(),
      scan_progress = scan.progress(),
      exporting_prototypes = protoexport.running(),
      seq = storage.pubseq,
      tick = game.tick,
      entity_budget = config.entity_budget(),
      chunk_budget = config.chunk_budget(),
      write_interval = config.write_interval(),
      file_output = config.file_output(),
      registry = reg_counts,
      errors = next(storage.errors) and storage.errors or nil
    })
  end,

  --- Einziger schreibender Aufruf. Validierung passiert hier, nicht beim
  --- Aufrufer: nur eine freigeschaltete, unerforschte Technologie bei leerer
  --- Warteschlange wird gesetzt.
  set_research = function(name)
    local force = game.forces.player
    local t = force and force.technologies[name]
    if t and not t.researched and t.enabled and #force.research_queue == 0 then
      force.add_research(t)
      return helpers.table_to_json({ result = "ok", tech = name })
    end
    local reason = (not force and "no_force")
      or (not t and "missing")
      or (t.researched and "done")
      or (not t.enabled and "disabled")
      or "queue_not_empty"
    return helpers.table_to_json({ result = "rejected", tech = name, reason = reason })
  end,

  --- Prototyp-Export erneut anstossen (z. B. nach einem Mod-Wechsel, den der
  --- Server mitbekommen hat, das Spiel aber nicht neu gestartet wurde).
  export_prototypes = function()
    protoexport.start()
    return helpers.table_to_json({ started = true })
  end,
})

-- ------------------------------------------------------------- Konsole

commands.add_command("fdash-status",
  { "", "Show fdash-exporter status" },
  function(event)
    local player = event.player_index and game.get_player(event.player_index)
    local text = remote.call("fdash", "status")
    if player then
      player.print(text)
    else
      -- Kein player_index heisst Server-Konsole oder RCON. rcon.print schreibt
      -- an den aufrufenden RCON-Client und ist ausserhalb einer RCON-Sitzung
      -- wirkungslos, deshalb zusaetzlich ins Log — sonst bliebe der Aufruf
      -- ueber RCON ohne jede Ausgabe.
      rcon.print(text)
      log(text)
    end
  end)
