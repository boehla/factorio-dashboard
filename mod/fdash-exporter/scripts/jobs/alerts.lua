-- Factorios eigene Alerts, plus ein eigener Zaehler fuer zerstoerte Gebaeude.
--
-- Der eigene Zaehler ist kein Luxus: player.get_alerts haengt an einem
-- LuaPlayer, und auf einem Headless-Server ohne verbundenen Spieler ist die
-- Liste schlicht leer. Genau dort laeuft dieser Mod aber ueblicherweise.
--
-- Billig und ohne Sweep: ein paar Spieler, eine feste Tabelle.

local job = { name = "alerts", per_surface = false, interval = 300 }

--- Alerts, die keine sind: der Spieler hat das so gebaut.
local IGNORED = {
  turret_fire = true,
  custom = true
}

local alert_names = nil
local function alert_name(v)
  if not alert_names then
    alert_names = {}
    for k, val in pairs(defines.alert_type) do alert_names[val] = k end
  end
  return alert_names[v] or ("unknown_" .. tostring(v))
end

function job.run(_st, _si, _budget)
  local by_type = {}
  local samples = {}

  -- ------------------------------------------------- 1 Alerts der Spieler
  for _, player in pairs(game.connected_players) do
    local ok, alerts = pcall(function() return player.get_alerts{} end)
    if ok and alerts then
      for _, per_type in pairs(alerts) do
        for atype, list in pairs(per_type) do
          local name = alert_name(atype)
          if not IGNORED[name] then
            by_type[name] = (by_type[name] or 0) + #list
            local s = samples[name]
            if not s then s = {}; samples[name] = s end
            for i = 1, #list do
              if #s >= 3 then break end
              local target = list[i].target
              if target and target.valid then
                s[#s + 1] = {
                  name = target.name,
                  x = math.floor(target.position.x),
                  y = math.floor(target.position.y)
                }
              end
            end
          end
        end
      end
    end
  end

  -- --------------------------------------------- 2 Eigene Zerstoerungszahl
  local d = storage.deaths or { count = 0, by_name = {}, recent = {} }
  local destroyed = {}
  for name, count in pairs(d.by_name) do destroyed[#destroyed + 1] = { name = name, count = count } end
  table.sort(destroyed, function(a, b)
    if a.count ~= b.count then return a.count > b.count end
    return a.name < b.name
  end)
  local top = {}
  for i = 1, math.min(8, #destroyed) do top[i] = destroyed[i] end

  local recent = {}
  for _, r in pairs(d.recent) do recent[#recent + 1] = r end
  table.sort(recent, function(a, b) return a.tick > b.tick end)

  return 1, {
    -- Leer heisst hier nicht "alles gut", sondern moeglicherweise "niemand
    -- verbunden". Deshalb die Spielerzahl mitschicken.
    players_online = #game.connected_players,
    by_type = by_type,
    samples = samples,
    destroyed_total = d.count,
    destroyed_by_name = top,
    destroyed_recent = recent,
    tick = game.tick
  }
end

return job
