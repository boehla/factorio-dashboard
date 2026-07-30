-- Discovery: Version, Surfaces, Seed, Mods.
--
-- Der Save-Fingerprint auf der Serverseite baut auf Seed + Surfaces auf, damit
-- ein Save-Wechsel automatisch eine neue Zeitreihe erzeugt. Deshalb muss das
-- Feld-Layout stabil bleiben.

local scan = require("scripts.scan")

local job = { name = "meta", per_surface = false, interval = 600 }

function job.run(_st, _si, _budget)
  local surfaces = {}
  for _, s in pairs(game.surfaces) do surfaces[#surfaces + 1] = s.name end
  table.sort(surfaces)

  local mods = {}
  for name, ver in pairs(script.active_mods) do mods[#mods + 1] = name .. " " .. ver end
  table.sort(mods)

  local nauvis = game.surfaces["nauvis"]

  return 1, {
    version   = tostring(script.active_mods.base or "unknown"),
    save_name = "server",
    surfaces  = surfaces,
    tick      = game.tick,
    seed      = nauvis and nauvis.map_gen_settings.seed or 0,
    mods      = mods,
    -- Zusatzinfo des Exporters (rein additiv, der alte Vertrag bleibt gueltig).
    exporter = {
      version = script.active_mods["fdash-exporter"] or "unknown",
      scanning = scan.pending(),
      scan_progress = scan.progress()
    }
  }
end

return job
