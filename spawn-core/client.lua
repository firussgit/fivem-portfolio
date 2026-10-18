local ready = false -- only report positions after the saved one has been applied

AddEventHandler('playerSpawned', function()
    ready = false
    TriggerServerEvent('spawncore:requestPos')
end)

RegisterNetEvent('spawncore:applyPos', function(pos)
    if pos then
        local ped = PlayerPedId()
        RequestCollisionAtCoord(pos.x, pos.y, pos.z)
        FreezeEntityPosition(ped, true)
        SetEntityCoordsNoOffset(ped, pos.x, pos.y, pos.z, false, false, false)
        SetEntityHeading(ped, pos.h or 0.0)

        -- Wait (max ~3s) for the ground to stream in so we don't fall through the map.
        local waited = 0
        while not HasCollisionLoadedAroundEntity(ped) and waited < 3000 do
            Wait(100)
            waited = waited + 100
        end
        FreezeEntityPosition(ped, false)
    end
    ready = true
end)

CreateThread(function()
    while true do
        Wait(5000)
        local ped = PlayerPedId()
        if ready and not IsEntityDead(ped) then
            local c = GetEntityCoords(ped)
            TriggerServerEvent('spawncore:savePos', c.x, c.y, c.z, GetEntityHeading(ped))
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
local lastCar

RegisterCommand('car', function(_, args)
    local name = args[1] or 'adder'
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
end, false)
