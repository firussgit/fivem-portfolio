-- Server decides what's true; the client only asks.
RegisterNetEvent('hello:requestGreeting', function()
    local src = source
    print(('[hello] request from %s (%s)'):format(GetPlayerName(src), src))
    TriggerClientEvent('hello:greeting', src, ('Welcome, %s!'):format(GetPlayerName(src)))
end)
