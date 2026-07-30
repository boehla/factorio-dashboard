-- Getaggte Constant Combinators (Beschreibung beginnt mit "FDASH:").
--
-- Damit lassen sich beliebige Schaltkreis-Signale ins Dashboard heben, ohne
-- dass der Mod etwas ueber ihre Bedeutung wissen muss.

local sweep = require("scripts.sweep")

local job = { name = "circuits", per_surface = true, interval = 300 }

local TYPES = { "constant-combinator" }
local PREFIX = "FDASH:"

function job.run(st, si, budget)
  if not st.tags then
    st.ti, st.slot = 1, 1
    st.tags = {}
  end
  st.extra = 0

  local tags = st.tags
  local surface_name = (game.surfaces[si] and game.surfaces[si].name) or tostring(si)

  local spent, done = sweep.registry(st, si, TYPES, budget, function(e)
    local desc = e.combinator_description
    if not desc or string.sub(desc, 1, 6) ~= PREFIX then return end

    -- Nur die getaggten Combinators kosten wirklich etwas — der Rest ist ein
    -- Stringvergleich.
    st.extra = st.extra + 3
    local signals = {}
    local cb = e.get_control_behavior()
    if cb and cb.sections then
      for _, sec in pairs(cb.sections) do
        for _, filt in pairs(sec.filters or {}) do
          if filt and filt.value then signals[filt.value.name] = filt.min end
        end
      end
    end
    tags[#tags + 1] = {
      label = string.sub(desc, 7),
      surface = surface_name,
      signals = signals
    }
  end)

  local cost = spent + st.extra
  if not done then return cost, nil end

  st.tags = nil
  return cost, { surface = surface_name, tags = tags }
end

return job
