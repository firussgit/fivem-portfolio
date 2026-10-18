# Build both assemblies and copy them (plus the manifest) into the server's resources.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $root '..\..\server-data\resources\[dev]\fuel-cs'

dotnet build "$root\Server\Server.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed, nothing was deployed.' }
dotnet build "$root\Client\Client.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed, nothing was deployed.' }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\build\fuel.server.net.dll", "$root\build\fuel.client.net.dll" -Destination $dest -Force
Copy-Item "$root\resource\fxmanifest.lua" -Destination $dest -Force
Write-Host "Deployed to $dest"
