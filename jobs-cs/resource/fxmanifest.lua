fx_version 'cerulean'
game 'gta5'

author 'firuss'
description 'Jobs, grades, duty and paychecks in C#; pays into bank-cs through a server export'
version '0.1.0'

dependencies {
    'oxmysql',
    'bank-cs',
}

server_script 'jobs.server.net.dll'
