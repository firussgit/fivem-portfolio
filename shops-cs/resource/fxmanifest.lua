fx_version 'cerulean'
game 'gta5'

author 'you'
description 'Shops in C#: server-validated purchases across bank-cs and inventory-cs'
version '0.1.0'

dependencies {
    'oxmysql',
    'bank-cs',
    'inventory-cs',
}

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'shops.client.net.dll'
server_script 'shops.server.net.dll'
