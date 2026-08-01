-- Runtime-Settings des Exporters.
--
-- Factorio liest Mod-Settings ausschliesslich aus dieser Datei, ausgewertet in
-- der Data-Stage. Eine settings.json gibt es nicht — lag hier frueher eine, hat
-- Factorio sie kommentarlos ignoriert und der Mod lief auf den Fallback-Werten
-- in scripts/config.lua.
--
-- Alle Settings sind "runtime-global": sie gehoeren zur Karte, gelten fuer alle
-- Spieler und lassen sich im laufenden Spiel aendern. config.lua liest sie bei
-- jedem Zugriff frisch, eine Aenderung wirkt also sofort.

data:extend({
  {
    type = "bool-setting",
    name = "fdash-enabled",
    setting_type = "runtime-global",
    default_value = true,
    order = "a-a"
  },
  -- Die zentrale Stellschraube gegen UPS-Verlust: Obergrenze fuer die Anzahl
  -- Entities, die ein Tick ueber alle Collector hinweg anfassen darf.
  {
    type = "int-setting",
    name = "fdash-entity-budget",
    setting_type = "runtime-global",
    default_value = 400,
    minimum_value = 25,
    maximum_value = 50000,
    order = "b-a"
  },
  {
    type = "int-setting",
    name = "fdash-chunk-budget",
    setting_type = "runtime-global",
    default_value = 4,
    minimum_value = 1,
    maximum_value = 512,
    order = "b-b"
  },
  {
    type = "int-setting",
    name = "fdash-write-interval",
    setting_type = "runtime-global",
    default_value = 300,
    minimum_value = 60,
    maximum_value = 18000,
    order = "c-a"
  },
  {
    type = "bool-setting",
    name = "fdash-file-output",
    setting_type = "runtime-global",
    default_value = true,
    order = "c-b"
  },
  {
    type = "bool-setting",
    name = "fdash-resource-scan",
    setting_type = "runtime-global",
    default_value = true,
    order = "d-a"
  },
  {
    type = "int-setting",
    name = "fdash-covered-refresh-minutes",
    setting_type = "runtime-global",
    default_value = 10,
    minimum_value = 1,
    maximum_value = 240,
    order = "d-b"
  },
  -- Fluidtanks: billig genug fuer den Dauerbetrieb. Auf Pyanodons ist die
  -- Gasbilanz die halbe Diagnose, deshalb per Default an.
  {
    type = "bool-setting",
    name = "fdash-fluid-scan",
    setting_type = "runtime-global",
    default_value = true,
    order = "e-a"
  },
  -- Kisteninhalte: ein get_contents PRO Kiste, und davon hat eine gewachsene
  -- Basis zehntausende. Der Sweep ist budgetiert, ein voller Durchlauf dauert
  -- damit aber Minuten — deshalb per Default aus.
  {
    type = "bool-setting",
    name = "fdash-container-scan",
    setting_type = "runtime-global",
    default_value = false,
    order = "e-b"
  },
  -- Nester zaehlen heisst die ganze Karte abgehen. Evolution und
  -- Pollution-Bilanz gibt es auch ohne, deshalb per Default aus.
  {
    type = "bool-setting",
    name = "fdash-threat-scan",
    setting_type = "runtime-global",
    default_value = false,
    order = "e-c"
  }
})
