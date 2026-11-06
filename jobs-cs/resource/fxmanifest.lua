fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Jobs, grades, duty, paychecks and courier/taxi activities in C#; pays through bank-cs'
version '0.3.0'

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

client_script 'jobs.client.net.dll'
server_script 'jobs.server.net.dll'
