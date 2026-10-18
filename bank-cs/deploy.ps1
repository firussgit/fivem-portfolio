# Build both assemblies and copy them (plus the manifest) into the server's resources.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $root '..\..\server-data\resources\[dev]\bank-cs'

dotnet build "$root\Server\Server.csproj" -c Release --nologo -v q
dotnet build "$root\Client\Client.csproj" -c Release --nologo -v q

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\build\bank.server.net.dll", "$root\build\bank.client.net.dll" -Destination $dest -Force
Copy-Item "$root\resource\fxmanifest.lua" -Destination $dest -Force
Copy-Item "$root\resource\ui" -Destination $dest -Recurse -Force
Write-Host "Deployed to $dest"
