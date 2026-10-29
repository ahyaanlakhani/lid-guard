# LidGuard

A Windows tray utility for timed, secure closed-lid work sessions, Windows locking, and a customizable privacy cover.

## Choose the right action

| Action | What it does | Power behavior |
| --- | --- | --- |
| **Privacy cover** (button, tray, or configured shortcut) | Shows a password-protected decorative cover with wallpapers and weather. This is an ordinary desktop application, not a Windows authentication boundary. | Temporarily sets the current plan's AC/DC lid action to do nothing and AC sleep timeout to zero; requests that the system stay awake. |
| **Secure closed-lid session** (button or tray) | Locks Windows and keeps work running for a chosen time. Windows sign-in ends the session. | Temporarily overrides lid and AC sleep settings, with battery safety rules and crash recovery. |
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

## Secure closed-lid sessions

Choose **Secure closed-lid session...** in the window or tray. Select a duration from 1 minute to 24 hours (default: two hours). **Only while plugged in** is enabled by default: disconnecting the charger ends the override on the next check. Disable it to allow battery operation and choose a cutoff from 10% to 90% (default: 20%). Unknown power-source information, or unknown battery charge while on battery, prevents or ends a session.

The optional **Turn off displays after Windows locks** checkbox requests display power-off once Windows confirms the session is locked. It is off by default. Normal keyboard/mouse activity can wake the display; Windows authentication still applies. Display power behavior depends on Windows and the monitor driver. If the request fails, the secure work session continues.

**Start and lock Windows** applies the temporary power settings and requests the Windows lock screen. It requires Windows authentication; no separate cover password is needed. The window shows remaining minutes, and the window/tray offers **End session**.

Signing back in, reaching the duration, reaching the battery cutoff, unplugging in AC-only mode, changing power plans, or exiting LidGuard ends the override and restores saved power settings. The app checks once per second; busy Windows or a suspended machine can delay a check. Ending a session restores normal Windows power behavior; it does not force sleep or unlock Windows. The existing Windows policies determine when the laptop sleeps afterward.

A helper process retries restoration if LidGuard crashes. If Windows does not confirm locking within 15 seconds, the session cancels and restores settings. Startup/recovery retries any remaining power journal. A failed restore is shown as **Recovery needs attention** and blocks new sessions. Privacy cover and secure work sessions cannot run together.

Session choices apply to that session and are not saved as personal preferences. Battery rules and timing currently apply to secure work sessions; the decorative privacy cover retains its existing behavior.

## Recovery status

The window and tray show **No recovery needed**, explain that an active session will restore its settings on completion, or list the settings needing recovery: power, desktop controls, and notifications. **Retry recovery** is available in both places when saved state remains and no cover or work session is active. It retries restoration without restarting the app. Failed restoration preserves recovery files; legacy power journals still require the manual steps below. A failed launch of notification restoration also retains its flag for another retry.

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

The tests use simulated power and Windows session operations; they never change machine power settings or lock Windows. They cover plan switching, original values, failed writes, corrupt journals, legacy recovery files, timer boundaries, charger changes, battery cutoffs, missing telemetry, Windows sign-in, lock confirmation, lock/monitor startup failures, session cleanup, recovery retries, and display-off requests after lock confirmation. GitHub Actions builds and runs these tests on Windows.

Before a release, manually verify on a test machine: lid close/open on AC and battery, multi-monitor changes, cover unlock, process crash/watchdog recovery, failed restoration, installation/uninstall, Windows lock/sign-in, secure session timeout, charger disconnect, battery threshold, lock failure, and crash recovery during a secure session. Automated checks do not exercise those desktop interactions.

## Data and limitations

Passwords and recovery keys are stored as salted PBKDF2 hashes locally. Weather and city search use Open-Meteo. Custom wallpaper videos remain local. The app temporarily changes accessibility shortcut behavior and hides taskbars during the cover; recovery restores saved state.

Hardware, firmware, Windows policy, and power-plan changes can affect closed-lid behavior. Keep ventilation clear while the machine runs. Windows secure locking uses normal Windows power behavior.

## License

No open-source license has been selected. Repository visibility alone does not grant redistribution or reuse permissions; contact the owner for permission.
