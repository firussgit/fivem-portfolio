RegisterCommand('hello', function()
    TriggerServerEvent('hello:requestGreeting')
end, false)

RegisterNetEvent('hello:greeting', function(msg)
    TriggerEvent('chat:addMessage', { args = { 'Server', msg } })
end)
