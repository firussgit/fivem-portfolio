# Build the assembly and copy it (plus the manifest) into the server's resources.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $root '..\..\server-data\resources\[dev]\jobs-cs'

dotnet build "$root\Server\Server.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed, nothing was deployed.' }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\build\jobs.server.net.dll" -Destination $dest -Force
Copy-Item "$root\resource\fxmanifest.lua" -Destination $dest -Force
Write-Host "Deployed to $dest"
