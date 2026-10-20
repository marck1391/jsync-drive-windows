# Builds the app and puts it, with the engine, in dist\jsync-drive-win-x64\ (and a zip next to it).
#
#   .\publish.ps1                    self-contained: runs on a PC without .NET installed
#   .\publish.ps1 -FrameworkDependent   small; needs the .NET 8 Desktop Runtime
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build-engine.ps1') | Out-Null
$out = Join-Path $root 'dist\jsync-drive-win-x64'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$sc = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish (Join-Path $root 'src\JsyncDrive') -c Release -r win-x64 --self-contained $sc -o $out `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$sc -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
New-Item -ItemType Directory -Force (Join-Path $out 'engine') | Out-Null
Copy-Item (Join-Path $root 'engine\*.exe') (Join-Path $out 'engine') -Force
Copy-Item (Join-Path $root 'install.ps1') $out -Force
$zip = Join-Path $root 'dist\jsync-drive-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum | ForEach-Object { 'folder: {0:N0} MB' -f ($_.Sum / 1MB) }
'zip:    {0:N0} MB  {1}' -f ((Get-Item $zip).Length / 1MB), $zip
