fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Hunger and thirst in C#: server-side decay, item effects via events, HUD'
version '0.1.0'

dependency 'oxmysql'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'needs.client.net.dll'
server_script 'needs.server.net.dll'
