# Builds LidGuard and installs it for the current user.
#   .\build.ps1             build, install, start
#   .\build.ps1 -NoInstall  build only (output in .\bin)
param([switch]$NoInstall)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

& $csc /nologo /target:exe "/out:$bin\IconGen.exe" /r:System.Drawing.dll "$PSScriptRoot\IconGen.cs"
if ($LASTEXITCODE) { throw 'IconGen failed to compile' }
& "$bin\IconGen.exe" "$bin\LidGuard.ico" "$bin\icon-preview.png"
if ($LASTEXITCODE) { throw 'IconGen failed' }

& $csc /nologo /target:winexe /codepage:65001 "/out:$bin\LidGuard.exe" "/win32icon:$bin\LidGuard.ico" "/resource:$bin\LidGuard.ico,LidGuard.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$PSScriptRoot\AgentWatch.cs" "$PSScriptRoot\WorkPolicy.cs" "$PSScriptRoot\SecureWorkSession.cs" "$PSScriptRoot\PowerRecovery.cs" "$PSScriptRoot\Program.cs" "$PSScriptRoot\Ui.cs" "$PSScriptRoot\LockScreen.cs" "$PSScriptRoot\VideoWallpaper.cs"
if ($LASTEXITCODE) { throw 'LidGuard failed to compile' }
Write-Host "Built $bin\LidGuard.exe"
if ($NoInstall) { return }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\LidGuard'
$exe = Join-Path $dest 'LidGuard.exe'
& "$PSScriptRoot\prepare-removal.ps1" -RecoveryExe "$bin\LidGuard.exe"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$bin\LidGuard.exe" $exe -Force

$shell = New-Object -ComObject WScript.Shell
$links = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'LidGuard.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LidGuard.lnk')
)
foreach ($lnk in $links) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dest
    $s.Description = 'Lock the screen and keep this PC running'
    $s.Save()
}
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name LidGuard -Value "`"$exe`" --background"

Start-Process $exe
Write-Host "Installed to $dest - starts with Windows, Start menu + desktop shortcuts created."




