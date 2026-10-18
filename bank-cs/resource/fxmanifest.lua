fx_version 'cerulean'
game 'gta5'

author 'you'
description 'Banking system in C#: server-authoritative accounts, ATM props, NUI interface'
version '0.2.0'

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'bank.client.net.dll'
server_script 'bank.server.net.dll'
