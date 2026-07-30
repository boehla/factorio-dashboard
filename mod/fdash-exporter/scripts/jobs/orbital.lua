-- Orbital Requests einer Plattform-Surface.
--
-- Die API ist in Space Age noch in Bewegung, deshalb durchgehend defensiv:
-- fehlt irgendetwas, kommt eine leere Liste statt eines Fehlers.

local job = { name = "orbital", per_surface = true, interval = 600 }

function job.enabled()
  return script.active_mods["space-age"] ~= nil
end

function job.run(_st, si, _budget)
  local surface = game.surfaces[si]
  local out = {}

  if surface and surface.valid and surface.platform then
    local hub = surface.platform.hub
    if hub and hub.valid then
      local ok, pt = pcall(function()
        return hub.get_logistic_point(defines.logistic_member_index.logistic_container)
      end)
      if ok and pt then
        for _, sec in pairs(pt.sections or {}) do
          for _, filt in pairs(sec.filters or {}) do
            if filt and filt.value then
              out[#out + 1] = {
                item = filt.value.name,
                requested = filt.min or 0,
                delivered = 0,
                waiting_platforms = 0
              }
            end
          end
        end
      end
    end
  end

  return 1, {
    surface = surface and surface.name or tostring(si),
    requests = out
  }
end

return job
