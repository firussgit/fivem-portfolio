fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Phone in C#: number per player, contacts, text messages, phone banking and 911/912 emergency calls over MySQL, NUI interface'
version '0.2.0'

dependencies {
    'oxmysql',
    'bank-cs',
}

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'phone.client.net.dll'
server_script 'phone.server.net.dll'
