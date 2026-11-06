fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Player vehicle dealership in C#: server-priced showroom, cash/bank purchases, free test drives and condition-based trade-ins'
version '0.1.0'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

dependencies {
    'oxmysql',
    'bank-cs',
    'garage-cs',
}

client_script 'dealer.client.net.dll'
server_script 'dealer.server.net.dll'
