# Installs jsync Drive for the current user: copies it to %LOCALAPPDATA%\Programs\jsync Drive, adds a Start menu
# shortcut and starts it (the first run asks whether this PC is a hub or a client). No administrator needed.
#
#   .\install.ps1            install / update
#   .\install.ps1 -Uninstall remove the program (your files, settings and Drive folder stay)
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\jsync Drive'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'jsync Drive.lnk'
Get-Process JsyncDrive -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force -ErrorAction SilentlyContinue
if ($Uninstall) {
    try { & cmd.exe /c 'schtasks.exe /Delete /TN "jsync Drive" /F >nul 2>&1' } catch { }
    if (Test-Path $lnk) { Remove-Item $lnk }
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    'jsync Drive removed. Its data is still in %LOCALAPPDATA%\jsync Drive.'
    return
}
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $dest -Recurse -Force -Exclude 'install.ps1'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $dest 'JsyncDrive.exe'
$s.WorkingDirectory = $dest
$s.Description = 'jsync Drive'
$s.Save()
Start-Process (Join-Path $dest 'JsyncDrive.exe')
"Installed in $dest"
