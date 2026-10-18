fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Vehicle customisation in C#: server-priced mods saved on the owned vehicle, live preview UI'
version '0.1.0'

dependencies {
    'oxmysql',
    'bank-cs',
    'garage-cs',
}

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'customs.client.net.dll'
server_script 'customs.server.net.dll'
