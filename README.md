# LidGuard

A Windows tray utility with a customizable privacy cover and a separate Windows lock command.

## Choose the right action

| Action | What it does | Power behavior |
| --- | --- | --- |
| **Privacy cover** (button, tray, or configured shortcut) | Shows a password-protected decorative cover with wallpapers and weather. This is an ordinary desktop application, not a Windows authentication boundary. | Temporarily sets the current plan's AC/DC lid action to do nothing and AC sleep timeout to zero; requests that the system stay awake. |
| **Lock Windows** (button or tray) | Requests the Windows lock screen. Sign back in with your Windows credentials. | Leaves Windows power settings unchanged. It does not enable closed-lid work mode. |

Use Windows locking when you need to protect access to your session. The privacy cover's keyboard blocking, foreground handling and crash watchdog are convenience safeguards, not a replacement for Windows security. Do not rely on the cover against someone who can control processes or modify your user files.

## Build and run

Requires 64-bit Windows with the .NET Framework 4.x compiler and desktop components. Video playback uses Windows Media Foundation; availability depends on Windows edition and installed codecs.

```powershell
# Compile only; does not install or launch LidGuard.
.\build.ps1 -NoInstall

# Run the compiled app manually.
.\bin\LidGuard.exe

# Build, install for the current user, create shortcuts, enable startup, and launch.
.\build.ps1
```

Exit any running LidGuard instance before installing an update. The build checks compilation failures. Installation first runs recovery and aborts if recovery is incomplete.

Set a password for the privacy cover. The default shortcut is Ctrl+L, which can conflict with other apps; change it or turn it off in settings. Use the **Lock Windows** button for Windows authentication without setting a separate cover password.

## Recovery and uninstall

Unlock the privacy cover and select **Exit** in the tray first. Then run:

```powershell
.\uninstall.ps1          # keeps preferences and password hashes
.\uninstall.ps1 -Purge   # also removes saved user data after recovery succeeds
```

Uninstall refuses to kill running instances, including the watchdog. It runs the installed executable with `--recover` and requires a successful exit before removing files. Recovery exit codes: 0 = complete, 1 = recovery state remains, 2 = another instance exists.

After a crash, relaunch LidGuard to retry recovery, or run a newly built executable without opening the UI:

```powershell
$p = Start-Process .\bin\LidGuard.exe -ArgumentList '--recover' -PassThru -Wait
$p.ExitCode
```

Recovery data is in `%APPDATA%\LidGuard` (or `LIDGUARD_HOME`, if set). Do not delete `restore.txt`, `guard.txt`, or `dnd.flag` while recovery is incomplete. Failed power restoration retains its journal for another attempt. A deleted power plan or denied permission may require manual intervention.

New `restore.txt` files contain the original plan GUID followed by AC lid action, battery lid action, and AC sleep timeout in seconds. Restoration targets that original plan and does not switch away from a different plan you selected.

Older three-value journals have no plan GUID. Automatic recovery preserves these without guessing which plan to modify. In Windows Power Options, select the plan used during the old cover session and restore its lid and sleep settings using those saved values (lid: 0 = do nothing, 1 = sleep, 2 = hibernate, 3 = shut down). Only after verifying the settings should you remove that legacy `restore.txt` and retry recovery/uninstall. Keep a copy if unsure.

## Checks

```powershell
.\tests\run.ps1
```

The tests use a fake power backend and never change machine power settings. They cover plan switching, original values, failed writes, corrupt journals, and legacy recovery files. GitHub Actions builds and runs these tests on Windows.

Before a release, manually verify on a test machine: lid close/open on AC and battery, multi-monitor changes, cover unlock, process crash/watchdog recovery, failed restoration, installation/uninstall, and Windows lock/sign-in. Automated checks do not exercise those desktop interactions.

## Data and limitations

Passwords and recovery keys are stored as salted PBKDF2 hashes locally. Weather and city search use Open-Meteo. Custom wallpaper videos remain local. The app temporarily changes accessibility shortcut behavior and hides taskbars during the cover; recovery restores saved state.

Hardware, firmware, Windows policy, and power-plan changes can affect closed-lid behavior. Keep ventilation clear while the machine runs. Windows secure locking uses normal Windows power behavior.

## License

No open-source license has been selected. Repository visibility alone does not grant redistribution or reuse permissions; contact the owner for permission.
