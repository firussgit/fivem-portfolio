fx_version 'cerulean'
game 'gta5'

author 'you'
description 'Vehicle fuel in C#: server-tracked levels, paid refuelling at stations, persisted on owned vehicles'
version '0.1.0'

dependencies {
    'oxmysql',
    'bank-cs',
}

client_script 'fuel.client.net.dll'
server_script 'fuel.server.net.dll'
