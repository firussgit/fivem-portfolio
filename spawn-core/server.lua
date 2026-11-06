-- Last known position per player, keyed by Rockstar license. Persisted via resource KVP.
-- The client reports often, but the server keeps its own copy when the two disagree (OneSync),
-- so a modified client cannot save an arbitrary teleport point for its next spawn.
local positions = {} -- [src] = { x, y, z, h }

local MAP_MIN_X, MAP_MAX_X = -4200.0, 4700.0
local MAP_MIN_Y, MAP_MAX_Y = -5200.0, 8200.0
local MAP_MIN_Z, MAP_MAX_Z = -200.0, 2000.0

local function validPos(x, y, z)
    return type(x) == 'number' and type(y) == 'number' and type(z) == 'number'
        and x == x and y == y and z == z -- NaN fails this comparison
        and x >= MAP_MIN_X and x <= MAP_MAX_X
        and y >= MAP_MIN_Y and y <= MAP_MAX_Y
        and z >= MAP_MIN_Z and z <= MAP_MAX_Z
end

local function heading(h)
    h = tonumber(h) or 0.0
    if h ~= h then return 0.0 end
    h = h % 360.0
    if h < 0 then h = h + 360.0 end
    return h
end

local function kvpKey(src)
    local license = GetPlayerIdentifierByType(src, 'license')
    return license and ('pos:' .. license) or nil
end

local function persist(src)
    local key, pos = kvpKey(src), positions[src]
    if key and pos then SetResourceKvp(key, json.encode(pos)) end
end

-- The client reports; the server only accepts well-formed numbers inside the map bounds.
RegisterNetEvent('spawncore:savePos', function(x, y, z, h)
    if not validPos(x, y, z) then return end

    local ped = GetPlayerPed(source)
    if ped and ped ~= 0 then
        local real = GetEntityCoords(ped)
        if real then
            -- If the client disagrees with the server's own position, the server wins.
            if math.abs(real.x - x) > 25.0 or math.abs(real.y - y) > 25.0 or math.abs(real.z - z) > 50.0 then
                x, y, z = real.x, real.y, real.z
            end
        end
    end

    positions[source] = { x = x, y = y, z = z, h = heading(h) }
end)

-- Always answers (false = nothing saved) so the client knows when it may start reporting.
RegisterNetEvent('spawncore:requestPos', function()
    local src = source
    local key = kvpKey(src)
    local raw = key and GetResourceKvpString(key)
    local pos = false

    if raw then
        local ok, decoded = pcall(json.decode, raw)
        if ok and type(decoded) == 'table' and validPos(decoded.x, decoded.y, decoded.z) then
            pos = { x = decoded.x, y = decoded.y, z = decoded.z, h = heading(decoded.h) }
        end
    end

    TriggerClientEvent('spawncore:applyPos', src, pos)
end)

-- /relog: persist the position we just received, then drop the player to the FiveM menu.
RegisterNetEvent('spawncore:relog', function()
    local src = source
    persist(src)
    DropPlayer(src, 'Relogging: reconnect from the FiveM menu.')
end)

-- /car is a dev command: check the ACE on the server before telling the client to spawn anything.
local function validModel(name)
    return type(name) == 'string' and #name > 0 and #name <= 32 and name:match('^[%w_%-]+$') ~= nil
end

RegisterNetEvent('spawncore:requestCar', function(name)
    local src = source
    if not IsPlayerAceAllowed(src, 'spawn.admin') then
        TriggerClientEvent('chat:addMessage', src, { args = { 'Car', 'You do not have permission to use /car.' } })
        return
    end
    if not validModel(name) then
        TriggerClientEvent('chat:addMessage', src, { args = { 'Car', 'Invalid vehicle model.' } })
        return
    end
    TriggerClientEvent('spawncore:spawnCar', src, name)
end)

AddEventHandler('playerDropped', function()
    persist(source)
    positions[source] = nil
end)

-- Periodic flush so a server crash loses at most a minute of movement.
CreateThread(function()
    while true do
        Wait(60000)
        for src in pairs(positions) do persist(src) end
    end
end)
