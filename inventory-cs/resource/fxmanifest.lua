fx_version 'cerulean'
game 'gta5'

author 'you'
description 'Slot/weight inventory in C#: server-authoritative, MySQL-backed, drag and drop NUI'
version '0.1.0'

dependency 'oxmysql'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'inventory.client.net.dll'
server_script 'inventory.server.net.dll'
