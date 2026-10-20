# Builds the jsync engine the desktop app runs, from ../filesharer, into engine\:
#   jsyncnode.exe   the client node (login, Drive files, device-to-device transfers) behind a local HTTP API
#   jsync.exe       `jsync mount drive` (the Cloud Filter API folder)
#   jsyncd.exe      the hub / Windows service
#
#   .\build-engine.ps1
param([string]$Source = (Join-Path $PSScriptRoot '..\filesharer'))
$ErrorActionPreference = 'Stop'
$src = (Resolve-Path $Source).Path
$out = Join-Path $PSScriptRoot 'engine'
New-Item -ItemType Directory -Force $out | Out-Null
$env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
foreach ($c in 'jsyncnode', 'jsync', 'jsyncd') {
    go build -C $src -trimpath -ldflags '-s -w' -o (Join-Path $out "$c.exe") "./cmd/$c"
    if ($LASTEXITCODE -ne 0) { throw "go build failed for $c" }
}
Get-ChildItem $out -Filter *.exe | ForEach-Object { '{0,-14} {1,6:N1} MB' -f $_.Name, ($_.Length / 1MB) }
