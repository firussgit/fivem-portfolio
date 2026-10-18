-- Last known position per player, keyed by Rockstar license. Persisted via resource KVP.
local positions = {} -- [src] = { x, y, z, h }

local function kvpKey(src)
    local license = GetPlayerIdentifierByType(src, 'license')
    return license and ('pos:' .. license) or nil
end

local function persist(src)
    local key, pos = kvpKey(src), positions[src]
    if key and pos then SetResourceKvp(key, json.encode(pos)) end
end

-- The client reports; the server only accepts well-formed numbers.
RegisterNetEvent('spawncore:savePos', function(x, y, z, h)
    if type(x) ~= 'number' or type(y) ~= 'number' or type(z) ~= 'number' then return end
    positions[source] = { x = x, y = y, z = z, h = type(h) == 'number' and h or 0.0 }
end)

-- Always answers (false = nothing saved) so the client knows when it may start reporting.
RegisterNetEvent('spawncore:requestPos', function()
    local src = source
    local key = kvpKey(src)
    local raw = key and GetResourceKvpString(key)
    TriggerClientEvent('spawncore:applyPos', src, raw and json.decode(raw) or false)
end)

-- Used by /relog: write the latest reported position to disk before the client disconnects.
RegisterNetEvent('spawncore:flush', function()
    persist(source)
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
