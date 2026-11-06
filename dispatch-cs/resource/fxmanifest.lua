fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Dispatch radio: 911/912 calls from the phone, on-duty police/EMS claim and respond, server-validated positions and pay'
version '0.1.0'

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
