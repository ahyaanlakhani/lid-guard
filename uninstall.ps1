# Removes LidGuard. Your password, key and settings in %APPDATA%\LidGuard are kept unless you pass -Purge.
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\prepare-removal.ps1" -RecoveryExe (Join-Path $env:LOCALAPPDATA 'Programs\LidGuard\LidGuard.exe')
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name LidGuard -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'LidGuard.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LidGuard.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\LidGuard') -Recurse -Force -ErrorAction SilentlyContinue
if ($Purge) { Remove-Item (Join-Path $env:APPDATA 'LidGuard') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'LidGuard removed.'
