fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Server-timed time trials in C#: fixed routes, a provided car, checkpoint polling and a top-10 leaderboard'
version '0.1.0'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

dependencies {
    'oxmysql',
}

client_script 'races.client.net.dll'
server_script 'races.server.net.dll'
