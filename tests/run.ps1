$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force "$PSScriptRoot\..\bin" | Out-Null
& $csc /nologo /target:exe "/out:$PSScriptRoot\..\bin\RecoveryTests.exe" "$PSScriptRoot\..\PowerRecovery.cs" "$PSScriptRoot\RecoveryTests.cs"
if ($LASTEXITCODE) { throw 'Test compilation failed' }
& "$PSScriptRoot\..\bin\RecoveryTests.exe"
if ($LASTEXITCODE) { throw 'Recovery tests failed' }

& $csc /nologo /target:exe "/out:$PSScriptRoot\..\bin\WorkPolicyTests.exe" "$PSScriptRoot\..\WorkPolicy.cs" "$PSScriptRoot\WorkPolicyTests.cs"
if ($LASTEXITCODE) { throw 'Work policy test compilation failed' }
& "$PSScriptRoot\..\bin\WorkPolicyTests.exe"
if ($LASTEXITCODE) { throw 'Work policy tests failed' }

& $csc /nologo /target:exe /main:SecureSessionTests "/out:$PSScriptRoot\..\bin\SecureSessionTests.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$PSScriptRoot\..\WorkPolicy.cs" "$PSScriptRoot\..\SecureWorkSession.cs" "$PSScriptRoot\..\PowerRecovery.cs" "$PSScriptRoot\..\Program.cs" "$PSScriptRoot\..\Ui.cs" "$PSScriptRoot\..\LockScreen.cs" "$PSScriptRoot\..\VideoWallpaper.cs" "$PSScriptRoot\SecureSessionTests.cs"
if ($LASTEXITCODE) { throw 'Session test compilation failed' }
& "$PSScriptRoot\..\bin\SecureSessionTests.exe"
if ($LASTEXITCODE) { throw 'Secure session tests failed' }
