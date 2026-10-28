$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force "$PSScriptRoot\..\bin" | Out-Null
& $csc /nologo /target:exe "/out:$PSScriptRoot\..\bin\RecoveryTests.exe" "$PSScriptRoot\..\PowerRecovery.cs" "$PSScriptRoot\RecoveryTests.cs"
if ($LASTEXITCODE) { throw 'Test compilation failed' }
& "$PSScriptRoot\..\bin\RecoveryTests.exe"
if ($LASTEXITCODE) { throw 'Recovery tests failed' }
