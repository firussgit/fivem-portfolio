fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Slot/weight inventory in C#: server-authoritative, MySQL-backed, drag and drop NUI; lockpick drives garage hotwiring'
version '0.2.0'

dependency 'oxmysql'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'inventory.client.net.dll'
server_script 'inventory.server.net.dll'
