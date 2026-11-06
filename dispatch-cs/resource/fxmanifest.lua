fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Dispatch radio: 911/912 calls, on-duty police/EMS response, server-validated positions, payouts and field-action auto-resolution'
version '0.2.0'

dependencies {
    'oxmysql',
    'jobs-cs',
}

ui_page 'ui/index.html'

files {
    'ui/index.html',
    'ui/style.css',
    'ui/script.js',
}

client_script 'dispatch.client.net.dll'
server_script 'dispatch.server.net.dll'
