param([Parameter(Mandatory=$true)][string]$RecoveryExe)
$ErrorActionPreference = 'Stop'
# Never kill an active cover or its watchdog. The user must unlock and exit first.
if (Get-Process LidGuard -ErrorAction SilentlyContinue) {
    throw 'Unlock the cover and exit LidGuard from its tray menu, then retry. No files were removed.'
}
if (!(Test-Path -LiteralPath $RecoveryExe)) { throw 'Recovery executable is missing. Build with -NoInstall first.' }
$recovery = Start-Process -FilePath $RecoveryExe -ArgumentList '--recover' -WindowStyle Hidden -PassThru
if (!$recovery.WaitForExit(30000)) { throw 'Recovery is still running. No files were removed. Retry after it finishes.' }
if ($recovery.ExitCode -ne 0) { throw 'Recovery did not finish. Settings and recovery files have been preserved; see README recovery instructions.' }
