fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Jobs, grades, duty, paychecks, courier/taxi work and police/EMS/mechanic field actions in C#; pays through bank-cs and closes dispatch calls'
version '0.5.0'

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

client_script 'jobs.client.net.dll'
server_script 'jobs.server.net.dll'
