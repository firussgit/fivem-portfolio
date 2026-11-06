local ready = false -- only report positions after the saved one has been applied

AddEventHandler('playerSpawned', function()
    ready = false
    TriggerServerEvent('spawncore:requestPos')
end)

-- Solid ground used when a saved position turns out to be a hole in the map.
local SAFE = vector3(215.0, -810.0, 30.7) -- Legion Square

local function placePed(ped, pos, heading)
    RequestCollisionAtCoord(pos.x, pos.y, pos.z)
    SetEntityCoordsNoOffset(ped, pos.x, pos.y, pos.z, false, false, false)
    if heading then SetEntityHeading(ped, heading) end
end

RegisterNetEvent('spawncore:applyPos', function(pos)
    -- Below sea level is never a walkable position; ignore it and keep the normal spawn.
    if pos and pos.z > 0.0 then
        local ped = PlayerPedId()
        FreezeEntityPosition(ped, true)
        placePed(ped, pos, pos.h or 0.0)

        -- Wait (max ~3s) for the ground to stream in so we don't fall through the map.
        local waited = 0
        while not HasCollisionLoadedAroundEntity(ped) and waited < 3000 do
            Wait(100)
            waited = waited + 100
        end

        -- A saved position can be inside a building or over a map hole (the PDM showroom
        -- is one). Unfreeze and watch: if the ped keeps falling, move it to solid ground.
        FreezeEntityPosition(ped, false)
        local z0 = GetEntityCoords(ped).z
        local waited2 = 0
        while waited2 < 1500 do
            Wait(100)
            waited2 = waited2 + 100

            local c = GetEntityCoords(ped)
            local veh = GetVehiclePedIsIn(ped, false)
            local inAir = IsPedFalling(ped) or (veh ~= 0 and IsEntityInAir(veh))
            if not inAir then break end

            if c.z < z0 - 5.0 then
                FreezeEntityPosition(ped, true)
                placePed(ped, SAFE, 0.0)
                Wait(300)
                FreezeEntityPosition(ped, false)
                TriggerEvent('chat:addMessage', { args = {
                    'Spawn', 'Your saved position was not on solid ground; moved to Legion Square. Use /stuck if it happens again.'
                } })
                break
            end
        end
    end
    ready = true
end)

-- /stuck: escape hatch for a hole in the map. Fixed destination, so it cannot be used
-- to teleport anywhere useful.
RegisterCommand('stuck', function()
    local ped = PlayerPedId()
    if IsEntityDead(ped) then return end

    FreezeEntityPosition(ped, true)
    RequestCollisionAtCoord(SAFE.x, SAFE.y, SAFE.z)

    local veh = GetVehiclePedIsIn(ped, false)
    if veh ~= 0 then SetPedCoordsKeepVehicle(ped, SAFE.x, SAFE.y, SAFE.z)
    else SetEntityCoordsNoOffset(ped, SAFE.x, SAFE.y, SAFE.z, false, false, false) end

    SetEntityVelocity(ped, 0.0, 0.0, 0.0)
    if veh ~= 0 then SetEntityVelocity(veh, 0.0, 0.0, 0.0) end

    Wait(400)
    FreezeEntityPosition(ped, false)
    TriggerEvent('chat:addMessage', { args = { 'Spawn', 'Moved to Legion Square.' } })
end, false)

-- A saved position is used as the next spawn, so only persist a position that is on
-- the ground and above sea level. Mid-fall positions (a hole in the map, a parachute
-- jump) and underwater dives would otherwise respawn the player in the void.
local function canSave(ped, c)
    if IsEntityDead(ped) or c.z < 0.0 or IsPedFalling(ped) then return false end
    local veh = GetVehiclePedIsIn(ped, false)
    if veh ~= 0 and IsEntityInAir(veh) then return false end
    return true
end

CreateThread(function()
    while true do
        Wait(5000)
        local ped = PlayerPedId()
        if ready and not IsEntityDead(ped) then
            local c = GetEntityCoords(ped)
            if canSave(ped, c) then
                TriggerServerEvent('spawncore:savePos', c.x, c.y, c.z, GetEntityHeading(ped))
            end
        end
    end
end)

-- /relog: save position, then ask the server to drop us back to the FiveM menu (no GTA relaunch).
-- Scripts can't run "connect" or "disconnect" (both disabled in production mode), so the
-- server does the drop, and reconnecting is one click on the server in the FiveM menu.
RegisterCommand('relog', function()
    local ped = PlayerPedId()
    local c = GetEntityCoords(ped)
    TriggerServerEvent('spawncore:savePos', c.x, c.y, c.z, GetEntityHeading(ped))
    TriggerServerEvent('spawncore:relog') -- events from one client arrive in order
end, false)

-- /car [model]  e.g. /car sultan
-- The server checks spawn.admin first, then asks this client to spawn the vehicle.
local lastCar

local function spawnCar(name)
    local model = GetHashKey(name)

    if not IsModelInCdimage(model) or not IsModelAVehicle(model) then
        TriggerEvent('chat:addMessage', { args = { 'Car', ('"%s" is not a valid vehicle model.'):format(name) } })
        return
    end

    RequestModel(model)
    local tries = 0
    while not HasModelLoaded(model) and tries < 100 do
        Wait(50)
        tries = tries + 1
    end
    if not HasModelLoaded(model) then return end

    if lastCar and DoesEntityExist(lastCar) then DeleteEntity(lastCar) end

    local ped = PlayerPedId()
    local c = GetEntityCoords(ped)
    lastCar = CreateVehicle(model, c.x, c.y, c.z, GetEntityHeading(ped), true, false)
    SetPedIntoVehicle(ped, lastCar, -1)
    SetModelAsNoLongerNeeded(model)
end

RegisterNetEvent('spawncore:spawnCar', function(name)
    if type(name) ~= 'string' then return end
    spawnCar(name)
end)

RegisterCommand('car', function(_, args)
    TriggerServerEvent('spawncore:requestCar', args[1] or 'adder')
end, false)
