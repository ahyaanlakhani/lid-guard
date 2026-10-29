using System;

// Pure policy: shared by the desktop session and regression tests.
internal sealed class WorkPolicy
{
    internal readonly TimeSpan Duration;
    internal readonly bool PluggedInOnly;
    internal readonly int MinimumBattery;
    internal WorkPolicy(int minutes, bool pluggedInOnly, int minimumBattery)
    {
        if (minutes < 1 || minutes > 1440) throw new ArgumentOutOfRangeException("minutes");
        if (minimumBattery < 10 || minimumBattery > 90) throw new ArgumentOutOfRangeException("minimumBattery");
        Duration = TimeSpan.FromMinutes(minutes);
        PluggedInOnly = pluggedInOnly;
        MinimumBattery = minimumBattery;
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
}
