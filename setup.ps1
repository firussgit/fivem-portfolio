<#
  Builds every resource in this repository and copies it into a FiveM server's resources folder.

  Usage:   ./setup.ps1 -ResourcesPath C:\fivem\server-data\resources
  Result:  <ResourcesPath>\[portfolio]\<each resource>  (ready for `ensure <name>`)

  Needs the .NET SDK. The C# resources are built with `dotnet build`; the Lua resources are copied as they are.
  A failed build stops the script, so a half-updated install is never left behind.
#>
param(
    [Parameter(Mandatory = $true)][string]$ResourcesPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download and try again.'
}
if (-not (Test-Path -LiteralPath $ResourcesPath)) {
    throw "ResourcesPath does not exist: $ResourcesPath"
}

# The brackets make FiveM treat this folder as a category instead of a resource.
$category = Join-Path $ResourcesPath '[portfolio]'
New-Item -ItemType Directory -Force -Path $category | Out-Null

$built = @()

# C# resources: <name>/Server/Server.csproj, optionally <name>/Client/Client.csproj, and <name>/resource/
foreach ($dir in Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name) {
    $server = Join-Path $dir.FullName 'Server\Server.csproj'
    $client = Join-Path $dir.FullName 'Client\Client.csproj'
    if (-not (Test-Path -LiteralPath $server)) { continue }   # server-only resources (admin-cs, jobs-cs) have no client project

    Write-Host "== $($dir.Name)" -ForegroundColor Cyan
    $projects = @($server)
    if (Test-Path -LiteralPath $client) { $projects += $client }
    foreach ($proj in $projects) {
        dotnet build $proj -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $($dir.Name), nothing more was deployed." }
    }

    $dest = Join-Path $category $dir.Name
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $dir.FullName 'build\*.net.dll') -Destination $dest -Force
    Copy-Item -Path (Join-Path $dir.FullName 'resource\*') -Destination $dest -Recurse -Force
    $built += $dir.Name
}

# Lua resources: plain folders with an fxmanifest.lua at the top level.
foreach ($name in @('spawn-core', 'hello-lua')) {
    $src = Join-Path $root $name
    if (-not (Test-Path -LiteralPath (Join-Path $src 'fxmanifest.lua'))) { continue }
    $dest = Join-Path $category $name
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
    $built += $name
}

Write-Host ''
Write-Host "Installed $($built.Count) resources into $category" -ForegroundColor Green
Write-Host 'Next: copy server.cfg.example to your server folder as server.cfg, fill in your license key, and start the server.'
