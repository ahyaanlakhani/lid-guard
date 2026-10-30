using System;

// Pure policy: shared by the desktop session and regression tests.
internal sealed class WorkPolicy
{
    internal readonly TimeSpan Duration;
    internal readonly bool PluggedInOnly;
    internal readonly int MinimumBattery;
    internal readonly bool TurnOffDisplays;
    // Privacy mode locks Windows when the session starts. Casual mode keeps the PC awake without a lock.
    internal readonly bool LockWindows;
    // 0 = run for the full timer. Otherwise end after this many minutes with no agent working.
    internal readonly int AgentQuietMinutes;

    internal WorkPolicy(int minutes, bool pluggedInOnly, int minimumBattery, bool turnOffDisplays = false, bool lockWindows = true, int agentQuietMinutes = 0)
    {
        if (minutes < 1 || minutes > 1440) throw new ArgumentOutOfRangeException("minutes");
        if (minimumBattery < 10 || minimumBattery > 90) throw new ArgumentOutOfRangeException("minimumBattery");
        if (agentQuietMinutes < 0 || agentQuietMinutes > 240) throw new ArgumentOutOfRangeException("agentQuietMinutes");
        // displays can only be turned off after a lock, so casual sessions cannot ask for it
        if (turnOffDisplays && !lockWindows) throw new ArgumentException("Turning off displays needs a Windows lock.", "turnOffDisplays");
        Duration = TimeSpan.FromMinutes(minutes);
        PluggedInOnly = pluggedInOnly;
        MinimumBattery = minimumBattery;
        TurnOffDisplays = turnOffDisplays;
        LockWindows = lockWindows;
        AgentQuietMinutes = agentQuietMinutes;
    }

    internal string StopReason(TimeSpan elapsed, bool? pluggedIn, int? batteryPercent)
    {
        if (elapsed >= Duration) return "The session timer finished.";
        if (!pluggedIn.HasValue) return "Power source could not be determined.";
        if (pluggedIn.Value) return null;
        if (PluggedInOnly) return "The charger was disconnected.";
        if (!batteryPercent.HasValue) return "Battery level could not be determined.";
        if (batteryPercent.Value <= MinimumBattery) return "Battery reached the safety limit.";
        return null;
    }

    // quiet: how long no agent has been busy
    internal string AgentStopReason(TimeSpan quiet)
    {
        if (AgentQuietMinutes == 0) return null;
        return quiet >= TimeSpan.FromMinutes(AgentQuietMinutes) ? "No agent has been working for " + AgentQuietMinutes + " minutes." : null;
    }
}
